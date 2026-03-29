using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Services;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 拦截仪表盘资源请求，按配置重写首页和播放器脚本内容。
    /// </summary>
    public static class DashboardResourcePatch
    {
        private static readonly IReadOnlyDictionary<string, Func<string, string>> RewriteHandlers =
            new Dictionary<string, Func<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["index.html"] = RewriteDashboardIndexHtml,
            ["modules/htmlvideoplayer/plugin.js"] = RewriteHtmlVideoPlayerPlugin
        };

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool enableCrossOriginPatch;
        private static bool enableDanmakuJs;
        private static MethodInfo getMethod;
        private static MethodInfo getContentFactoryMethod;
        private static MethodInfo getResourceStreamMethod;
        private static MethodInfo findWebResourceMethod;
        private static bool isPatched;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enableCrossOrigin, bool enableDanmaku)
        {
            if (harmony != null)
            {
                Configure(enableCrossOrigin, enableDanmaku);
                return;
            }

            logger = pluginLogger;
            enableCrossOriginPatch = enableCrossOrigin;
            enableDanmakuJs = enableDanmaku;
            isEnabled = enableCrossOriginPatch || enableDanmakuJs;

            try
            {
                var embyWebAssembly = Assembly.Load("Emby.Web");
                var embyWebVersion = embyWebAssembly?.GetName().Version;
                var webAppServiceType = embyWebAssembly?.GetType("Emby.Web.Api.WebAppService", false);
                var getDashboardResourceType = embyWebAssembly?.GetType("Emby.Web.Api.GetDashboardResource", false);

                if (webAppServiceType == null || getDashboardResourceType == null)
                {
                    PatchLog.InitFailed(logger, nameof(DashboardResourcePatch), "WebAppService 或 GetDashboardResource 类型缺失");
                    return;
                }

                getMethod = PatchMethodResolver.Resolve(
                    webAppServiceType,
                    embyWebVersion,
                    new MethodSignatureProfile
                    {
                        Name = "webappservice-get-dashboardresource-exact",
                        MethodName = "Get",
                        BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                        IsStatic = false,
                        ParameterTypes = new[] { getDashboardResourceType },
                        ReturnType = typeof(Task<object>)
                    },
                    logger,
                    "DashboardResourcePatch.Get");

                getContentFactoryMethod = PatchMethodResolver.Resolve(
                    webAppServiceType,
                    embyWebVersion,
                    new MethodSignatureProfile
                    {
                        Name = "webappservice-getcontentfactory-for-dashboardpatch",
                        MethodName = "GetContentFactory",
                        BindingFlags = BindingFlags.NonPublic | BindingFlags.Instance,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(string) }
                    },
                    logger,
                    "DashboardResourcePatch.GetContentFactory");

                getResourceStreamMethod = PatchMethodResolver.Resolve(
                    webAppServiceType,
                    embyWebVersion,
                    new MethodSignatureProfile
                    {
                        Name = "webappservice-getresourcestream-for-dashboardpatch",
                        MethodName = "GetResourceStream",
                        BindingFlags = BindingFlags.NonPublic | BindingFlags.Instance,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(string),
                            typeof(string),
                            typeof(string),
                            typeof(long)
                        }
                    },
                    logger,
                    "DashboardResourcePatch.GetResourceStream");

                findWebResourceMethod = PatchMethodResolver.Resolve(
                    webAppServiceType,
                    embyWebVersion,
                    new MethodSignatureProfile
                    {
                        Name = "webappservice-findwebresource-for-dashboardpatch",
                        MethodName = "FindWebResource",
                        BindingFlags = BindingFlags.NonPublic | BindingFlags.Static,
                        IsStatic = true,
                        ParameterTypes = new[]
                        {
                            typeof(string),
                            typeof(string[])
                        },
                        ReturnType = typeof(string)
                    },
                    logger,
                    "DashboardResourcePatch.FindWebResource");

                if (getMethod == null || getContentFactoryMethod == null || getResourceStreamMethod == null ||
                    findWebResourceMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(DashboardResourcePatch), "WebAppService 目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.dashboardresource");
                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(DashboardResourcePatch), ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                getMethod = null;
                getContentFactoryMethod = null;
                getResourceStreamMethod = null;
                findWebResourceMethod = null;
                isPatched = false;
            }
        }

        public static void Configure(bool enableCrossOrigin, bool enableDanmaku)
        {
            enableCrossOriginPatch = enableCrossOrigin;
            enableDanmakuJs = enableDanmaku;
            isEnabled = enableCrossOriginPatch || enableDanmakuJs;

            if (harmony == null)
            {
                return;
            }

            if (isEnabled)
            {
                Patch();
            }
            else
            {
                Unpatch();
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || getMethod == null)
            {
                return;
            }

            harmony.Patch(
                getMethod,
                prefix: new HarmonyMethod(typeof(DashboardResourcePatch), nameof(GetPrefix)));
            PatchLog.Patched(logger, nameof(DashboardResourcePatch), getMethod);

            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || getMethod == null)
            {
                return;
            }

            harmony.Unpatch(getMethod, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.Normal)]
        private static bool GetPrefix(object __instance, object __0, ref Task<object> __result)
        {
            var request = __0;
            var path = request?.GetType()
                .GetProperty("ResourceName", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(request) as string;
            if (string.IsNullOrWhiteSpace(path) || !RewriteHandlers.ContainsKey(path))
            {
                return true;
            }

            logger?.Debug("DashboardResourcePatch target hit: {0}", path);
            
            try
            {
                var requestContext = GetPropertyValue<IRequest>(__instance, "Request");
                var configurationManager = GetPropertyValue<object>(__instance, "ConfigurationManager");
                var configuration = configurationManager?.GetType()
                    .GetProperty("Configuration", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(configurationManager);
                var appHost = GetPropertyValue<object>(__instance, "ApplicationHost");
                var resultFactory = GetFieldValue<IHttpResultFactory>(__instance, "_resultFactory");
                var dashboardUiPath = __instance.GetType()
                    .GetProperty("DashboardUIPath", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(__instance) as string;
                var localizationCulture = configuration?.GetType()
                    .GetProperty("UICulture", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(configuration) as string;
                var dashboardSourcePath = configuration?.GetType()
                    .GetProperty("DashboardSourcePath", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(configuration) as string;
                var appVersion = appHost?.GetType()
                    .GetProperty("ApplicationVersion", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(appHost)
                    ?.ToString();

                if (requestContext == null || resultFactory == null || string.IsNullOrWhiteSpace(dashboardUiPath))
                {
                    return true;
                }

                var contentFactory = BuildContentFactory(
                    __instance,
                    dashboardSourcePath,
                    dashboardUiPath,
                    path,
                    localizationCulture);
                if (contentFactory == null)
                {
                    return true;
                }

                __result = resultFactory.GetStaticResult(
                    requestContext,
                    BaseExtensions.GetMD5((appVersion ?? string.Empty) + (localizationCulture ?? string.Empty) + path),
                    null,
                    TimeSpan.FromDays(365),
                    MimeTypes.GetMimeType(path),
                    contentFactory,
                    null,
                    false);
                return false;
            }
            catch (Exception ex)
            {
                logger?.Warn("DashboardResourcePatch runtime failed: path={0}, error={1}", path, ex.Message);
                return true;
            }
        }

        private static Func<long, long, CancellationToken, Task<StreamHandler>> BuildContentFactory(
            object webAppService,
            string dashboardSourcePath,
            string basePath,
            string path,
            string localizationCulture)
        {
            if (string.IsNullOrWhiteSpace(dashboardSourcePath) &&
                path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                var resources = webAppService.GetType().Assembly.GetManifestResourceNames();
                var resourceName = findWebResourceMethod.Invoke(null, new object[] { path, resources }) as string;
                if (!string.IsNullOrWhiteSpace(resourceName))
                {
                    var originalFactory = getContentFactoryMethod.Invoke(webAppService, new object[] { resourceName }) as
                        Func<long, long, CancellationToken, Task<StreamHandler>>;
                    if (originalFactory != null)
                    {
                        return RewriteFactory(originalFactory, path);
                    }
                }
            }

            return (offset, length, cancellationToken) =>
            {
                var task = getResourceStreamMethod.Invoke(
                    webAppService,
                    new object[] { basePath, path, localizationCulture, offset }) as Task<StreamHandler>;
                return RewriteTaskAsync(task, path);
            };
        }

        private static Func<long, long, CancellationToken, Task<StreamHandler>> RewriteFactory(
            Func<long, long, CancellationToken, Task<StreamHandler>> originalFactory,
            string targetName)
        {
            return (offset, length, cancellationToken) =>
            {
                var task = originalFactory(offset, length, cancellationToken);
                return RewriteTaskAsync(task, targetName);
            };
        }

        private static async Task<StreamHandler> RewriteTaskAsync(Task<StreamHandler> originalTask, string targetName)
        {
            var handler = await originalTask.ConfigureAwait(false);
            if (handler?.Stream == null)
            {
                return handler;
            }

            using var reader = new StreamReader(handler.Stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            var originalContent = await reader.ReadToEndAsync().ConfigureAwait(false);
            var rewrittenContent = RewriteContent(targetName, originalContent);

            handler.Stream = new MemoryStream(Encoding.UTF8.GetBytes(rewrittenContent), writable: false);
            handler.Length = rewrittenContent.Length;
            handler.TotalLength = rewrittenContent.Length;
            return handler;
        }

        private static string RewriteContent(string targetName, string content)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrWhiteSpace(targetName))
            {
                return content;
            }

            if (!RewriteHandlers.TryGetValue(targetName, out var handler) || handler == null)
            {
                return content;
            }

            return handler(content);
        }
        private static string RewriteHtmlVideoPlayerPlugin(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            if (!enableCrossOriginPatch)
            {
                return content;
            }

            logger?.Info("DashboardResourcePatch: 移除 crossOrigin");
            
            return content.Replace(
                "&&(elem.crossOrigin=initialSubtitleStream)",
                string.Empty,
                StringComparison.Ordinal);
        }

        private static string RewriteDashboardIndexHtml(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            if (!enableDanmakuJs)
            {
                return content;
            }

            const string scriptTag = "<script src=\"components/mediainfokeeper/ede.js\" charset=\"utf-8\"></script>";
            if (content.Contains(scriptTag, StringComparison.OrdinalIgnoreCase))
            {
                return content;
            }

            var bodyCloseIndex = content.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyCloseIndex < 0)
            {
                return content;
            }

            logger?.Info("DashboardResourcePatch: index.html 注入弹幕js ede.js");
            return content.Insert(bodyCloseIndex, scriptTag + Environment.NewLine);
        }

        private static T GetPropertyValue<T>(object instance, string propertyName) where T : class
        {
            return instance
                ?.GetType()
                .GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(instance) as T;
        }

        private static T GetFieldValue<T>(object instance, string fieldName) where T : class
        {
            return instance
                ?.GetType()
                .GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(instance) as T;
        }
    }
}
