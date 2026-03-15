using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Patch;

namespace MediaInfoKeeper.Services
{
    internal static class MediaInfoRestoreService
    {
        private static ILogger logger;
        private static readonly ConcurrentDictionary<long, long> restoreVersionMap =
            new ConcurrentDictionary<long, long>();

        public static void QueueRestore(string source, BaseItem item, int delaySeconds)
        {
            logger ??= Plugin.Instance?.Logger;

            if (item == null || item.InternalId == 0)
            {
                return;
            }

            try
            {
                var workItem = Plugin.LibraryManager?.GetItemById(item.InternalId) ?? item;
                if (workItem == null || workItem.InternalId == 0 || !workItem.IsShortcut)
                {
                    return;
                }

                if (Plugin.LibraryService != null && !Plugin.LibraryService.IsItemInScope(workItem))
                {
                    return;
                }

                BackupIfNeeded(workItem, source);

                var itemId = workItem.InternalId;
                var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                restoreVersionMap[itemId] = version;

                logger?.Debug($"{source} 已加入媒体信息延迟检查队列: {workItem.FileName ?? workItem.Path} InternalId:{itemId}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);

                        if (!restoreVersionMap.TryGetValue(itemId, out var latest) || latest != version)
                        {
                            return;
                        }

                        restoreVersionMap.TryRemove(itemId, out _);

                        workItem = Plugin.LibraryManager?.GetItemById(itemId) ?? workItem;
                        if (workItem == null || workItem.InternalId == 0 || !workItem.IsShortcut)
                        {
                            return;
                        }

                        if (Plugin.LibraryService != null && !Plugin.LibraryService.IsItemInScope(workItem))
                        {
                            return;
                        }

                        if (Plugin.MediaInfoService?.HasMediaInfo(workItem) == true)
                        {
                            logger?.Debug($"{source} {workItem.FileName ?? workItem.Path} 延迟检查后 MediaInfo 仍存在，跳过恢复");
                            return;
                        }

                        logger?.Info($"{source} {workItem.FileName ?? workItem.Path} 延迟检查结束，媒体信息缺失，开始尝试恢复媒体信息");

                        var restoreResult = Plugin.MediaSourceInfoStore?.ApplyToItem(workItem)
                            ?? MediaInfoDocument.MediaInfoRestoreResult.Failed;
                        if (restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Failed)
                        {
                            logger?.Warn($"{source} 恢复媒体信息失败: {workItem.FileName ?? workItem.Path}");
                            return;
                        }

                        if (Plugin.IntroScanService == null || !Plugin.IntroScanService.HasIntroMarkers(workItem))
                        {
                            Plugin.ChaptersStore?.ApplyToItem(workItem);
                        }

                        logger?.Info($"{source} 恢复媒体信息完成: {workItem.FileName ?? workItem.Path}");
                    }
                    catch (Exception ex)
                    {
                        logger?.Error($"{source} 延迟恢复 MediaInfo 失败");
                        logger?.Error(ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                logger?.Error($"{source} 排队恢复 MediaInfo 失败");
                logger?.Error(ex.Message);
            }
        }

        private static void BackupIfNeeded(BaseItem item, string source)
        {
            logger?.Debug($"{source} 检查媒体信息备份: {item.FileName ?? item.Path}");

            if (!Plugin.MediaSourceInfoStore.HasInFile(item) && Plugin.MediaInfoService.HasMediaInfo(item))
            {
                Plugin.MediaSourceInfoStore.WriteToFile(item);

                if (!Plugin.ChaptersStore.HasInFile(item) && Plugin.IntroScanService.HasIntroMarkers(item))
                {
                    Plugin.ChaptersStore.WriteToFile(item);
                }
            }
        }
    }
}
