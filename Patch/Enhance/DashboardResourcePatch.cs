using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 拦截仪表盘资源请求，按配置重写首页内容。
    /// </summary>
    public static class DashboardResourcePatch
    {
        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool enableDanmakuJs;
        private static MethodInfo modifyHtmlMethod;
        private static bool isPatched;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enableDanmaku)
        {
            if (harmony != null)
            {
                Configure(enableDanmaku);
                return;
            }

            logger = pluginLogger;
            enableDanmakuJs = enableDanmaku;
            isEnabled = enableDanmakuJs;

            try
            {
                var embyWebAssembly = Assembly.Load("Emby.Web");
                var embyWebVersion = embyWebAssembly?.GetName().Version;
                var packageCreatorType = embyWebAssembly?.GetType("Emby.Web.Api.PackageCreator", false);

                if (packageCreatorType == null)
                {
                    PatchLog.InitFailed(logger, nameof(DashboardResourcePatch), "PackageCreator 类型缺失");
                    return;
                }

                modifyHtmlMethod = PatchMethodResolver.Resolve(
                    packageCreatorType,
                    embyWebVersion,
                    new MethodSignatureProfile
                    {
                        Name = "packagecreator-modifyhtml-exact",
                        MethodName = "ModifyHtml",
                        BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(string),
                            typeof(Stream),
                            typeof(string),
                            typeof(string),
                            typeof(long)
                        },
                        ReturnType = typeof(Task<StreamHandler>)
                    },
                    logger,
                    "DashboardResourcePatch.ModifyHtml");

                if (modifyHtmlMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(DashboardResourcePatch), "DashboardResourcePatch 目标方法缺失");
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
                modifyHtmlMethod = null;
                isPatched = false;
            }
        }

        public static void Configure(bool enableDanmaku)
        {
            enableDanmakuJs = enableDanmaku;
            isEnabled = enableDanmakuJs;

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
            if (isPatched || harmony == null || modifyHtmlMethod == null)
            {
                return;
            }

            harmony.Patch(
                modifyHtmlMethod,
                postfix: new HarmonyMethod(typeof(DashboardResourcePatch), nameof(ModifyHtmlPostfix)));
            PatchLog.Patched(logger, nameof(DashboardResourcePatch), modifyHtmlMethod);

            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.UnpatchAll(harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void ModifyHtmlPostfix(string __0, ref Task<StreamHandler> __result)
        {
            if (!enableDanmakuJs)
            {
                return;
            }

            var path = __0;
            if (!string.Equals(path, "index.html", StringComparison.OrdinalIgnoreCase) || __result == null)
            {
                return;
            }

            logger?.Debug("DashboardResourcePatch target hit: {0}", path);
            logger?.Debug("DashboardResourcePatch: 捕获 dashboard 资源请求 path={0}", path);
            __result = RewriteModifyHtmlTaskAsync(__result, path);
        }

        private static async Task<StreamHandler> RewriteModifyHtmlTaskAsync(Task<StreamHandler> originalTask, string path)
        {
            var handler = await originalTask.ConfigureAwait(false);
            if (handler?.Stream == null)
            {
                logger?.Warn("DashboardResourcePatch: ModifyHtml 返回空流 path={0}", path);
                return handler;
            }

            using var reader = new StreamReader(handler.Stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            var originalContent = await reader.ReadToEndAsync().ConfigureAwait(false);
            var rewrittenContent = RewriteDashboardIndexHtml(originalContent);

            handler.Stream = new MemoryStream(Encoding.UTF8.GetBytes(rewrittenContent), writable: false);
            handler.Length = rewrittenContent.Length;
            handler.TotalLength = rewrittenContent.Length;
            return handler;
        }

        private static string RewriteDashboardIndexHtml(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                logger?.Warn("DashboardResourcePatch: index.html 内容为空，跳过注入 ede.js");
                return content;
            }

            if (!enableDanmakuJs)
            {
                logger?.Debug("DashboardResourcePatch: index.html 命中但 EnableDanmakuJs=false，跳过注入");
                return content;
            }

            const string scriptTag = "<script src=\"components/mediainfokeeper/ede.js\" charset=\"utf-8\"></script>";
            if (content.Contains(scriptTag, StringComparison.OrdinalIgnoreCase))
            {
                logger?.Debug("DashboardResourcePatch: index.html 已包含 ede.js，跳过重复注入");
                return content;
            }

            var bodyCloseIndex = content.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyCloseIndex < 0)
            {
                logger?.Warn("DashboardResourcePatch: index.html 未找到 </body>，无法注入 ede.js");
                return content;
            }

            var rewritten = content.Insert(bodyCloseIndex, scriptTag + Environment.NewLine);
            logger?.Debug("DashboardResourcePatch: index.html 注入弹幕js ede.js 成功");
            return rewritten;
        }
    }
}
