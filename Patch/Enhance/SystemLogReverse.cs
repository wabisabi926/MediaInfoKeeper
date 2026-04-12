using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using MediaInfoKeeper.Common;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 将 Emby 系统日志接口返回的日志内容改为倒序输出，便于直接查看最新日志。
    /// </summary>
    internal static class SystemLogReverse
    {
        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;
        private static MethodInfo getLogFileMethod;
        private static MethodInfo getLogLinesMethod;

        public static bool IsReady => harmony != null && isPatched;

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            if (harmony != null)
            {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try
            {
                var embyApiAssembly = Assembly.Load("Emby.Api");
                var embyApiVersion = embyApiAssembly?.GetName().Version;
                var systemServiceType = embyApiAssembly?.GetType("Emby.Api.System.SystemService", false);
                var getLogFileType = embyApiAssembly?.GetType("Emby.Api.System.GetLogFile", false);
                var getLogLinesType = embyApiAssembly?.GetType("Emby.Api.System.GetLogLines", false);

                if (systemServiceType == null || getLogFileType == null || getLogLinesType == null)
                {
                    PatchLog.InitFailed(logger, nameof(SystemLogReverse), "SystemService 或日志请求类型缺失");
                    return;
                }

                getLogFileMethod = PatchMethodResolver.Resolve(
                    systemServiceType,
                    embyApiVersion,
                    new MethodSignatureProfile
                    {
                        Name = "systemservice-get-logfile-exact",
                        MethodName = "Get",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[] { getLogFileType },
                        ReturnType = typeof(Task<object>)
                    },
                    logger,
                    "SystemLogReverse.SystemService.Get(GetLogFile)");

                getLogLinesMethod = PatchMethodResolver.Resolve(
                    systemServiceType,
                    embyApiVersion,
                    new MethodSignatureProfile
                    {
                        Name = "systemservice-get-loglines-exact",
                        MethodName = "Get",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[] { getLogLinesType },
                        ReturnType = typeof(Task<object>)
                    },
                    logger,
                    "SystemLogReverse.SystemService.Get(GetLogLines)");

                if (getLogFileMethod == null || getLogLinesMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(SystemLogReverse), "SystemService 日志方法未找到");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.systemlogreverse");
                Patch();
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(SystemLogReverse), ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                getLogFileMethod = null;
                getLogLinesMethod = null;
                isPatched = false;
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || getLogFileMethod == null || getLogLinesMethod == null)
            {
                return;
            }

            harmony.Patch(
                getLogFileMethod,
                prefix: new HarmonyMethod(typeof(SystemLogReverse), nameof(GetLogFilePrefix)));
            harmony.Patch(
                getLogLinesMethod,
                prefix: new HarmonyMethod(typeof(SystemLogReverse), nameof(GetLogLinesPrefix)));

            PatchLog.Patched(logger, nameof(SystemLogReverse), getLogFileMethod);
            PatchLog.Patched(logger, nameof(SystemLogReverse), getLogLinesMethod);
            isPatched = true;
        }

        [HarmonyPrefix]
        private static bool GetLogFilePrefix(object __instance, object __0, ref Task<object> __result)
        {
            if (!isEnabled)
            {
                return true;
            }

            var name = GetPropertyValue<string>(__0, "Name");
            if (!ShouldReverse(name))
            {
                return true;
            }

            try
            {
                __result = BuildLogFileResultAsync(__instance, __0, name);
                return false;
            }
            catch (Exception ex)
            {
                logger?.Warn("SystemLogReverse logfile failed: {0}", ex.Message);
                return true;
            }
        }

        [HarmonyPrefix]
        private static bool GetLogLinesPrefix(object __instance, object __0, ref Task<object> __result)
        {
            if (!isEnabled)
            {
                return true;
            }

            var name = GetPropertyValue<string>(__0, "Name");
            if (!ShouldReverse(name))
            {
                return true;
            }

            try
            {
                __result = BuildLogLinesResultAsync(__instance, __0, name);
                return false;
            }
            catch (Exception ex)
            {
                logger?.Warn("SystemLogReverse log lines failed: {0}", ex.Message);
                return true;
            }
        }

        private static async Task<object> BuildLogFileResultAsync(object serviceInstance, object request, string name)
        {
            var requestContext = GetPropertyValue<IRequest>(serviceInstance, "Request");
            var resultFactory = GetPropertyValue<IHttpResultFactory>(serviceInstance, "ResultFactory");
            var fileSystem = GetFieldValue<IFileSystem>(serviceInstance, "_fileSystem");
            var sanitationManager = GetFieldValue<object>(serviceInstance, "_sanitationManager");
            var logDirectoryPath = GetLogDirectoryPath(serviceInstance);
            var sanitize = GetPropertyValue<bool>(request, "Sanitize");
            var setFilename = GetPropertyValue<bool>(request, "SetFilename");

            if (requestContext == null || resultFactory == null || fileSystem == null || string.IsNullOrWhiteSpace(logDirectoryPath))
            {
                return await Task.FromResult<object>(null).ConfigureAwait(false);
            }

            var file = GetLogFileMetadata(fileSystem, logDirectoryPath, name);
            var content = await ReadReversedLogTextAsync(fileSystem, sanitationManager, file.FullName, file.LastWriteTimeUtc, sanitize)
                .ConfigureAwait(false);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (setFilename)
            {
                var safeName = (Path.GetFileName(file.FullName) ?? string.Empty).Replace("\"", string.Empty);
                BaseApiService.SetContentDisposition(headers, safeName);
            }

            return resultFactory.GetResult(
                requestContext,
                new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
                "text/plain; charset=UTF-8",
                headers);
        }

        private static async Task<object> BuildLogLinesResultAsync(object serviceInstance, object request, string name)
        {
            var fileSystem = GetFieldValue<IFileSystem>(serviceInstance, "_fileSystem");
            var sanitationManager = GetFieldValue<object>(serviceInstance, "_sanitationManager");
            var logDirectoryPath = GetLogDirectoryPath(serviceInstance);
            var sanitize = GetPropertyValue<bool>(request, "Sanitize");
            var startIndex = Math.Max(0, GetPropertyValue<int>(request, "StartIndex"));
            var limit = Math.Max(0, GetPropertyValue<int>(request, "Limit"));
            var enableTotalRecordCount = GetPropertyValue<bool>(request, "EnableTotalRecordCount");

            if (fileSystem == null || string.IsNullOrWhiteSpace(logDirectoryPath))
            {
                return new QueryResult<string>
                {
                    Items = Array.Empty<string>(),
                    TotalRecordCount = 0
                };
            }

            var file = GetLogFileMetadata(fileSystem, logDirectoryPath, name);
            var lines = await ReadReversedLogLinesAsync(fileSystem, sanitationManager, file.FullName, file.LastWriteTimeUtc, sanitize)
                .ConfigureAwait(false);
            var items = lines.Skip(startIndex).Take(limit).ToArray();

            return new QueryResult<string>
            {
                Items = items,
                TotalRecordCount = enableTotalRecordCount ? lines.Count : 0
            };
        }

        private static FileSystemMetadata GetLogFileMetadata(IFileSystem fileSystem, string logDirectoryPath, string name)
        {
            return fileSystem.GetFiles(logDirectoryPath)
                .First(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<string> ReadReversedLogTextAsync(
            IFileSystem fileSystem,
            object sanitationManager,
            string fullName,
            DateTimeOffset lastWriteTime,
            bool sanitize)
        {
            var lines = await ReadReversedLogLinesAsync(fileSystem, sanitationManager, fullName, lastWriteTime, sanitize)
                .ConfigureAwait(false);
            return string.Join(Environment.NewLine, lines);
        }

        private static async Task<List<string>> ReadReversedLogLinesAsync(
            IFileSystem fileSystem,
            object sanitationManager,
            string fullName,
            DateTimeOffset lastWriteTime,
            bool sanitize)
        {
            var shareMode = lastWriteTime < ConfiguredDateTime.NowOffset.AddHours(-1.0)
                ? FileShareMode.Read
                : FileShareMode.ReadWrite;
            var lines = new List<string>();

            using (var stream = fileSystem.GetFileStream(
                fullName,
                FileOpenMode.Open,
                FileAccessMode.Read,
                shareMode,
                FileOpenOptions.SequentialScan | FileOpenOptions.Asynchronous))
            {
                using (var streamToRead = sanitize ? SanitizeStream(sanitationManager, stream) : stream)
                using (var reader = new StreamReader(streamToRead, Encoding.UTF8, true, 4096, leaveOpen: false))
                {
                    while (true)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }

                        lines.Add(line);
                    }
                }
            }

            lines.Reverse();
            return lines;
        }

        private static Stream SanitizeStream(object sanitationManager, Stream input)
        {
            if (sanitationManager == null)
            {
                return input;
            }

            return sanitationManager.GetType()
                       .GetMethod("SanitizeStream", BindingFlags.Instance | BindingFlags.Public)
                       ?.Invoke(sanitationManager, new object[] { input }) as Stream
                   ?? input;
        }

        private static string GetLogDirectoryPath(object serviceInstance)
        {
            var appPaths = GetFieldValue<object>(serviceInstance, "_appPaths");
            return appPaths?.GetType()
                .GetProperty("LogDirectoryPath", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(appPaths) as string;
        }

        private static bool ShouldReverse(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(name);
        }

        private static T GetFieldValue<T>(object instance, string fieldName) where T : class
        {
            return instance?.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(instance) as T;
        }

        private static T GetPropertyValue<T>(object instance, string propertyName)
        {
            var value = instance?.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance);

            if (value == null)
            {
                return default(T);
            }

            return value is T typedValue ? typedValue : default(T);
        }
    }
}
