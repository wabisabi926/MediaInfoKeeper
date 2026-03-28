using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 拦截章节保存与删除，保护现有片头片尾标记不被刷新覆盖。
    /// </summary>
    public static class IntroMarkerProtect
    {
        private static readonly AsyncLocal<long> AllowItem = new AsyncLocal<long>();
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
                saveChapters = PatchMethodResolver.Resolve(
                    sqliteItemRepository,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "sqliteitemrepository-savechapters-with-clear-flag",
                        MethodName = "SaveChapters",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                       BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(long), typeof(bool), typeof(List<ChapterInfo>) }
                    },
                    logger,
                    "IntroMarkerProtect.SaveChapters(bool)");
                deleteChapters = PatchMethodResolver.Resolve(
                    sqliteItemRepository,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "sqliteitemrepository-deletechapters-exact",
                        MethodName = "DeleteChapters",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                       BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(long), typeof(MarkerType[]) }
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

        public static IDisposable Allow(long itemId)
        {
            AllowItem.Value = itemId;
            return new AllowSaveScope();
        }

        [HarmonyPrefix]
        private static bool SaveChaptersPrefix(long itemId, List<ChapterInfo> chapters)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (AllowItem.Value != 0 && AllowItem.Value == itemId)
            {
                return true;
            }
            var item = Plugin.LibraryManager?.GetItemById(itemId);
            
            if (chapters == null || chapters.Count == 0 || !chapters.Any(IsIntroMarker))
            {
                if (chapters == null || chapters.Count == 0)
                {
                    // 避免用空的章节列表覆盖现有章节数据。
                    logger?.Debug($"片头保护 - 拦截 SaveChapters：提交的章节列表为空，禁止清空现有章节。item: {item?.FileName ?? item?.Path ?? itemId.ToString()}, itemId: {itemId}");
                    return false;
                }

                if (HasIntroMarkers(itemId))
                {
                    // 当前已有片头标记时，不允许被一份不含片头标记的结果覆盖。
                    logger?.Debug($"片头保护 - 拦截 SaveChapters：当前已存在片头标记，禁止被不含片头标记的结果覆盖。item: {item?.FileName ?? item?.Path ?? itemId.ToString()}, itemId: {itemId}");
                    return false;
                }
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

            if (AllowItem.Value != 0 && AllowItem.Value == itemId)
            {
                return true;
            }
            var item = Plugin.LibraryManager?.GetItemById(itemId);

            if (markerTypes != null && markerTypes.Length > 0 &&
                !markerTypes.Any(t => t == MarkerType.IntroStart || t == MarkerType.IntroEnd || t == MarkerType.CreditsStart))
            {
                return true;
            }

            if (HasIntroMarkers(itemId))
            {
                // 当前已有片头标记时，不允许删除片头相关标记。
                logger?.Info($"片头保护 - 拦截 DeleteChapters：当前已存在片头标记，禁止删除片头相关标记。item: {item?.FileName ?? item?.Path ?? itemId.ToString()}, itemId: {itemId}");
                return false;
            }

            return true;
        }

        private static bool HasIntroMarkers(long itemId)
        {
            if (Plugin.LibraryManager == null || Plugin.IntroScanService == null)
            {
                return false;
            }

            var item = Plugin.LibraryManager.GetItemById(itemId);
            if (item == null)
            {
                return false;
            }

            return Plugin.IntroScanService.HasIntroMarkers(item);
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

        private sealed class AllowSaveScope : IDisposable
        {
            public void Dispose()
            {
                AllowItem.Value = 0;
            }
        }
    }
}
