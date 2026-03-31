using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Patch;

namespace MediaInfoKeeper.ScheduledTask
{
    public class RebuildSearchIndexTask : IScheduledTask
    {
        private readonly ILogger logger;

        public RebuildSearchIndexTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperRebuildSearchIndexTask";

        public string Name => "6.重建搜索索引";

        public string Description => "按当前增强搜索配置，强制重建 Emby 搜索 FTS 索引。用于应用新的分词归一化规则或修复历史索引。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0);

            this.logger.Info("计划任务执行: 重建搜索索引");
            var rebuildResult = ChineseSearch.RebuildSearchIndex();
            if (!rebuildResult)
            {
                throw new InvalidOperationException("重建搜索索引失败，请查看 Emby 系统日志中 EnhanceChineseSearch 相关记录。");
            }

            progress?.Report(100);
            this.logger.Info("计划任务完成: 重建搜索索引");
            return Task.CompletedTask;
        }
    }
}
