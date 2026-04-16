using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
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

        public static void SaveChapters(
            IItemRepository itemRepository,
            BaseItem item,
            List<ChapterInfo> chapters,
            IEnumerable<MarkerType> managedMarkerTypes = null,
            bool clearExtractionFailureResult = false)
        {
            if (itemRepository == null)
            {
                throw new ArgumentNullException(nameof(itemRepository));
            }

            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            var mergedChapters = BuildMergedChapters(
                item,
                chapters,
                managedMarkerTypes,
                filterPlainChapters: true,
                out _);

            using (Allow(item.InternalId))
            {
                if (clearExtractionFailureResult)
                {
                    itemRepository.SaveChapters(item.InternalId, true, mergedChapters);
                    return;
                }

                itemRepository.SaveChapters(item.InternalId, mergedChapters);
            }
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

            if (chapters == null || chapters.Count == 0)
            {
                // 避免用空的章节列表覆盖现有章节数据。
                logger?.Debug($"片头保护 - 拦截 SaveChapters：提交的章节列表为空，禁止清空现有章节。item: {item?.FileName ?? item?.Path ?? itemId.ToString()}, itemId: {itemId}");
                return false;
            }

            var preservedProtectedMarkers = MergeProtectedMarkers(item, chapters);
            if (preservedProtectedMarkers > 0)
            {
                chapters.Sort((left, right) => left.StartPositionTicks.CompareTo(right.StartPositionTicks));
                logger?.Debug($"片头片尾保护 - SaveChapters 合并缺失标记 {preservedProtectedMarkers} 个。item: {item?.FileName ?? item?.Path ?? itemId.ToString()}, itemId: {itemId}");
            }

            if (!chapters.Any(IsProtectedMarker))
            {
                if (HasProtectedMarkers(itemId))
                {
                    // 当前已有片头或片尾标记时，不允许被一份不含片头片尾标记的结果覆盖。
                    logger?.Debug($"片头片尾保护 - 拦截 SaveChapters：当前已存在片头或片尾标记，禁止被不含片头片尾标记的结果覆盖。item: {item?.FileName ?? item?.Path ?? itemId.ToString()}, itemId: {itemId}");
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

            if (HasProtectedMarkers(itemId, markerTypes))
            {
                // 当前存在本次请求要删除的片头或片尾标记时，不允许删除相关标记。
                logger?.Info($"片头片尾保护 - 拦截 DeleteChapters：当前存在待删除的片头或片尾标记，禁止删除相关标记。item: {item?.FileName ?? item?.Path ?? itemId.ToString()}, itemId: {itemId}");
                return false;
            }

            return true;
        }

        private static bool HasProtectedMarkers(long itemId, MarkerType[] requestedMarkerTypes = null)
        {
            if (Plugin.LibraryManager == null || Plugin.IntroSkipChapterApi == null)
            {
                return false;
            }

            var item = Plugin.LibraryManager.GetItemById(itemId);
            if (item == null)
            {
                return false;
            }

            var requestedTypes = ResolveRequestedProtectedMarkerTypes(requestedMarkerTypes);
            return requestedTypes.Any(markerType => HasProtectedMarker(item, markerType));
        }

        private static int MergeProtectedMarkers(BaseItem item, List<ChapterInfo> chapters)
        {
            var mergedChapters = BuildMergedChapters(
                item,
                chapters,
                managedMarkerTypes: null,
                filterPlainChapters: false,
                out var addedCount);

            chapters.Clear();
            chapters.AddRange(mergedChapters);
            return addedCount;
        }

        private static List<ChapterInfo> BuildMergedChapters(
            BaseItem item,
            List<ChapterInfo> chapters,
            IEnumerable<MarkerType> managedMarkerTypes,
            bool filterPlainChapters,
            out int addedProtectedMarkerCount)
        {
            var result = (chapters ?? new List<ChapterInfo>())
                .Where(chapter => chapter != null)
                .Select(CloneChapter)
                .ToList();

            addedProtectedMarkerCount = 0;

            if (Plugin.IntroSkipChapterApi != null && item != null)
            {
                var existingChapters = Plugin.IntroSkipChapterApi.GetChapters(item) ?? new List<ChapterInfo>();
                var managedTypes = new HashSet<MarkerType>(NormalizeManagedProtectedMarkerTypes(managedMarkerTypes));

                foreach (var markerType in GetProtectedMarkerTypes())
                {
                    if (managedTypes.Contains(markerType) || result.Any(c => c.MarkerType == markerType))
                    {
                        continue;
                    }

                    var existingMarkers = existingChapters
                        .Where(c => c?.MarkerType == markerType)
                        .Select(CloneChapter)
                        .ToList();
                    if (existingMarkers.Count == 0)
                    {
                        continue;
                    }

                    result.AddRange(existingMarkers);
                    addedProtectedMarkerCount += existingMarkers.Count;
                }
            }

            if (filterPlainChapters)
            {
                result.RemoveAll(chapter => chapter == null || chapter.MarkerType == MarkerType.Chapter);
            }

            result.Sort((left, right) => left.StartPositionTicks.CompareTo(right.StartPositionTicks));
            return result;
        }

        private static IEnumerable<MarkerType> GetProtectedMarkerTypes()
        {
            yield return MarkerType.IntroStart;
            yield return MarkerType.IntroEnd;
            yield return MarkerType.CreditsStart;
        }

        private static IEnumerable<MarkerType> ResolveRequestedProtectedMarkerTypes(IEnumerable<MarkerType> requestedMarkerTypes)
        {
            var protectedMarkerTypes = new HashSet<MarkerType>(GetProtectedMarkerTypes());
            var requested = requestedMarkerTypes?
                .Where(protectedMarkerTypes.Contains)
                .Distinct()
                .ToList();

            return requested != null && requested.Count > 0
                ? requested
                : protectedMarkerTypes;
        }

        private static IEnumerable<MarkerType> NormalizeManagedProtectedMarkerTypes(IEnumerable<MarkerType> managedMarkerTypes)
        {
            var protectedMarkerTypes = new HashSet<MarkerType>(GetProtectedMarkerTypes());
            return managedMarkerTypes?
                .Where(protectedMarkerTypes.Contains)
                .Distinct()
                .ToArray() ?? Array.Empty<MarkerType>();
        }

        private static bool HasProtectedMarker(BaseItem item, MarkerType markerType)
        {
            if (item == null || Plugin.IntroSkipChapterApi == null)
            {
                return false;
            }

            return markerType switch
            {
                MarkerType.IntroStart => Plugin.IntroSkipChapterApi.GetIntroStart(item).HasValue,
                MarkerType.IntroEnd => Plugin.IntroSkipChapterApi.GetIntroEnd(item).HasValue,
                MarkerType.CreditsStart => Plugin.IntroSkipChapterApi.GetCreditsStart(item).HasValue,
                _ => false
            };
        }

        private static ChapterInfo CloneChapter(ChapterInfo chapter)
        {
            if (chapter == null)
            {
                return null;
            }

            return new ChapterInfo
            {
                ChapterIndex = chapter.ChapterIndex,
                ImageDateModified = chapter.ImageDateModified,
                ImagePath = chapter.ImagePath,
                ImageTag = chapter.ImageTag,
                MarkerType = chapter.MarkerType,
                Name = chapter.Name,
                StartPositionTicks = chapter.StartPositionTicks
            };
        }

        private static bool IsProtectedMarker(ChapterInfo chapter)
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
