using System;
using System.Reflection;
using System.Linq;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    internal static class SystemLog
    {
        private static int suppressNextGlobalCount;
        private static Harmony harmony;
        private static ILogger pluginLogger;
        private static MethodInfo namedLoggerLog;
        private static bool isEnabled = true;
        private static bool isPatched;
        public static bool IsReady => harmony != null && isPatched;

        public static void Initialize(ILogger logger, bool enable)
        {
            if (harmony != null)
            {
                Configure(enable);
                return;
            }

            pluginLogger = logger;
            isEnabled = enable;

            try
            {
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var namedLoggerType = embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Logging.NamedLogger");
                namedLoggerLog = PatchMethodResolver.Resolve(
                    namedLoggerType,
                    embyServerImplementationsAssembly.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "namedlogger-log-exact",
                        MethodName = "Log",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(LogSeverity), typeof(string), typeof(object[]) },
                        ReturnType = typeof(void)
                    },
                    pluginLogger,
                    "SystemLog.NamedLogger.Log");

                if (namedLoggerLog == null)
                {
                    InitFailed(pluginLogger, nameof(SystemLog), "NamedLogger.Log 未找到");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.log");
                Patch();
            }
            catch (Exception ex)
            {
                pluginLogger?.Error("Log patch 初始化失败。");
                pluginLogger?.Error(ex.Message);
                pluginLogger?.Error(ex.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;

            if (harmony == null)
            {
                return;
            }

            Patch();
        }

        /// <summary>
        /// 临时抑制接下来通过 Log 输出的日志。
        /// </summary>
        public static void SuppressNext(int count = 1)
        {
            if (count <= 0)
            {
                return;
            }

            Interlocked.Add(ref suppressNextGlobalCount, count);
        }

        private static bool TryConsumeSuppression()
        {
            while (true)
            {
                var current = Volatile.Read(ref suppressNextGlobalCount);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref suppressNextGlobalCount, current - 1, current) == current)
                {
                    return true;
                }
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || namedLoggerLog == null)
            {
                return;
            }

            harmony.Patch(namedLoggerLog,
                prefix: new HarmonyMethod(typeof(SystemLog), nameof(NamedLoggerLogPrefix)));
            Patched(pluginLogger, nameof(SystemLog), namedLoggerLog);
            isPatched = true;
        }

        [HarmonyPrefix]
        private static bool NamedLoggerLogPrefix()
        {
            if (!isEnabled)
            {
                return true;
            }

            return !TryConsumeSuppression();
        }

        public static void Waiting(ILogger logger, string module, string dependency, bool enabled)
        {
            if (TryConsumeSuppression()) return;
            logger?.Info(
                "补丁等待：模块={0}，依赖={1}，启用={2}",
                module ?? "unknown",
                dependency ?? "unknown",
                enabled);
        }

        public static void InitFailed(ILogger logger, string module, string reason)
        {
            if (TryConsumeSuppression()) return;
            logger?.Warn(
                "补丁初始化失败：模块={0}，原因={1}",
                module ?? "unknown",
                string.IsNullOrWhiteSpace(reason) ? "unknown" : reason);
        }

        public static void ResolveHit(
            ILogger logger,
            string context,
            string level,
            string profile,
            string method,
            string dllVersion)
        {
            if (TryConsumeSuppression()) return;
            logger?.Debug(
                "补丁解析：模块={0}，级别={1}，配置={2}，dll版本={3}，命中={4}",
                context ?? "unknown",
                level ?? "unknown",
                profile ?? "unknown",
                dllVersion ?? "<unknown>",
                method ?? "unknown");
        }

        public static void ResolveFailed(
            ILogger logger,
            string context,
            string level,
            string profile,
            string type,
            string asmVersion)
        {
            if (TryConsumeSuppression()) return;
            logger?.Warn(
                "补丁解析失败：模块={0}，级别={1}，配置={2}，类型={3}，dll版本={4}",
                context ?? "unknown",
                level ?? "unknown",
                profile ?? "unknown",
                type ?? "<null>",
                asmVersion ?? "<unknown>");
        }

        public static void Patched(ILogger logger, string module, MethodInfo method)
        {
            if (method == null)
            {
                Patched(logger, module, "<null>", null);
                return;
            }

            var signature = string.Format(
                "{0}.{1}({2}) -> {3}",
                method.DeclaringType?.FullName ?? "<unknown>",
                method.Name,
                string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name)),
                method.ReturnType?.Name ?? "<void>");
            var dllVersion = method.Module?.Assembly?.GetName()?.Version?.ToString();
            Patched(logger, module, signature, dllVersion);
        }

        public static void Patched(ILogger logger, string module, string method)
        {
            Patched(logger, module, method, null);
        }

        public static void Patched(ILogger logger, string module, string method, string dllVersion)
        {
            if (TryConsumeSuppression()) return;
            logger?.Debug(
                "补丁安装：模块={0}，dll版本={1}，方法={2}",
                module ?? "unknown",
                dllVersion ?? "<unknown>",
                method ?? "unknown");
        }

        public static void Candidates(ILogger logger, string module, string candidates)
        {
            if (TryConsumeSuppression()) return;
            logger?.Debug(
                "补丁候选：模块={0}，候选={1}",
                module ?? "unknown",
                candidates ?? string.Empty);
        }
    }
}
