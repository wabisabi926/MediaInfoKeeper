using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ScanRecentIntroTask : IScheduledTask
    {
        private readonly ILogger logger;
        public ScanRecentIntroTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            _ = libraryManager;
        }

        public string Key => "MediaInfoKeeperScanRecentIntroTask";

        public string Name => "5.扫描片头（测试用）";

        public string Description => "对最近入库的剧集执行片头检测。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("最近入库片头扫描计划任务开始");
            await Plugin.IntroScanService
                .ScanRecentIntroAsync(cancellationToken, progress)
                .ConfigureAwait(false);
            this.logger.Info("最近入库片头扫描计划任务完成");
        }
    }
}
