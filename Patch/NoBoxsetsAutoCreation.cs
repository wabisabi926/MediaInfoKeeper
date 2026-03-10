using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Data;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// Prevent automatic BoxSets library creation and hide the BoxSets view entry.
    /// </summary>
    public static class NoBoxsetsAutoCreation
    {
        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo ensureLibraryFolder;
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

                var collectionManagerType =
                    implAssembly?.GetType("Emby.Server.Implementations.Collections.CollectionManager");
                ensureLibraryFolder = PatchMethodResolver.Resolve(
                    collectionManagerType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "collectionmanager-ensurelibraryfolder-exact",
                        MethodName = "EnsureLibraryFolder",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = Type.EmptyTypes,
                        ReturnType = typeof(void)
                    },
                    logger,
                    "NoBoxsetsAutoCreation.CollectionManager.EnsureLibraryFolder");

                var userViewManagerType =
                    implAssembly?.GetType("Emby.Server.Implementations.Library.UserViewManager");
                getUserViews = PatchMethodResolver.Resolve(
                    userViewManagerType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "userviewmanager-getuserviews-exact",
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
                    "NoBoxsetsAutoCreation.UserViewManager.GetUserViews");

                if (ensureLibraryFolder == null || getUserViews == null)
                {
                    PatchLog.InitFailed(logger, nameof(NoBoxsetsAutoCreation), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.noboxsetsautocreation");
                PatchLog.Patched(logger, nameof(NoBoxsetsAutoCreation), ensureLibraryFolder);
                PatchLog.Patched(logger, nameof(NoBoxsetsAutoCreation), getUserViews);

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("NoBoxsetsAutoCreation 初始化失败。");
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
            if (isPatched || harmony == null || ensureLibraryFolder == null || getUserViews == null)
            {
                return;
            }

            harmony.Patch(
                ensureLibraryFolder,
                prefix: new HarmonyMethod(typeof(NoBoxsetsAutoCreation), nameof(EnsureLibraryFolderPrefix)));
            harmony.Patch(
                getUserViews,
                postfix: new HarmonyMethod(typeof(NoBoxsetsAutoCreation), nameof(GetUserViewsPostfix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            if (ensureLibraryFolder != null)
            {
                harmony.Unpatch(ensureLibraryFolder, HarmonyPatchType.Prefix, harmony.Id);
            }

            if (getUserViews != null)
            {
                harmony.Unpatch(getUserViews, HarmonyPatchType.Postfix, harmony.Id);
            }

            isPatched = false;
        }

        [HarmonyPrefix]
        private static bool EnsureLibraryFolderPrefix()
        {
            return !isEnabled;
        }

        [HarmonyPostfix]
        private static void GetUserViewsPostfix(ref Folder[] __result)
        {
            if (!isEnabled || __result == null || __result.Length == 0)
            {
                return;
            }

            __result = __result
                .Where(i => !(i is CollectionFolder library) ||
                            !string.Equals(library.CollectionType, CollectionType.BoxSets.ToString(),
                                StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }
}
