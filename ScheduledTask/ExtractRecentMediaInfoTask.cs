using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
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

            await MediaInfoTaskRunner.ProcessItemsAsync(
                    items,
                    item => ProcessItemAsync(item, "Recent Scheduled Task", cancellationToken),
                    this.logger,
                    cancellationToken,
                    progress)
                .ConfigureAwait(false);

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

            var items = Plugin.LibraryService.FetchScopedVideoItems(scopePaths, true, limit);
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

                var collectionFolders = (BaseItem[])this.libraryManager.GetCollectionFolders(item);
                var libraryOptions = this.libraryManager.GetLibraryOptions(item);

                var dummyLibraryOptions = LibraryService.CopyLibraryOptions(libraryOptions);
                foreach (var option in dummyLibraryOptions.TypeOptions)
                {
                }

                var deserializeResult = await Plugin.MediaInfoService
                    .DeserializeMediaInfo(item, directoryService, source, false)
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

                this.logger.Info($"无Json媒体信息存在，刷新开始: {displayName}");
                item.DateLastRefreshed = new DateTimeOffset();

                await Plugin.ProviderManager
                    .RefreshSingleItem(item, refreshOptions, collectionFolders, dummyLibraryOptions, cancellationToken)
                    .ConfigureAwait(false);

                this.logger.Info($"写入 JSON: {displayName}");
                await Plugin.MediaInfoService.SerializeMediaInfo(item.InternalId, directoryService, true, source)
                    .ConfigureAwait(false);

                this.logger.Info($"完成: {displayName}");
            }
        }

    }
}
