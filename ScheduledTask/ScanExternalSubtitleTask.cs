using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ScanExternalSubtitleTask : IScheduledTask
    {
        private readonly ILogger logger;

        public ScanExternalSubtitleTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperScanExternalSubtitleTask";

        public string Name => "8.扫描外挂字幕";

        public string Description => "对计划任务范围内的视频独立扫描外挂字幕，发现变更时更新字幕媒体流。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("外挂字幕扫描计划任务开始");

            if (Plugin.ExternalSubtitle == null || !Plugin.ExternalSubtitle.IsAvailable)
            {
                this.logger.Warn("外挂字幕扫描不可用，缺少 Emby 所需依赖");
                progress?.Report(100.0);
                return;
            }

            var items = FetchScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                progress?.Report(100.0);
                this.logger.Info("外挂字幕扫描计划任务完成，条目数 0");
                return;
            }

            var refreshOptions = Plugin.ExternalSubtitle.GetRefreshOptions();
            var completed = 0;
            var tasks = items
                .Select(async item =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ProcessItemAsync(item, refreshOptions, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        this.logger.Info($"外挂字幕扫描已取消: {item.Path ?? item.Name}");
                    }
                    catch (Exception ex)
                    {
                        this.logger.Error($"外挂字幕扫描失败: {item.Path ?? item.Name}");
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
            this.logger.Info("外挂字幕扫描计划任务完成");
        }

        private List<BaseItem> FetchScopedItems()
        {
            var scopePaths = Plugin.LibraryService.GetScopedLibraryPaths(
                Plugin.Instance.Options.MainPage.ScheduledTaskLibraries,
                out var hasScope);
            if (hasScope && !scopePaths.Any())
            {
                this.logger.Info("外挂字幕扫描条目数 0(范围内未匹配到媒体库)");
                return new List<BaseItem>();
            }

            var items = Plugin.LibraryService.FetchScopedVideoItems(scopePaths);
            this.logger.Info($"外挂字幕扫描条目数 {items.Count}");
            return items;
        }

        private async Task ProcessItemAsync(
            BaseItem item,
            MediaBrowser.Controller.Providers.MetadataRefreshOptions refreshOptions,
            CancellationToken cancellationToken)
        {
            var displayName = item.FileName ?? item.Path ?? item.Name;
            if (!Plugin.LibraryService.IsItemInScope(item))
            {
                return;
            }

            if (!Plugin.ExternalSubtitle.HasExternalSubtitleChanged(item, refreshOptions.DirectoryService, true))
            {
                return;
            }

            await Plugin.ExternalSubtitle
                .UpdateExternalSubtitles(item, refreshOptions, false, cancellationToken)
                .ConfigureAwait(false);
            this.logger.Info($"外挂字幕已更新: {displayName}");
        }
    }
}
