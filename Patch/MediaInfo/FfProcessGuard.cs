using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 拦截 Emby 自身的 ffprobe/ffmpeg 调用，仅允许插件在作用域内显式放行。
    /// </summary>
    public static class FfProcessGuard
    {
        public sealed class AllowanceContext
        {
            public long? ItemInternalId { get; set; }
            public string ItemPath { get; set; }
            public bool IsShortcut { get; set; }
            public bool AllowFfprocess { get; set; }
        }

        public sealed class AllowanceHandle
        {
            internal AllowanceHandle(AllowanceContext context)
            {
                Context = context;
            }

            internal AllowanceContext Context { get; }
        }

        private static readonly AsyncLocal<ScopeFrame> CurrentScope = new AsyncLocal<ScopeFrame>();
        private static Harmony harmony;
        private static MethodInfo runFfProcess;
        private static MethodInfo diagnosticsRunFfProcess;
        private static MethodInfo runExtraction;
        private static PropertyInfo standardOutput;
        private static PropertyInfo standardError;
        private static object emptyResult;
        private static ILogger logger;
        private static bool isEnabled;
        public static bool IsReady => harmony != null && runFfProcess != null && diagnosticsRunFfProcess != null &&
                                      runExtraction != null && emptyResult != null;

        public static void Initialize(ILogger pluginLogger, bool disableSystemFfprobe)
        {
            if (harmony != null) return;

            logger = pluginLogger;
            isEnabled = disableSystemFfprobe;

            try
            {
                var mediaEncoding = Assembly.Load("Emby.Server.MediaEncoding");
                if (mediaEncoding == null)
                {
                    PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 Emby.Server.MediaEncoding");
                    return;
                }

                var mediaProbeManager = mediaEncoding.GetType("Emby.Server.MediaEncoding.Probing.MediaProbeManager");
                if (mediaProbeManager == null)
                {
                    PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 MediaProbeManager 类型");
                    return;
                }

                runFfProcess = ResolveRunFfProcess(mediaEncoding, mediaProbeManager);
                diagnosticsRunFfProcess = ResolveDiagnosticsRunFfProcess(mediaEncoding);
                runExtraction = ResolveRunExtraction(mediaEncoding);

                var processRun = Assembly.Load("Emby.ProcessRun");
                var processResult = processRun?.GetType("Emby.ProcessRun.Common.ProcessResult");
                standardOutput = FindProperty(processResult, "StandardOutput");
                standardError = FindProperty(processResult, "StandardError");

                emptyResult = CreateEmptyResult(runFfProcess?.ReturnType);

                if (runFfProcess == null || emptyResult == null)
                {
                    PatchLog.InitFailed(logger, nameof(FfProcessGuard), "目标方法未找到或返回类型不支持");
                    return;
                }

                PatchLog.Patched(logger, nameof(FfProcessGuard), runFfProcess);

                harmony = new Harmony("mediainfokeeper.ffprobe");

                try
                {
                    harmony.Patch(runFfProcess,
                        prefix: new HarmonyMethod(typeof(FfProcessGuard), nameof(RunFfProcessPrefix)),
                        postfix: new HarmonyMethod(typeof(FfProcessGuard), nameof(RunFfProcessPostfix)));
                    harmony.Patch(diagnosticsRunFfProcess,
                        prefix: new HarmonyMethod(typeof(FfProcessGuard), nameof(DiagnosticsRunFfProcessPrefix)),
                        postfix: new HarmonyMethod(typeof(FfProcessGuard), nameof(RunFfProcessPostfix)));
                    PatchRunExtractionMethod(runExtraction);
                }
                catch (Exception patchEx)
                {
                    logger.Error("ffprobe/ffmpeg guard patch 失败");
                    logger.Error(patchEx.Message);
                    logger.Error(patchEx.ToString());
                    harmony = null;
                    isEnabled = false;
                    return;
                }
            }
            catch (Exception e)
            {
                logger.Error("ffprobe/ffmpeg guard 初始化失败");
                logger.Error(e.Message);
                logger.Error(e.ToString());
                harmony = null;
                isEnabled = false;
                logger.Warn("ffprobe/ffmpeg guard 初始化失败已禁用，ffprobe/ffmpeg 不再拦截");
            }
        }

        public static void Configure(bool disableSystemFfprobe)
        {
            isEnabled = disableSystemFfprobe;
        }

        /// <summary>
        /// 创建放行作用域，插件内部调用 ffprobe/ffmpeg 时使用。
        /// </summary>
        public static IDisposable Allow()
        {
            return new GuardScope(BeginAllow());
        }

        public static AllowanceHandle BeginAllow(AllowanceContext context = null)
        {
            var previous = CurrentScope.Value;
            var handle = new AllowanceHandle(context);
            CurrentScope.Value = new ScopeFrame(handle, previous);
            return handle;
        }

        public static bool HasExplicitAllowance()
        {
            var scope = CurrentScope.Value;
            while (scope != null)
            {
                if (scope.Handle?.Context == null)
                {
                    return true;
                }

                scope = scope.Previous;
            }

            return false;
        }

        public static void EndAllow(AllowanceHandle handle = null)
        {
            var current = CurrentScope.Value;
            if (current == null)
            {
                return;
            }

            if (handle == null || ReferenceEquals(current.Handle, handle))
            {
                CurrentScope.Value = current.Previous;
            }
        }

        private static bool RunFfProcessPrefix(object __instance, object __0, string __1, string __2,
            ref int __3, CancellationToken __4, ref object __result)
        {
            if (!isEnabled)
            {
                return true;
            }

            var inputHint = Regex.Match(__2 ?? string.Empty, @"-i\s+file:""[^""]+""").Value
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
            var runTypeText = __0?.ToString() ?? string.Empty;
            var isFfprobe = runTypeText.IndexOf("ffprobe", StringComparison.OrdinalIgnoreCase) >= 0;
            var isFfmpeg = runTypeText.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isFfprobe && !isFfmpeg)
            {
                return true;
            }

            var displayTarget = $"{(isFfmpeg ? "ffmpeg" : "ffprobe")} {inputHint}";

            var scope = CurrentScope.Value;
            if (scope != null)
            {
                var context = scope.Handle?.Context;
                if (context != null && !context.AllowFfprocess)
                {
                    logger?.Info($"拦截 {displayTarget}");
                    __result = emptyResult;
                    return false;
                }

                logger?.Info($"允许 {displayTarget}");
                __3 = Math.Max(__3, 60000);
                return true;
            }
            logger?.Info($"拦截 {displayTarget}");
            __result = emptyResult;
            return false;
        }

        private static bool DiagnosticsRunFfProcessPrefix(object __instance, object __0, string __1, string __2,
            ref TimeSpan __3, bool __4, CancellationToken __5, ref object __result)
        {
            if (!isEnabled)
            {
                return true;
            }

            var inputHint = Regex.Match(__2 ?? string.Empty, @"-i\s+file:""[^""]+""").Value
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
            var runTypeText = __0?.ToString() ?? string.Empty;
            var isFfprobe = runTypeText.IndexOf("ffprobe", StringComparison.OrdinalIgnoreCase) >= 0;
            var isFfmpeg = runTypeText.IndexOf("ffmpeg", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isFfprobe && !isFfmpeg)
            {
                return true;
            }

            var displayTarget = $"{(isFfmpeg ? "ffmpeg" : "ffprobe")} {inputHint}";

            var scope = CurrentScope.Value;
            if (scope != null)
            {
                var context = scope.Handle?.Context;
                if (context != null && !context.AllowFfprocess)
                {
                    logger?.Info($"拦截 {displayTarget}");
                    __result = emptyResult;
                    return false;
                }

                logger?.Info($"允许 {displayTarget}");
                if (__3 < TimeSpan.FromSeconds(60))
                {
                    __3 = TimeSpan.FromSeconds(60);
                }

                return true;
            }

            logger?.Info($"拦截 {displayTarget}");
            __result = emptyResult;
            return false;
        }

        private static bool RunExtractionPrefix(object __instance, string __0, ref object __result)
        {
            if (!isEnabled)
            {
                return true;
            }

            var displayTarget = $"ffmpeg -i file:\"{(__0 ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n")}\"";
            if (HasFfprocessAllowanceInCurrentScope())
            {
                logger?.Info($"允许 {displayTarget}");
                return true;
            }

            logger?.Info($"拦截 {displayTarget}");
            __result = Task.CompletedTask;
            return false;
        }

        private static void RunFfProcessPostfix(ref object __result)
        {
            if (__result is Task task)
            {
                var result = task.GetType().GetProperty("Result")?.GetValue(task);
                if (result == null) return;

                var stdout = standardOutput?.GetValue(result) as string;
                var stderr = standardError?.GetValue(result) as string;
                if (stdout == null || stderr == null) return;

                var trimmed = new string((stdout ?? string.Empty)
                    .Where(c => !char.IsWhiteSpace(c))
                    .ToArray());
                if (!string.Equals(trimmed, "{}", StringComparison.Ordinal)) return;

                var lines = stderr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;

                var message = lines[lines.Length - 1].Trim();
                if (!string.IsNullOrEmpty(message))
                {
                    logger.Error("ffprobe/ffmpeg 错误: " + message);
                }
            }
        }

        private static object CreateEmptyResult(Type returnType)
        {
            if (returnType == null) return null;

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                object payload = null;

                try
                {
                    payload = Activator.CreateInstance(resultType, nonPublic: true);
                    if (payload != null)
                    {
                        try { standardOutput?.SetValue(payload, "{}"); }
                        catch { /* best-effort stub */ }
                        try { standardError?.SetValue(payload, "ffprobe/ffmpeg 已被 MediaInfoKeeper 拦截"); }
                        catch { /* best-effort stub */ }
                    }
                }
                catch (Exception e)
                {
                    logger?.Debug(e.Message);
                    logger?.Debug(e.ToString());
                }

                var fromResult = typeof(Task).GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .First(m => m.Name == nameof(Task.FromResult) && m.IsGenericMethod)
                    .MakeGenericMethod(resultType);
                return fromResult.Invoke(null, new[] { payload });
            }

            return null;
        }

        private static MethodInfo ResolveRunFfProcess(Assembly mediaEncoding, Type mediaProbeManager)
        {
            try
            {
                var ffRunType = mediaEncoding.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfRunType");
                if (ffRunType == null)
                {
                    PatchLog.Candidates(
                        logger,
                        "FfprobeGuard.FfRunType",
                        "未找到类型 Emby.Server.MediaEncoding.Unified.Ffmpeg.FfRunType");
                    return null;
                }

                var assemblyVersion = mediaEncoding.GetName().Version;
                var exactProfile = new MethodSignatureProfile
                {
                    Name = "runffprocess-exact",
                    MethodName = "RunFfProcess",
                    BindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                   BindingFlags.NonPublic,
                    ParameterTypes = new[] { ffRunType, typeof(string), typeof(string), typeof(int), typeof(CancellationToken) }
                };

                return PatchMethodResolver.Resolve(
                    mediaProbeManager,
                    assemblyVersion,
                    exactProfile,
                    logger,
                    "FfprobeGuard.RunFfProcess");
            }
            catch
            {
                return null;
            }
        }

        private static MethodInfo ResolveDiagnosticsRunFfProcess(Assembly mediaEncoding)
        {
            try
            {
                var encodingDiagnostics = mediaEncoding.GetType("Emby.Server.MediaEncoding.Unified.Diagnostics.EncodingDiagnostics");
                if (encodingDiagnostics == null)
                {
                    PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 EncodingDiagnostics 类型");
                    return null;
                }

                var ffRunType = mediaEncoding.GetType("Emby.Server.MediaEncoding.Unified.Ffmpeg.FfRunType");
                if (ffRunType == null)
                {
                    PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 FfRunType 类型");
                    return null;
                }

                var assemblyVersion = mediaEncoding.GetName().Version;
                return PatchMethodResolver.Resolve(
                    encodingDiagnostics,
                    assemblyVersion,
                    new MethodSignatureProfile
                    {
                        Name = "diagnostics-runffprocess-exact",
                        MethodName = "RunFfProcess",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { ffRunType, typeof(string), typeof(string), typeof(TimeSpan), typeof(bool), typeof(CancellationToken) }
                    },
                    logger,
                    "FfprobeGuard.EncodingDiagnostics.RunFfProcess");
            }
            catch
            {
                return null;
            }
        }

        private static MethodInfo ResolveRunExtraction(Assembly mediaEncoding)
        {
            var imageExtractorBaseType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.ImageExtraction.ImageExtractorBase");
            if (imageExtractorBaseType == null)
            {
                PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 ImageExtractorBase 类型");
                return null;
            }

            var mediaBrowserModel = Assembly.Load("MediaBrowser.Model");
            var mediaContainersType = mediaBrowserModel?.GetType("MediaBrowser.Model.MediaInfo.MediaContainers");
            var mediaProtocolType = mediaBrowserModel?.GetType("MediaBrowser.Model.MediaInfo.MediaProtocol");
            var video3DFormatType = mediaBrowserModel?.GetType("MediaBrowser.Model.Entities.Video3DFormat");
            if (mediaContainersType == null || mediaProtocolType == null || video3DFormatType == null)
            {
                PatchLog.InitFailed(logger, nameof(FfProcessGuard), "未找到 ImageExtraction 相关依赖类型");
                return null;
            }

            var assemblyVersion = mediaEncoding.GetName().Version;
            return PatchMethodResolver.Resolve(
                imageExtractorBaseType,
                assemblyVersion,
                new MethodSignatureProfile
                {
                    Name = "run-extraction-exact",
                    MethodName = "RunExtraction",
                    BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    ParameterTypes = new[]
                    {
                        typeof(string),
                        typeof(System.Collections.Generic.IDictionary<string, string>),
                        typeof(Nullable<>).MakeGenericType(mediaContainersType),
                        typeof(MediaStream),
                        typeof(Nullable<>).MakeGenericType(mediaProtocolType),
                        typeof(int?),
                        typeof(Nullable<>).MakeGenericType(video3DFormatType),
                        typeof(TimeSpan?),
                        typeof(TimeSpan?),
                        typeof(string),
                        typeof(string),
                        typeof(int?),
                        typeof(bool),
                        typeof(CancellationToken)
                    },
                    ReturnType = typeof(Task)
                },
                logger,
                "FfprobeGuard.RunExtraction");
        }

        private static void PatchRunExtractionMethod(MethodInfo method)
        {
            if (method == null)
            {
                return;
            }

            PatchLog.Patched(logger, nameof(FfProcessGuard), method);
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(FfProcessGuard), nameof(RunExtractionPrefix)));
        }

        private static bool HasFfprocessAllowanceInCurrentScope()
        {
            var scope = CurrentScope.Value;
            while (scope != null)
            {
                if (scope.Handle?.Context == null || scope.Handle.Context.AllowFfprocess)
                {
                    return true;
                }

                scope = scope.Previous;
            }

            return false;
        }

        private static PropertyInfo FindProperty(Type type, string propertyName)
        {
            var property = type?.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Static |
                                                         BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null)
            {
                LogPropertyCandidates(type, propertyName);
            }

            return property;
        }

        private static void LogPropertyCandidates(Type type, string propertyName)
        {
            try
            {
                var candidates = type?.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                                     BindingFlags.NonPublic)
                    .Where(p => p.Name == propertyName)
                    .Select(p => $"{p.PropertyType?.Name} {p.Name}");

                PatchLog.Candidates(
                    logger,
                    string.Format("{0}.{1}", type?.FullName ?? "<null>", propertyName ?? "<null>"),
                    string.Join("; ", candidates ?? Enumerable.Empty<string>()));
            }
            catch (Exception e)
            {
                logger?.Debug(e.Message);
                logger?.Debug(e.StackTrace);
            }
        }

        private sealed class ScopeFrame
        {
            public ScopeFrame(AllowanceHandle handle, ScopeFrame previous)
            {
                Handle = handle;
                Previous = previous;
            }

            public AllowanceHandle Handle { get; }
            public ScopeFrame Previous { get; }
        }

        private sealed class GuardScope : IDisposable
        {
            private readonly AllowanceHandle handle;

            public GuardScope(AllowanceHandle handle)
            {
                this.handle = handle;
            }

            public void Dispose()
            {
                EndAllow(this.handle);
            }
        }
    }
}
