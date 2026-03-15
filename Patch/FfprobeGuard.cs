using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 拦截 Emby 内置 ffprobe，只允许插件显式放行。
    /// </summary>
    public static class FfprobeGuard
    {
        private static readonly Regex InputArgRegex = new Regex(@"(?:^|\s)-i\s+(?:""(?<path>[^""]+)""|'(?<path>[^']+)'|(?<path>\S+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly AsyncLocal<int> GuardCount = new AsyncLocal<int>();
        private static Harmony harmony;
        private static MethodInfo runFfProcess;
        private static PropertyInfo standardOutput;
        private static PropertyInfo standardError;
        private static object emptyResult;
        private static ILogger logger;
        private static bool isEnabled;
        public static bool IsReady => harmony != null && runFfProcess != null && emptyResult != null;

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
                    PatchLog.InitFailed(logger, nameof(FfprobeGuard), "未找到 Emby.Server.MediaEncoding");
                    return;
                }

                var mediaProbeManager = mediaEncoding.GetType("Emby.Server.MediaEncoding.Probing.MediaProbeManager");
                if (mediaProbeManager == null)
                {
                    PatchLog.InitFailed(logger, nameof(FfprobeGuard), "未找到 MediaProbeManager 类型");
                    return;
                }

                runFfProcess = ResolveRunFfProcess(mediaEncoding, mediaProbeManager);

                var processRun = Assembly.Load("Emby.ProcessRun");
                var processResult = processRun?.GetType("Emby.ProcessRun.Common.ProcessResult");
                standardOutput = FindProperty(processResult, "StandardOutput");
                standardError = FindProperty(processResult, "StandardError");

                emptyResult = CreateEmptyResult(runFfProcess?.ReturnType);

                if (runFfProcess == null || emptyResult == null)
                {
                    PatchLog.InitFailed(logger, nameof(FfprobeGuard), "目标方法未找到或返回类型不支持");
                    return;
                }

                PatchLog.Patched(logger, nameof(FfprobeGuard), runFfProcess);

                harmony = new Harmony("mediainfokeeper.ffprobe");

                try
                {
                    harmony.Patch(runFfProcess,
                        prefix: new HarmonyMethod(typeof(FfprobeGuard), nameof(RunFfProcessPrefix)),
                        postfix: new HarmonyMethod(typeof(FfprobeGuard), nameof(RunFfProcessPostfix)));
                }
                catch (Exception patchEx)
                {
                    logger.Error("ffprobe guard patch 失败");
                    logger.Error(patchEx.Message);
                    logger.Error(patchEx.ToString());
                    harmony = null;
                    isEnabled = false;
                    return;
                }
            }
            catch (Exception e)
            {
                logger.Error("ffprobe guard 初始化失败");
                logger.Error(e.Message);
                logger.Error(e.ToString());
                harmony = null;
                isEnabled = false;
                logger.Warn("ffprobe guard 初始化失败已禁用，ffprobe 不再拦截");
            }
        }

        public static void Configure(bool disableSystemFfprobe)
        {
            isEnabled = disableSystemFfprobe;
        }

        /// <summary>
        /// 创建放行作用域，插件内部调用 ffprobe 时使用。
        /// </summary>
        public static IDisposable Allow()
        {
            GuardCount.Value = GuardCount.Value + 1;
            return new GuardScope();
        }

        public static void BeginAllow()
        {
            GuardCount.Value = GuardCount.Value + 1;
        }

        public static void EndAllow()
        {
            if (GuardCount.Value > 0)
            {
                GuardCount.Value--;
            }
        }

        private static bool RunFfProcessPrefix(object __instance, object __0, string __1, string __2,
            ref int __3, CancellationToken __4, ref object __result)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (GuardCount.Value > 0)
            {
                logger?.Info($"""允许 ffprobe {Regex.Match(__2, @"-i\s+file:""[^""]+""").Value}""");
                SystemLog.SuppressNext();
                __3 = Math.Max(__3, 60000);
                return true;
            }
            logger?.Info($"""拦截 ffprobe {Regex.Match(__2, @"-i\s+file:""[^""]+""").Value}""");
            // 抑制显示App Error ffprobe Error
            SystemLog.SuppressNext();
            __result = emptyResult;
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
                    logger.Error("ffprobe 错误: " + message);
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
                        try { standardError?.SetValue(payload, "ffprobe 已被 MediaInfoKeeper 拦截"); }
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

        private sealed class GuardScope : IDisposable
        {
            public void Dispose()
            {
                EndAllow();
            }
        }
    }
}
