using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 按临时抑制标记过滤 NamedLogger 日志输出，并将特定噪音错误降级为 Debug。
    /// </summary>
    internal static class SystemLog
    {
        private static readonly AsyncLocal<int> suppressNextScopeCount = new AsyncLocal<int>();
        private static Harmony harmony;
        private static ILogger pluginLogger;
        private static MethodInfo namedLoggerLog;
        private static MethodInfo namedLoggerLogMemory;
        private static MethodInfo namedLoggerLogException;
        private static PropertyInfo namedLoggerNameProperty;
        private static bool isEnabled = true;
        private static bool isPatched;
        private static HashSet<string> loggerNameBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static bool IsReady => harmony != null && isPatched;

        public static void Initialize(ILogger logger, bool enable, string loggerNameBlacklistRaw)
        {
            if (harmony != null)
            {
                Configure(enable, loggerNameBlacklistRaw);
                return;
            }

            pluginLogger = logger;
            isEnabled = enable;
            UpdateLoggerNameBlacklist(loggerNameBlacklistRaw);

            try
            {
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var namedLoggerType = embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Logging.NamedLogger");
                namedLoggerNameProperty = namedLoggerType?.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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
                namedLoggerLogMemory = PatchMethodResolver.Resolve(
                    namedLoggerType,
                    embyServerImplementationsAssembly.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "namedlogger-log-memory-exact",
                        MethodName = "Log",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(LogSeverity), typeof(ReadOnlyMemory<char>) },
                        ReturnType = typeof(void)
                    },
                    pluginLogger,
                    "SystemLog.NamedLogger.Log(ReadOnlyMemory)");
                namedLoggerLogException = PatchMethodResolver.Resolve(
                    namedLoggerType,
                    embyServerImplementationsAssembly.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "namedlogger-logexception-exact",
                        MethodName = "LogException",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(LogSeverity), typeof(string), typeof(Exception), typeof(object[]) },
                        ReturnType = typeof(void)
                    },
                    pluginLogger,
                    "SystemLog.NamedLogger.LogException");

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

        public static void Configure(bool enable, string loggerNameBlacklistRaw)
        {
            isEnabled = enable;
            UpdateLoggerNameBlacklist(loggerNameBlacklistRaw);

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

            suppressNextScopeCount.Value += count;
        }

        private static bool TryConsumeSuppression()
        {
            var current = suppressNextScopeCount.Value;
            if (current <= 0)
            {
                return false;
            }

            suppressNextScopeCount.Value = current - 1;
            return true;
        }

        private static bool IsBlockedMessage(LogSeverity severity, string message)
        {
            if (severity != LogSeverity.Error || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.StartsWith("Error in ffprobe", StringComparison.OrdinalIgnoreCase) ||
                   message.StartsWith("Error in Image Capture dynamic image provider", StringComparison.OrdinalIgnoreCase) ||
                   message.StartsWith("Thumbnail-Filter extraction failed, will attempt standard way.", StringComparison.OrdinalIgnoreCase) ||
                   message.StartsWith("ffmpeg image extraction failed for", StringComparison.OrdinalIgnoreCase);
        }

        private static void UpdateLoggerNameBlacklist(string raw)
        {
            loggerNameBlacklist = new HashSet<string>(
                (raw ?? string.Empty)
                    .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item)),
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsLoggerNameBlocked(object loggerInstance)
        {
            if (loggerNameBlacklist == null || loggerNameBlacklist.Count == 0 || loggerInstance == null || namedLoggerNameProperty == null)
            {
                return false;
            }

            try
            {
                var loggerName = namedLoggerNameProperty.GetValue(loggerInstance) as string;
                if (string.IsNullOrWhiteSpace(loggerName))
                {
                    return false;
                }

                loggerName = loggerName.Trim();
                return loggerNameBlacklist.Any(rule =>
                    !string.IsNullOrWhiteSpace(rule) &&
                    (string.Equals(loggerName, rule, StringComparison.OrdinalIgnoreCase) ||
                     loggerName.StartsWith(rule, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                pluginLogger?.Debug("SystemLog 读取 logger.Name 失败: {0}", ex.Message);
                return false;
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
            if (namedLoggerLogMemory != null)
            {
                harmony.Patch(namedLoggerLogMemory,
                    prefix: new HarmonyMethod(typeof(SystemLog), nameof(NamedLoggerMemoryLogPrefix)));
            }

            if (namedLoggerLogException != null)
            {
                harmony.Patch(namedLoggerLogException,
                    prefix: new HarmonyMethod(typeof(SystemLog), nameof(NamedLoggerLogExceptionPrefix)));
            }

            Patched(pluginLogger, nameof(SystemLog), namedLoggerLog);
            isPatched = true;
        }

        [HarmonyPrefix]
        private static bool NamedLoggerLogPrefix(object __instance, ref LogSeverity __0, string __1)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (TryConsumeSuppression())
            {
                return false;
            }

            if (IsBlockedMessage(__0, __1))
            {
                __0 = LogSeverity.Debug;
            }

            return !IsLoggerNameBlocked(__instance);
        }

        [HarmonyPrefix]
        private static bool NamedLoggerMemoryLogPrefix(object __instance)
        {
            if (!isEnabled)
            {
                return true;
            }

            return !TryConsumeSuppression() && !IsLoggerNameBlocked(__instance);
        }

        [HarmonyPrefix]
        private static bool NamedLoggerLogExceptionPrefix(object __instance, ref LogSeverity __0, string __1)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (TryConsumeSuppression())
            {
                return false;
            }

            if (IsBlockedMessage(__0, __1))
            {
                __0 = LogSeverity.Debug;
            }

            return !IsLoggerNameBlocked(__instance);
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
