using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ExtractMediaInfoTask : IScheduledTask
    {
        private readonly ILogger logger;

        public ExtractMediaInfoTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperExtractMediaInfoTask";

        public string Name => "4.恢复媒体信息";

        public string Description => "对计划任务范围内！的条目,存在 JSON 则恢复，不存在则跳过";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("计划任务执行");

            var items = FetchScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100.0);
                this.logger.Info("计划任务完成(0 个条目)");
                return;
            }

            await MediaInfoTaskRunner.ProcessItemsAsync(
                    items,
                    item => ProcessItemAsync(item, "Scheduled Task", cancellationToken),
                    this.logger,
                    cancellationToken,
                    progress)
                .ConfigureAwait(false);

            this.logger.Info("计划任务完成");
        }

        private List<BaseItem> FetchScopedItems()
        {
            var scopePaths = Plugin.LibraryService.GetScopedLibraryPaths(
                Plugin.Instance.Options.MainPage.ScheduledTaskLibraries,
                out var hasScope);
            if (hasScope && !scopePaths.Any())
            {
                this.logger.Info("计划任务条目数 0(范围内未匹配到媒体库)");
                return new List<BaseItem>();
            }

            var items = Plugin.LibraryService.FetchScopedVideoItems(scopePaths);
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

            var persistMediaInfo = item is Video && Plugin.Instance.Options.MainPage.PlugginEnabled;
            if (!persistMediaInfo)
            {
                this.logger.Info($"跳过 未开启持久化或非视频: {displayName}");
                return;
            }

            using (FfprobeGuard.Allow())
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

                var deserializeResult = await Plugin.MediaInfoService
                    .DeserializeMediaInfo(item, directoryService, source)
                    .ConfigureAwait(false);

                if (deserializeResult == MediaInfoService.MediaInfoRestoreResult.Restored)
                {
                    this.logger.Info($"从JSON 恢复成功: {displayName}");
                    return;
                }

                if (deserializeResult == MediaInfoService.MediaInfoRestoreResult.AlreadyExists)
                {
                    return;
                }

                this.logger.Info($"无Json媒体信息存在，跳过: {displayName}");
            }
        }

    }
}
