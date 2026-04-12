using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Options;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask
{
    public class DownloadDanmuXmlTask : IScheduledTask
    {
        private readonly ILogger logger;

        public DownloadDanmuXmlTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperDownloadDanmuXmlTask";

        public string Name => "9.下载弹幕";

        public string Description => "按计划任务媒体库范围，使用“最近入库时间窗口（天）”筛选（0=不限制），为电影和剧集下载弹幕。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("弹幕下载计划任务开始");

            if (Plugin.DanmuService?.IsEnabled != true)
            {
                this.logger.Info("弹幕下载计划任务跳过: 未配置弹幕 API BaseUrl");
                progress?.Report(100.0);
                return;
            }

            var items = FetchRecentScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                this.logger.Info("弹幕下载计划任务完成: 条目数 0");
                progress?.Report(100.0);
                return;
            }

            var networkFirst = string.Equals(
                Plugin.Instance?.Options?.MetaData?.DanmuFetchMode,
                MetaDataOptions.DanmuFetchModeOption.NetworkFirst.ToString(),
                StringComparison.Ordinal);
            this.logger.Info($"弹幕下载计划任务策略: {(networkFirst ? "网络优先(始终拉取并覆盖本地)" : "本地优先(本地存在则跳过)")}");

            var completed = 0;
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Plugin.DanmuService.QueueDownloadAsync(item.InternalId, networkFirst, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    this.logger.Info($"弹幕下载: 取消 {item.FileName}");
                }
                catch (Exception ex)
                {
                    this.logger.Info($"弹幕下载: 失败 {item.FileName} {ex.Message}");
                    this.logger.Debug(ex.StackTrace);
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(done / (double)total * 100);
                }
            }

            this.logger.Info($"弹幕下载计划任务完成: 条目数 {total}");
        }

        private List<BaseItem> FetchRecentScopedItems()
        {
            var cutoff = Plugin.Instance.Options.MainPage.RecentItemsDays > 0
                ? DateTime.UtcNow.AddDays(-Plugin.Instance.Options.MainPage.RecentItemsDays)
                : (DateTime?)null;
            var scopePaths = Plugin.LibraryService.GetScopedLibraryPaths(
                Plugin.Instance.Options.MainPage.ScheduledTaskLibraries,
                out var hasScope);

            if (hasScope && !scopePaths.Any())
            {
                this.logger.Info("弹幕下载计划任务跳过: 范围内未匹配到媒体库");
                return new List<BaseItem>();
            }

            var items = Plugin.LibraryService.FetchScopedVideoItems(scopePaths, true)
                .Where(item => cutoff == null || item.DateCreated >= cutoff)
                .Where(item => item is MediaBrowser.Controller.Entities.TV.Episode || item is MediaBrowser.Controller.Entities.Movies.Movie)
                .ToList();

            this.logger.Info($"弹幕下载计划任务条目数: {items.Count}, 天数窗口: {(cutoff == null ? "不限制" : Plugin.Instance.Options.MainPage.RecentItemsDays.ToString())}");
            return items;
        }
    }
}
