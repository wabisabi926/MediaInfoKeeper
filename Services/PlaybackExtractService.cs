using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Patch;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public sealed class PlaybackExtractService : IDisposable
    {
        private const double NextEpisodePrefetchThreshold = 0.5;
        private readonly ILibraryManager libraryManager;
        private readonly ISessionManager sessionManager;
        private readonly ILogger logger;
        private readonly ConcurrentDictionary<long, byte> extractingItemIds = new ConcurrentDictionary<long, byte>();
        private readonly ConcurrentDictionary<string, byte> prefetchedSessions = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private bool initialized;

        public PlaybackExtractService(
            ILibraryManager libraryManager,
            ISessionManager sessionManager,
            ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.sessionManager = sessionManager;
            this.logger = logger;
        }

        public void Initialize()
        {
            if (initialized)
            {
                return;
            }

            sessionManager.PlaybackStart += OnPlaybackStart;
            sessionManager.PlaybackProgress += OnPlaybackProgress;
            sessionManager.PlaybackStopped += OnPlaybackStopped;
            initialized = true;
        }

        public void Dispose()
        {
            if (!initialized)
            {
                return;
            }

            sessionManager.PlaybackStart -= OnPlaybackStart;
            sessionManager.PlaybackProgress -= OnPlaybackProgress;
            sessionManager.PlaybackStopped -= OnPlaybackStopped;
            initialized = false;
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            if (Plugin.Instance?.Options?.MainPage?.PlugginEnabled != true)
            {
                return;
            }

            var targetItem = ResolvePlaybackItem(e);
            if (targetItem is not Video && targetItem is not Audio)
            {
                return;
            }

            if (!Plugin.LibraryService.IsItemInScope(targetItem))
            {
                return;
            }

            QueueExtractIfNeeded(targetItem, "开始播放");
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            if (Plugin.Instance?.Options?.MainPage?.PlugginEnabled != true ||
                e?.Item is not Episode episode ||
                !episode.RunTimeTicks.HasValue ||
                !e.PlaybackPositionTicks.HasValue)
            {
                return;
            }

            var playedRatio = (double)e.PlaybackPositionTicks.Value / episode.RunTimeTicks.Value;
            if (playedRatio < NextEpisodePrefetchThreshold)
            {
                return;
            }

            var sessionKey = !string.IsNullOrWhiteSpace(e.PlaySessionId)
                ? e.PlaySessionId
                : e.Session?.Id;
            if (string.IsNullOrWhiteSpace(sessionKey) || !prefetchedSessions.TryAdd(sessionKey, 0))
            {
                return;
            }

            var nextEpisodeIds = Plugin.LibraryService.NextEpisodesId(episode, 1);
            if (nextEpisodeIds.Count == 0)
            {
                return;
            }

            var nextEpisode = libraryManager.GetItemById(nextEpisodeIds[0]) as Episode;
            if (nextEpisode != null && Plugin.LibraryService.IsItemInScope(nextEpisode))
            {
                QueueExtractIfNeeded(nextEpisode, "预提取");
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            var sessionKey = !string.IsNullOrWhiteSpace(e?.PlaySessionId)
                ? e.PlaySessionId
                : e?.Session?.Id;
            if (string.IsNullOrWhiteSpace(sessionKey))
            {
                return;
            }

            prefetchedSessions.TryRemove(sessionKey, out _);
        }

        private async Task EnsurePlaybackMediaInfoAsync(long itemId, string source)
        {
            var workItem = libraryManager.GetItemById(itemId);
            if (workItem is not Video && workItem is not Audio)
            {
                return;
            }

            var hasMediaInfo = Plugin.MediaInfoService.HasMediaInfo(workItem);
            var shouldRefreshAudioForMissingCover = workItem is Audio && !Plugin.LibraryService.HasCover(workItem);
            if (hasMediaInfo && !shouldRefreshAudioForMissingCover)
            {
                return;
            }

            var displayName = workItem.Path ?? workItem.Name ?? workItem.Id.ToString();
            var logPrefix = $"{source} 媒体信息提取";
            var restoreResult = Plugin.MediaSourceInfoStore.ApplyToItem(workItem);
            if (workItem is Video)
            {
                Plugin.ChaptersStore.ApplyToItem(workItem);
            }
            else if (workItem is Audio)
            {
                Plugin.AudioMetadataStore.ApplyToItem(workItem);
                Plugin.CoverStore.ApplyToItem(workItem);
                shouldRefreshAudioForMissingCover = !Plugin.LibraryService.HasCover(workItem);
            }

            if ((restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored ||
                 restoreResult == MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists) &&
                !shouldRefreshAudioForMissingCover)
            {
                logger.Info($"{logPrefix}: 命中已保存 MediaInfo，跳过提取 {displayName}");
                return;
            }

            logger.Info($"{logPrefix}: 开始 {displayName}");

            var refreshOptions = Plugin.MediaInfoService.GetMediaInfoRefreshOptions();
            refreshOptions.ImageRefreshMode = MetadataRefreshMode.ValidationOnly;
            refreshOptions.ReplaceAllImages = true;
            refreshOptions.EnableThumbnailImageExtraction = true;
            if (shouldRefreshAudioForMissingCover)
            {
                refreshOptions.ImageRefreshMode = MetadataRefreshMode.FullRefresh;
            }
            var collectionFolders = libraryManager.GetCollectionFolders(workItem).Cast<BaseItem>().ToArray();
            var libraryOptions = libraryManager.GetLibraryOptions(workItem);

            using (FfProcessGuard.Allow())
            {
                workItem.DateLastRefreshed = new DateTimeOffset();
                await RefreshTaskRunner.RunAsync(
                        () => Plugin.ProviderManager.RefreshSingleItem(
                            workItem,
                            refreshOptions,
                            collectionFolders,
                            libraryOptions,
                            CancellationToken.None),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (!Plugin.MediaInfoService.HasMediaInfo(workItem))
            {
                logger.Warn($"{logPrefix}: 提取后仍无 MediaInfo {displayName}");
                return;
            }

            Plugin.MediaSourceInfoStore.OverWriteToFile(workItem);
            if (workItem is Video)
            {
                Plugin.ChaptersStore.OverWriteToFile(workItem);
            }
            else if (workItem is Audio)
            {
                Plugin.AudioMetadataStore.OverWriteToFile(workItem);
                Plugin.CoverStore.OverWriteToFile(workItem);
            }

            logger.Info($"{logPrefix}: 完成 {displayName}");
        }

        private void QueueExtractIfNeeded(BaseItem item, string source)
        {
            if (item is not Video && item is not Audio)
            {
                return;
            }

            if (Plugin.MediaInfoService.HasMediaInfo(item) &&
                (item is not Audio || Plugin.LibraryService.HasCover(item)))
            {
                return;
            }

            if (!extractingItemIds.TryAdd(item.InternalId, 0))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsurePlaybackMediaInfoAsync(item.InternalId, source).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Error($"{source} 媒体信息提取: 失败 {item.Path ?? item.Name}");
                    logger.Error(ex.Message);
                    logger.Debug(ex.StackTrace);
                }
                finally
                {
                    extractingItemIds.TryRemove(item.InternalId, out _);
                }
            });
        }

        private BaseItem ResolvePlaybackItem(PlaybackProgressEventArgs e)
        {
            var item = e?.Item;
            if (item == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(e.MediaSourceId) || item.GetDefaultMediaSourceId() == e.MediaSourceId)
            {
                return item;
            }

            var mediaSource = item.GetMediaSources(true, false, null)
                .FirstOrDefault(source => source.Id == e.MediaSourceId);

            if (mediaSource != null &&
                long.TryParse(mediaSource.ItemId, out var mediaSourceItemId) &&
                mediaSourceItemId > 0)
            {
                return libraryManager.GetItemById(mediaSourceItemId) ?? item;
            }

            return item;
        }
    }
}
