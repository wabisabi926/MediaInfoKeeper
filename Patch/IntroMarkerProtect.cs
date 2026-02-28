using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    public static class IntroMarkerProtect
    {
        private static readonly AsyncLocal<long> AllowSaveItem = new AsyncLocal<long>();
        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo saveChapters;
        private static MethodInfo deleteChapters;
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
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var sqliteItemRepository =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
                var version = embyServerImplementationsAssembly.GetName().Version;
                saveChapters = VersionedMethodResolver.Resolve(
                    sqliteItemRepository,
                    version,
                    new[]
                    {
                        new MethodSignatureProfile
                        {
                            Name = "sqliteitemrepository-savechapters-exact",
                            MethodName = "SaveChapters",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                           BindingFlags.NonPublic,
                            Predicate = m =>
                            {
                                var p = m.GetParameters();
                                return p.Length >= 3 && p[0].ParameterType == typeof(long);
                            }
                        }
                    },
                    logger,
                    "IntroMarkerProtect.SaveChapters");
                deleteChapters = VersionedMethodResolver.Resolve(
                    sqliteItemRepository,
                    version,
                    new[]
                    {
                        new MethodSignatureProfile
                        {
                            Name = "sqliteitemrepository-deletechapters-exact",
                            MethodName = "DeleteChapters",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                           BindingFlags.NonPublic,
                            ParameterTypes = new[] { typeof(long), typeof(MarkerType[]) }
                        }
                    },
                    logger,
                    "IntroMarkerProtect.DeleteChapters");

                if (saveChapters == null || deleteChapters == null)
                {
                    PatchLog.InitFailed(logger, nameof(IntroMarkerProtect), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.introprotect");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception e)
            {
                logger?.Error("IntroMarkerProtect 初始化失败。");
                logger?.Error(e.Message);
                logger?.Error(e.ToString());
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

        public static IDisposable AllowSave(long itemId)
        {
            AllowSaveItem.Value = itemId;
            return new AllowSaveScope();
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(saveChapters,
                prefix: new HarmonyMethod(typeof(IntroMarkerProtect), nameof(SaveChaptersPrefix)));
            harmony.Patch(deleteChapters,
                prefix: new HarmonyMethod(typeof(IntroMarkerProtect), nameof(DeleteChaptersPrefix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.Unpatch(saveChapters, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(deleteChapters, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        private static bool SaveChaptersPrefix(long itemId, bool clearExtractionFailureResult,
            List<ChapterInfo> chapters)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (AllowSaveItem.Value != 0 && AllowSaveItem.Value == itemId)
            {
                return true;
            }

            if (chapters != null && chapters.Any(IsIntroMarker))
            {
                return true;
            }

            if (HasIntroMarkers(itemId))
            {
                logger?.Info($"片头保护 - 拦截 SaveChapters，itemId: {itemId}");
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool DeleteChaptersPrefix(long itemId, MarkerType[] markerTypes)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (AllowSaveItem.Value != 0 && AllowSaveItem.Value == itemId)
            {
                return true;
            }

            if (markerTypes != null && markerTypes.Length > 0 &&
                !markerTypes.Any(t => t == MarkerType.IntroStart || t == MarkerType.IntroEnd || t == MarkerType.CreditsStart))
            {
                return true;
            }

            if (HasIntroMarkers(itemId))
            {
                logger?.Info($"片头保护 - 拦截 DeleteChapters，itemId: {itemId}");
                return false;
            }

            return true;
        }

        private static bool HasIntroMarkers(long itemId)
        {
            if (Plugin.LibraryManager == null)
            {
                return false;
            }

            var item = Plugin.LibraryManager.GetItemById(itemId);
            if (item == null)
            {
                return false;
            }

            var chapters = Plugin.IntroSkipChapterApi?.GetChapters(item);
            if (chapters == null || chapters.Count == 0)
            {
                return false;
            }

            return chapters.Any(IsIntroMarker);
        }

        private static bool IsIntroMarker(ChapterInfo chapter)
        {
            if (chapter == null)
            {
                return false;
            }

            if (chapter.MarkerType != MarkerType.IntroStart &&
                chapter.MarkerType != MarkerType.IntroEnd &&
                chapter.MarkerType != MarkerType.CreditsStart)
            {
                return false;
            }

            return true;
        }

        private static MethodInfo FindMethod(Type type, string methodName, Func<MethodInfo, bool> predicate = null)
        {
            if (type == null) return null;

            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                          BindingFlags.NonPublic)
                .Where(m => m.Name == methodName);

            if (predicate != null) methods = methods.Where(predicate);

            return methods.FirstOrDefault();
        }

        private sealed class AllowSaveScope : IDisposable
        {
            public void Dispose()
            {
                AllowSaveItem.Value = 0;
            }
        }
    }
}
