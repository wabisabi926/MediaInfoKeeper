using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Patch;
using static MediaInfoKeeper.Options.MainPageOptions;

namespace MediaInfoKeeper.ScheduledTask
{
    public class RefreshRecentMetadataTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;

        public RefreshRecentMetadataTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }
        public string Key => "MediaInfoKeeperRefreshRecentMetadataTask";

        public string Name => "1.刷新媒体元数据";

        public string Description => "全局媒体库范围内，按“最近入库时间窗口（天）”筛选（0=不限制），刷新元数据（可选覆盖或补全），之后会从 JSON 恢复媒体信息。";

        public string Category => Plugin.TaskCategoryName;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("最近条目刷新元数据计划任务开始");

            var items = FetchRecentItems();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100.0);
                this.logger.Info("计划任务完成，条目数 0");
                return;
            }

            var replaceMetadata = ShouldReplaceMetadata();
            var replaceImages = ShouldReplaceImages();
            var replaceThumbnails = ShouldReplaceThumbnails();
            this.logger.Info($"计划任务条目数{total}，元数据覆盖{replaceMetadata}，图片覆盖{replaceImages}，视频缩略图覆盖{replaceThumbnails}");

            var current = 0;
            foreach (var item in items)
            {
                var created = item.DateCreated == default ? "unknown" : item.DateCreated.ToString("u");
                this.logger.Info($"[{current + 1}/{total}] 刷新元数据 {item.FileName ?? item.Path} 入库日期 = {created}");

                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info("计划任务已取消");
                    return;
                }

                try
                {
                    var options = BuildRefreshOptions(replaceMetadata, replaceImages, replaceThumbnails);
                    var collectionFolders = this.libraryManager.GetCollectionFolders(item).Cast<BaseItem>().ToArray();
                    var libraryOptions = this.libraryManager.GetLibraryOptions(item);

                    await Plugin.ProviderManager
                        .RefreshSingleItem(item, options, collectionFolders, libraryOptions, cancellationToken)
                        .ConfigureAwait(false);
                    // 刷新完元数据要重新从json恢复媒体信息，
                    // 非strm会重新 ffprobe，但是没有allow所以会拦截，
                    // strm会丢失信息，所以重新恢复，启用元数据变动监听会恢复，不必重复恢复
                    // Plugin.MediaSourceInfoStore.ApplyToItem(item);
                    // Plugin.ChaptersStore.ApplyToItem(item);
                }
                catch (OperationCanceledException)
                {
                    this.logger.Info($"计划任务已取消 {item.Path}");
                    return;
                }
                catch (Exception e)
                {
                    this.logger.Error($"计划任务失败: {item.Path}");
                    this.logger.Error(e.Message);
                    this.logger.Debug(e.StackTrace);
                }

                current++;
                progress.Report(current / (double)total * 100);
            }

            this.logger.Info("最近条目刷新元数据计划任务完成");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            };

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(16).Ticks
            };
        }

        private List<BaseItem> FetchRecentItems()
        {
            var cutoff = Plugin.Instance.Options.MainPage.RecentItemsDays > 0
                ? DateTime.UtcNow.AddDays(-Plugin.Instance.Options.MainPage.RecentItemsDays)
                : (DateTime?)null;

            return Plugin.LibraryService.FetchRecentItems(cutoff, true, includeAudio: true);
        }

        private MetadataRefreshOptions BuildRefreshOptions(bool replaceMetadata, bool replaceImages, bool replaceThumbnails)
        {
            var directoryService = new DirectoryService(this.logger, Plugin.FileSystem);
            return new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = replaceMetadata,
                ReplaceAllImages = replaceImages,
                ReplaceThumbnailImages = replaceThumbnails,
                EnableThumbnailImageExtraction = Plugin.Instance.Options.MetaData.EnableImageCapture
            };
        }

        private bool ShouldReplaceMetadata()
        {
            return Plugin.Instance.Options.MainPage.RefreshMetadataMode == RefreshModeOption.Replace;
        }

        private bool ShouldReplaceImages()
        {
            return Plugin.Instance.Options.MainPage.ReplaceExistingImages;
        }

        private bool ShouldReplaceThumbnails()
        {
            return Plugin.Instance.Options.MainPage.ReplaceExistingVideoPreviewThumbnails;
        }
    }
}
