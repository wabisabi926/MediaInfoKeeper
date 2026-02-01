using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ScanRecentIntroTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        public ScanRecentIntroTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }

        public string Key => "MediaInfoKeeperScanRecentIntroTask";

        public string Name => "2.扫描片头";

        public string Description => "全局媒体库范围内，按入库时间倒序取最近 N 条（“最近入库媒体筛选数量”）的剧集执行片头检测。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(1).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("最近入库片头扫描计划任务开始");
            var episodes = FetchRecentEpisodes();
            await Plugin.IntroScanService
                .ScanEpisodesAsync(episodes, cancellationToken, progress)
                .ConfigureAwait(false);
            this.logger.Info("最近入库片头扫描计划任务完成");
        }

        private List<Episode> FetchRecentEpisodes()
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            var episodes = this.libraryManager.GetItemList(query)
                .OfType<Episode>()
                .Where(i => i.ExtraType is null)
                .OrderByDescending(i => i.DateCreated)
                .Take(Math.Max(1, Plugin.Instance.Options.MainPage.RecentItemsLimit))
                .ToList();

            this.logger.Info($"扫描条目数 {episodes.Count}");
            return episodes;
        }
    }
}
