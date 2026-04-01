using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ExtractRecentMediaInfoTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;

        public ExtractRecentMediaInfoTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }

        public string Key => "MediaInfoKeeperExtractRecentMediaInfoTask";

        public string Name => "3.提取媒体信息";

        public string Description => "计划任务媒体库范围内，按入库时间倒序取最近 N 条（“最近入库媒体筛选数量”）恢复/提取媒体信息并写入 JSON。（已存在则恢复）";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("计划任务执行");

            var items = FetchRecentScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100.0);
                this.logger.Info("计划任务完成，条目数 0");
                return;
            }

            var completed = 0;
            var tasks = items
                .Select(async item =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        await RefreshTaskRunner.RunAsync(
                                () => ProcessItemAsync(item, "Recent Scheduled Task", cancellationToken),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                    catch (Exception ex)
                    {
                        this.logger.Error($"任务执行失败: {item.Path ?? item.Name}");
                        this.logger.Error(ex.Message);
                        this.logger.Debug(ex.StackTrace);
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report(done / (double)total * 100);
                    }
                })
                .ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            this.logger.Info("计划任务完成");
        }

        private List<BaseItem> FetchRecentScopedItems()
        {
            var limit = Math.Max(1, Plugin.Instance.Options.MainPage.RecentItemsLimit);
            var scopePaths = Plugin.LibraryService.GetScopedLibraryPaths(
                Plugin.Instance.Options.MainPage.ScheduledTaskLibraries,
                out var hasScope);
            if (hasScope && !scopePaths.Any())
            {
                this.logger.Info("计划任务条目数 0，范围内未匹配到媒体库");
                return new List<BaseItem>();
            }

            var items = Plugin.LibraryService.FetchScopedVideoItems(scopePaths, true, limit, includeAudio: true);
            this.logger.Info($"计划任务条目数 {items.Count}");
            return items;
        }

        private async Task ProcessItemAsync(BaseItem item, string source, CancellationToken cancellationToken)
        {
            var displayName = item.FileName ?? item.Path;

            if (!Plugin.LibraryService.IsItemInScope(item))
            {
                this.logger.Info($"跳过 不在库范围: {displayName}");
                return;
            }

            var persistMediaInfo = (item is Video || item is Audio) && Plugin.Instance.Options.MainPage.PlugginEnabled;
            if (!persistMediaInfo)
            {
                this.logger.Info($"跳过 未开启持久化或非音视频: {displayName}");
                return;
            }

            using (FfProcessGuard.Allow())
            {
                var filePath = item.Path;
                if (string.IsNullOrEmpty(filePath))
                {
                    this.logger.Info($"跳过 无路径: {displayName}");
                    return;
                }

                var refreshOptions = Plugin.MediaInfoService.GetMediaInfoRefreshOptions();
                var directoryService = refreshOptions.DirectoryService;

                if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                    uri.Scheme == Uri.UriSchemeFile)
                {
                    var file = directoryService.GetFile(filePath);
                    if (file?.Exists != true)
                    {
                        this.logger.Info($"跳过 文件不存在: {displayName}");
                        return;
                    }
                }

                var collectionFolders = this.libraryManager.GetCollectionFolders(item).Cast<BaseItem>().ToArray();
                var libraryOptions = this.libraryManager.GetLibraryOptions(item);

                var dummyLibraryOptions = LibraryService.CopyLibraryOptions(libraryOptions);

                var deserializeResult = Plugin.MediaSourceInfoStore.ApplyToItem(item);
                var shouldRefreshAudioForMissingCover = false;
                if (item is Video)
                {
                    Plugin.ChaptersStore.ApplyToItem(item);
                }
                else if (item is Audio)
                {
                    Plugin.AudioMetadataStore.ApplyToItem(item);
                    Plugin.CoverStore.ApplyToItem(item);
                    shouldRefreshAudioForMissingCover = !Plugin.LibraryService.HasCover(item);
                }

                if (deserializeResult == MediaInfoDocument.MediaInfoRestoreResult.Restored)
                {
                    if (shouldRefreshAudioForMissingCover)
                    {
                        this.logger.Info($"从JSON 恢复媒体信息成功但缺少封面，继续提取: {displayName}");
                    }
                    else
                    {
                        this.logger.Info($"从JSON 恢复成功: {displayName}");
                        return;
                    }
                }

                if (deserializeResult == MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists)
                {
                    if (item is not Audio || !shouldRefreshAudioForMissingCover)
                    {
                        this.logger.Info($"跳过 已存在MediaInfo: {displayName}");
                        return;
                    }

                    this.logger.Info($"音频已存在MediaInfo但缺少封面，继续提取: {displayName}");
                }

                if (deserializeResult == MediaInfoDocument.MediaInfoRestoreResult.Failed)
                {
                    this.logger.Info($"无Json媒体信息存在，刷新开始: {displayName}");
                }
                else
                {
                    this.logger.Info($"继续刷新并补全封面: {displayName}");
                }

                item.DateLastRefreshed = new DateTimeOffset();

                if (shouldRefreshAudioForMissingCover)
                {
                    refreshOptions.ImageRefreshMode = MetadataRefreshMode.FullRefresh;
                }
                
                await Plugin.ProviderManager
                    .RefreshSingleItem(item, refreshOptions, collectionFolders, dummyLibraryOptions, cancellationToken)
                    .ConfigureAwait(false);

                this.logger.Info($"写入 JSON: {displayName}");
                Plugin.MediaSourceInfoStore.OverWriteToFile(item);
                if (item is Audio)
                {
                    Plugin.AudioMetadataStore.OverWriteToFile(item);
                    Plugin.CoverStore.OverWriteToFile(item);
                }

                this.logger.Info($"完成: {displayName}");
            }
        }

    }
}
