using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Data;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 在获取用户视图前同步管理员排序，统一媒体库显示顺序。
    /// </summary>
    public static class EnforceLibraryOrder
    {
        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo getUserViews;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

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
                var implAssembly = Assembly.Load("Emby.Server.Implementations");
                var implVersion = implAssembly?.GetName().Version;
                var userViewManagerType =
                    implAssembly?.GetType("Emby.Server.Implementations.Library.UserViewManager");

                getUserViews = PatchMethodResolver.Resolve(
                    userViewManagerType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "userviewmanager-getuserviews-enforcelibraryorder",
                        MethodName = "GetUserViews",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(UserViewQuery),
                            typeof(User),
                            typeof(Folder[]),
                            typeof(IDataContext),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Folder[])
                    },
                    logger,
                    "EnforceLibraryOrder.UserViewManager.GetUserViews");

                if (getUserViews == null)
                {
                    PatchLog.InitFailed(logger, nameof(EnforceLibraryOrder), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.enforcelibraryorder");
                PatchLog.Patched(logger, nameof(EnforceLibraryOrder), getUserViews);

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("EnforceLibraryOrder 初始化失败。");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
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
            if (isPatched || harmony == null || getUserViews == null)
            {
                return;
            }

            harmony.Patch(
                getUserViews,
                prefix: new HarmonyMethod(typeof(EnforceLibraryOrder), nameof(GetUserViewsPrefix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || getUserViews == null)
            {
                return;
            }

            harmony.Unpatch(getUserViews, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        private static bool GetUserViewsPrefix(User user)
        {
            if (!isEnabled || user?.Configuration == null)
            {
                return true;
            }

            var adminOrderedViews = Plugin.LibraryService?.GetAdminOrderedViews();
            if (adminOrderedViews == null || adminOrderedViews.Length == 0)
            {
                return true;
            }

            user.Configuration.OrderedViews = adminOrderedViews;
            return true;
        }
    }
}
