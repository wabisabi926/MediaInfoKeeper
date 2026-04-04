using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Options;
using MediaInfoKeeper.Patch;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaInfoKeeper.Services.IntroSkip
{
    public class IntroSkipChapterApi
    {
        private const string MarkerSuffix = "#MIK";

        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IItemRepository itemRepository;

        public IntroSkipChapterApi(ILibraryManager libraryManager, IItemRepository itemRepository, ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.itemRepository = itemRepository;
            this.logger = logger;
        }

        public List<ChapterInfo> GetChapters(BaseItem item)
        {
            return itemRepository.GetChapters(item);
        }

        public long? GetIntroStart(BaseItem item)
        {
            var introStart = itemRepository.GetChapters(item)
                .FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);
            return introStart?.StartPositionTicks;
        }

        public long? GetIntroEnd(BaseItem item)
        {
            var introEnd = itemRepository.GetChapters(item)
                .FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);
            return introEnd?.StartPositionTicks;
        }

        public long? GetCreditsStart(BaseItem item)
        {
            var creditsStart = itemRepository.GetChapters(item)
                .FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart);
            return creditsStart?.StartPositionTicks;
        }

        public void UpdateIntro(Episode item, SessionInfo session, long introStartPositionTicks,
            long introEndPositionTicks)
        {
            if (introStartPositionTicks > introEndPositionTicks)
            {
                return;
            }

            var mode = ParseMode(Plugin.Instance?.Options?.IntroSkip?.IntroMarkerMode);
            var fetchResult = FetchEpisodes(item, mode, MarkerType.IntroEnd);
            var resultEpisodes = fetchResult.TargetEpisodes;
            var updatedEpisodes = new List<string>();
            var skippedEpisodes = new List<string>();

            foreach (var episode in resultEpisodes)
            {
                var chapters = itemRepository.GetChapters(episode);

                if (episode.InternalId == item.InternalId && HasNonMikIntro(chapters))
                {
                    skippedEpisodes.Add((episode.FileName ?? episode.Path ?? episode.Name) + " (当前集已有非插件片头标记)");
                    continue;
                }

                if (episode.InternalId != item.InternalId &&
                    mode != IntroSkipOptions.SubsequentMarkerMode.Overwrite &&
                    HasNonMikIntro(chapters))
                {
                    skippedEpisodes.Add((episode.FileName ?? episode.Path ?? episode.Name) + " (已有非插件片头标记)");
                    continue;
                }

                if (mode == IntroSkipOptions.SubsequentMarkerMode.FillMissing &&
                    episode.InternalId != item.InternalId &&
                    HasIntroData(chapters))
                {
                    skippedEpisodes.Add((episode.FileName ?? episode.Path ?? episode.Name) + " (已有片头数据)");
                    continue;
                }

                chapters.RemoveAll(c => c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);

                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.IntroStart + MarkerSuffix,
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = introStartPositionTicks
                });
                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.IntroEnd + MarkerSuffix,
                    MarkerType = MarkerType.IntroEnd,
                    StartPositionTicks = introEndPositionTicks
                });

                chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                using (IntroMarkerProtect.Allow(episode.InternalId))
                {
                    itemRepository.SaveChapters(episode.InternalId, chapters);
                }

                Plugin.ChaptersStore.OverWriteToFile(episode);
                updatedEpisodes.Add(episode.FileName ?? episode.Path ?? episode.Name);
            }

            var introStartTime = new TimeSpan(introStartPositionTicks).ToString(@"hh\:mm\:ss\.fff");
            var introEndTime = new TimeSpan(introEndPositionTicks).ToString(@"hh\:mm\:ss\.fff");
            logger.Info("片头标记已更新，用户: " + session.UserName + "，条目: " +
                        (item.FileName ?? item.Path) + "，片头开始: " + introStartTime +
                        "，片头结束: " + introEndTime);
            logger.Info("片头生效偏好: " + GetSubsequentMarkerModeDisplayName(mode) +
                        "，当前季候选=" + fetchResult.AllEpisodes.Count +
                        "，生效目标=" + resultEpisodes.Count +
                        "，已写入=" + updatedEpisodes.Count +
                        "，跳过=" + skippedEpisodes.Count);
            logger.Debug("片头当前季候选诊断: 当前条目=" + (item.FileName ?? item.Path) +
                         "，SeasonId=" + item.ParentId +
                         "，当前索引=" + fetchResult.CurrentIndex +
                         "，候选列表=" + FormatEpisodeList(fetchResult.AllEpisodes) +
                         "，生效目标=" + FormatEpisodeList(resultEpisodes) +
                         "，筛除原因=" + FormatExcludedReasons(fetchResult.ExcludedReasons) +
                         "，跳过详情=" + FormatReasons(skippedEpisodes));
            _ = Plugin.NotificationApi.IntroUpdateSendNotification(item, session, introStartTime, introEndTime);
        }

        public void UpdateCredits(Episode item, SessionInfo session, long creditsDurationTicks)
        {
            var mode = ParseMode(Plugin.Instance?.Options?.IntroSkip?.CreditsMarkerMode);
            var fetchResult = FetchEpisodes(item, mode, MarkerType.CreditsStart);
            var resultEpisodes = fetchResult.TargetEpisodes;
            var updatedEpisodes = new List<string>();
            var skippedEpisodes = new List<string>();

            foreach (var episode in resultEpisodes)
            {
                if (!episode.RunTimeTicks.HasValue)
                {
                    skippedEpisodes.Add((episode.FileName ?? episode.Path ?? episode.Name) + " (无运行时长)");
                    continue;
                }

                if (episode.RunTimeTicks.Value - creditsDurationTicks <= 0)
                {
                    skippedEpisodes.Add((episode.FileName ?? episode.Path ?? episode.Name) + " (片尾时长超出范围)");
                    continue;
                }

                var chapters = itemRepository.GetChapters(episode);

                if (episode.InternalId != item.InternalId &&
                    mode != IntroSkipOptions.SubsequentMarkerMode.Overwrite &&
                    HasNonMikCredits(chapters))
                {
                    skippedEpisodes.Add((episode.FileName ?? episode.Path ?? episode.Name) + " (已有非插件片尾标记)");
                    continue;
                }

                if (mode == IntroSkipOptions.SubsequentMarkerMode.FillMissing &&
                    episode.InternalId != item.InternalId &&
                    HasCreditsData(chapters))
                {
                    skippedEpisodes.Add((episode.FileName ?? episode.Path ?? episode.Name) + " (已有片尾数据)");
                    continue;
                }

                chapters.RemoveAll(c => c.MarkerType == MarkerType.CreditsStart);

                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.CreditsStart + MarkerSuffix,
                    MarkerType = MarkerType.CreditsStart,
                    StartPositionTicks = episode.RunTimeTicks.Value - creditsDurationTicks
                });

                chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                using (IntroMarkerProtect.Allow(episode.InternalId))
                {
                    itemRepository.SaveChapters(episode.InternalId, chapters);
                }

                Plugin.ChaptersStore.OverWriteToFile(episode);
                updatedEpisodes.Add(episode.FileName ?? episode.Path ?? episode.Name);
            }

            var creditsDuration = new TimeSpan(creditsDurationTicks).ToString(@"hh\:mm\:ss\.fff");
            var creditsStartTime = item.RunTimeTicks.HasValue
                ? new TimeSpan(item.RunTimeTicks.Value - creditsDurationTicks).ToString(@"hh\:mm\:ss\.fff")
                : "unknown";
            logger.Info("片尾标记已更新，用户: " + session.UserName + "，条目: " +
                        (item.FileName ?? item.Path) + "，片尾开始: " + creditsStartTime +
                        "，片尾时长: " + creditsDuration);
            logger.Info("片尾生效偏好: " + GetSubsequentMarkerModeDisplayName(mode) +
                        "，当前季候选=" + fetchResult.AllEpisodes.Count +
                        "，生效目标=" + resultEpisodes.Count +
                        "，已写入=" + updatedEpisodes.Count +
                        "，跳过=" + skippedEpisodes.Count);
            logger.Debug("片尾当前季候选诊断: 当前条目=" + (item.FileName ?? item.Path) +
                         "，SeasonId=" + item.ParentId +
                         "，当前索引=" + fetchResult.CurrentIndex +
                         "，候选列表=" + FormatEpisodeList(fetchResult.AllEpisodes) +
                         "，生效目标=" + FormatEpisodeList(resultEpisodes) +
                         "，筛除原因=" + FormatExcludedReasons(fetchResult.ExcludedReasons) +
                         "，跳过详情=" + FormatReasons(skippedEpisodes));
            _ = Plugin.NotificationApi.CreditsUpdateSendNotification(item, session, creditsDuration);
        }

        public void RemoveIntroMarkers(BaseItem item)
        {
            if (item == null)
            {
                return;
            }

            var chapters = itemRepository.GetChapters(item);
            if (chapters == null || chapters.Count == 0)
            {
                return;
            }

            var removed = chapters.RemoveAll(c =>
                c.MarkerType == MarkerType.IntroStart ||
                c.MarkerType == MarkerType.IntroEnd ||
                c.MarkerType == MarkerType.CreditsStart);

            if (removed <= 0)
            {
                logger.Info("ShortcutMenu 未发现片头片尾标记: " + (item.FileName ?? item.Path));
                return;
            }

            chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
            using (IntroMarkerProtect.Allow(item.InternalId))
            {
                itemRepository.SaveChapters(item.InternalId, chapters);
            }

            Plugin.ChaptersStore.OverWriteToFile(item);

            logger.Info("ShortcutMenu 清理片头片尾标记: " + (item.FileName ?? item.Path));
        }

        private static bool IsMarkerAddedByMik(ChapterInfo chapter)
        {
            return chapter.Name?.EndsWith(MarkerSuffix, StringComparison.Ordinal) == true;
        }

        private static bool HasNonMikIntro(List<ChapterInfo> chapters)
        {
            return chapters.Any(c => (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd) &&
                                     !IsMarkerAddedByMik(c));
        }

        private static bool HasNonMikCredits(List<ChapterInfo> chapters)
        {
            return chapters.Any(c => c.MarkerType == MarkerType.CreditsStart && !IsMarkerAddedByMik(c));
        }

        private static bool HasIntroData(List<ChapterInfo> chapters)
        {
            return chapters?.Any(c => c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd) == true;
        }

        private static bool HasCreditsData(List<ChapterInfo> chapters)
        {
            return chapters?.Any(c => c.MarkerType == MarkerType.CreditsStart) == true;
        }

        private static bool HasOnlyMikIntro(List<ChapterInfo> chapters)
        {
            var introMarkers = chapters?
                .Where(c => c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd)
                .ToList();

            return introMarkers != null && introMarkers.Count > 0 && introMarkers.All(IsMarkerAddedByMik);
        }

        private static bool HasOnlyMikCredits(List<ChapterInfo> chapters)
        {
            var creditsMarkers = chapters?
                .Where(c => c.MarkerType == MarkerType.CreditsStart)
                .ToList();

            return creditsMarkers != null && creditsMarkers.Count > 0 && creditsMarkers.All(IsMarkerAddedByMik);
        }

        private static IntroSkipOptions.SubsequentMarkerMode ParseMode(string value)
        {
            return Enum.TryParse(value, true, out IntroSkipOptions.SubsequentMarkerMode mode)
                ? mode
                : IntroSkipOptions.SubsequentMarkerMode.CurrentOnly;
        }

        private static string GetSubsequentMarkerModeDisplayName(IntroSkipOptions.SubsequentMarkerMode option)
        {
            return option switch
            {
                IntroSkipOptions.SubsequentMarkerMode.CurrentOnly => "仅设置本集，不作用于后续剧集",
                IntroSkipOptions.SubsequentMarkerMode.FillMissing => "如果后续剧集无数据，则补全写入",
                IntroSkipOptions.SubsequentMarkerMode.Overwrite => "忽略后续已有章节信息，覆盖写入后续剧集",
                _ => option.ToString()
            };
        }

        private static string FormatEpisodeList(IEnumerable<Episode> episodes)
        {
            var list = episodes?
                .Select(e => e?.FileName ?? e?.Path ?? e?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            return list == null || list.Count == 0 ? "无" : string.Join(" | ", list);
        }

        private static string FormatExcludedReasons(IEnumerable<string> reasons)
        {
            var list = reasons?.Where(reason => !string.IsNullOrWhiteSpace(reason)).ToList();
            return list == null || list.Count == 0 ? "无" : string.Join(" | ", list);
        }

        private static string FormatReasons(IEnumerable<string> reasons)
        {
            var list = reasons?.Where(reason => !string.IsNullOrWhiteSpace(reason)).ToList();
            return list == null || list.Count == 0 ? "无" : string.Join(" | ", list);
        }

        private FetchEpisodesResult FetchEpisodes(Episode item, IntroSkipOptions.SubsequentMarkerMode mode, MarkerType markerType)
        {
            var libraryService = Plugin.LibraryService;
            var episodes = (libraryService?.GetSeriesEpisodesFromItem(item) ?? Array.Empty<Episode>())
                .Where(e => e != null)
                .OrderBy(e => e.IndexNumber ?? int.MaxValue)
                .ThenBy(e => e.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var currentIndex = episodes.FindIndex(e => e.InternalId == item.InternalId);
            if (currentIndex < 0)
            {
                return new FetchEpisodesResult
                {
                    AllEpisodes = episodes,
                    CurrentIndex = -1,
                    TargetEpisodes = new List<Episode> { item }
                };
            }

            if (mode == IntroSkipOptions.SubsequentMarkerMode.CurrentOnly)
            {
                return new FetchEpisodesResult
                {
                    AllEpisodes = episodes,
                    CurrentIndex = currentIndex,
                    TargetEpisodes = new List<Episode> { episodes[currentIndex] }
                };
            }

            var currentEpisode = episodes[currentIndex];
            var excludedReasons = new List<string>();
            var priorEpisodesWithoutMarkers = episodes
                .Take(currentIndex)
                .Where(e => IsMissingMarkerData(e, markerType, excludedReasons))
                .ToList();

            var nextEpisodeIds = libraryService?.NextEpisodesId(item) ?? Array.Empty<long>();
            var followingEpisodes = nextEpisodeIds
                .Select(id => episodes.FirstOrDefault(e => e.InternalId == id))
                .Where(e => e != null)
                .Where(e => ShouldApplyToFollowingEpisode(e, markerType, mode, excludedReasons))
                .ToList();

            return new FetchEpisodesResult
            {
                AllEpisodes = episodes,
                CurrentIndex = currentIndex,
                ExcludedReasons = excludedReasons,
                TargetEpisodes = priorEpisodesWithoutMarkers
                    .Concat(new[] { currentEpisode })
                    .Concat(followingEpisodes)
                    .ToList()
            };
        }

        private bool IsMissingMarkerData(Episode episode, MarkerType markerType, List<string> excludedReasons)
        {
            var chapters = itemRepository.GetChapters(episode);
            var shouldInclude = markerType switch
            {
                MarkerType.IntroEnd => !HasIntroData(chapters) || HasOnlyMikIntro(chapters),
                MarkerType.CreditsStart => !HasCreditsData(chapters) || HasOnlyMikCredits(chapters),
                _ => false
            };

            if (!shouldInclude)
            {
                excludedReasons?.Add((episode.FileName ?? episode.Path ?? episode.Name) +
                                     (markerType == MarkerType.IntroEnd ? " (当前集之前，已有非插件片头数据)" : " (当前集之前，已有非插件片尾数据)"));
            }

            return shouldInclude;
        }

        private bool ShouldApplyToFollowingEpisode(Episode episode, MarkerType markerType, IntroSkipOptions.SubsequentMarkerMode mode,
            List<string> excludedReasons)
        {
            if (mode == IntroSkipOptions.SubsequentMarkerMode.CurrentOnly)
            {
                excludedReasons?.Add((episode.FileName ?? episode.Path ?? episode.Name) + " (仅设置本集)");
                return false;
            }

            var chapters = itemRepository.GetChapters(episode);
            var shouldInclude = markerType switch
            {
                MarkerType.IntroEnd => mode == IntroSkipOptions.SubsequentMarkerMode.Overwrite
                    ? !HasNonMikIntro(chapters)
                    : !HasIntroData(chapters) || HasOnlyMikIntro(chapters),
                MarkerType.CreditsStart => mode == IntroSkipOptions.SubsequentMarkerMode.Overwrite
                    ? !HasNonMikCredits(chapters)
                    : !HasCreditsData(chapters) || HasOnlyMikCredits(chapters),
                _ => false
            };

            if (!shouldInclude)
            {
                var reason = markerType switch
                {
                    MarkerType.IntroEnd when mode == IntroSkipOptions.SubsequentMarkerMode.Overwrite => "后续已有非插件片头标记",
                    MarkerType.IntroEnd => "后续已有非插件片头数据",
                    MarkerType.CreditsStart when mode == IntroSkipOptions.SubsequentMarkerMode.Overwrite => "后续已有非插件片尾标记",
                    MarkerType.CreditsStart => "后续已有非插件片尾数据",
                    _ => "不满足条件"
                };
                excludedReasons?.Add((episode.FileName ?? episode.Path ?? episode.Name) + " (" + reason + ")");
            }

            return shouldInclude;
        }

        private sealed class FetchEpisodesResult
        {
            public List<Episode> AllEpisodes { get; set; } = new List<Episode>();

            public List<Episode> TargetEpisodes { get; set; } = new List<Episode>();

            public int CurrentIndex { get; set; } = -1;

            public List<string> ExcludedReasons { get; set; } = new List<string>();
        }
    }
}
