using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Patch;

namespace MediaInfoKeeper.Services
{
    internal static class MediaInfoRestoreService
    {
        private const int MaxRestoreAttempts = 3;
        private static ILogger logger;
        private static readonly ConcurrentDictionary<long, long> restoreVersionMap =
            new ConcurrentDictionary<long, long>();

        public static void QueueRestore(BaseItem item, int delaySeconds)
        {
            logger ??= Plugin.Instance?.Logger;

            if (item == null || item.InternalId == 0)
            {
                return;
            }

            try
            {
                var workItem = Plugin.LibraryManager?.GetItemById(item.InternalId) ?? item;
                if (workItem == null ||
                    workItem.InternalId == 0 ||
                    !LibraryService.IsFileShortcut(workItem.Path ?? workItem.FileName))
                {
                    return;
                }

                if (Plugin.LibraryService != null && !Plugin.LibraryService.IsItemInScope(workItem))
                {
                    return;
                }

                BackupIfNeeded(workItem);

                var itemId = workItem.InternalId;
                var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                restoreVersionMap[itemId] = version;

                logger?.Debug($"已加入媒体信息延迟检查队列: {workItem.FileName ?? workItem.Path} InternalId:{itemId}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);

                        for (var attempt = 1; attempt <= MaxRestoreAttempts; attempt++)
                        {
                            if (!restoreVersionMap.TryGetValue(itemId, out var latest) || latest != version)
                            {
                                return;
                            }

                            workItem = Plugin.LibraryManager?.GetItemById(itemId) ?? workItem;
                            if (workItem == null ||
                                workItem.InternalId == 0 ||
                                !LibraryService.IsFileShortcut(workItem.Path ?? workItem.FileName))
                            {
                                return;
                            }

                            if (Plugin.LibraryService != null && !Plugin.LibraryService.IsItemInScope(workItem))
                            {
                                return;
                            }

                            var hasMediaInfo = Plugin.MediaInfoService?.HasMediaInfo(workItem) == true;
                            var hasCover = workItem is Audio && Plugin.LibraryService.HasCover(workItem);
                            var isHealthy = workItem is Audio ? hasMediaInfo && hasCover : hasMediaInfo;

                            if (isHealthy)
                            {
                                if (attempt < MaxRestoreAttempts)
                                {
                                    logger?.Debug(
                                        workItem is Audio
                                            ? $"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查 MediaInfo 和封面均存在，继续观察"
                                            : $"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查 MediaInfo 存在，继续观察");
                                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds * attempt * attempt)).ConfigureAwait(false);
                                    continue;
                                }

                                logger?.Debug(
                                    workItem is Audio
                                        ? $"{workItem.FileName ?? workItem.Path} 连续检查后 MediaInfo 和封面均存在，跳过恢复"
                                        : $"{workItem.FileName ?? workItem.Path} 连续检查后 MediaInfo 存在，跳过恢复");
                                restoreVersionMap.TryRemove(itemId, out _);
                                return;
                            }

                            if (workItem is Audio)
                            {
                                if (!hasMediaInfo && !hasCover)
                                {
                                    logger?.Info($"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查发现 MediaInfo 和封面都缺失，开始恢复媒体信息");
                                }
                                else if (!hasMediaInfo)
                                {
                                    logger?.Info($"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查发现 MediaInfo 缺失，开始恢复媒体信息");
                                }
                                else
                                {
                                    logger?.Info($"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查发现封面缺失，开始恢复媒体信息");
                                }
                            }
                            else
                            {
                                logger?.Info($"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查发现 MediaInfo 缺失，开始恢复媒体信息");
                            }

                            var restoreResult = Plugin.MediaSourceInfoStore?.ApplyToItem(workItem)
                                ?? MediaInfoDocument.MediaInfoRestoreResult.Failed;
                            
                            if (workItem is Audio)
                            {
                                Plugin.AudioMetadataStore?.ApplyToItem(workItem);
                                Plugin.LyricsStore?.ApplyToItem(workItem);
                                Plugin.EmbeddedCoverStore?.ApplyToItem(workItem);
                            }
                            
                            if (restoreResult != MediaInfoDocument.MediaInfoRestoreResult.Failed)
                            {
                                if (workItem is Video &&!Plugin.IntroScanService.HasIntroMarkers(workItem))
                                {
                                    Plugin.ChaptersStore?.ApplyToItem(workItem);
                                }

                                logger?.Info($"恢复媒体信息完成: {workItem.FileName ?? workItem.Path}");
                                restoreVersionMap.TryRemove(itemId, out _);
                                return;
                            }

                            logger?.Warn($"恢复媒体信息失败，不再重试: {workItem.FileName ?? workItem.Path}");
                            restoreVersionMap.TryRemove(itemId, out _);
                            return;
                        }

                        restoreVersionMap.TryRemove(itemId, out _);
                        logger?.Debug($"连续检查结束，未触发恢复: {workItem?.FileName ?? workItem?.Path ?? itemId.ToString()}");
                    }
                    catch (Exception ex)
                    {
                        restoreVersionMap.TryRemove(itemId, out _);
                        logger?.Error("延迟恢复 MediaInfo 失败");
                        logger?.Error(ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                logger?.Error("排队恢复 MediaInfo 失败");
                logger?.Error(ex.Message);
            }
        }

        private static void BackupIfNeeded(BaseItem item)
        {
            logger?.Debug($"检查媒体信息备份: {item.FileName ?? item.Path}");

            if (!Plugin.MediaSourceInfoStore.HasInFile(item) && Plugin.MediaInfoService.HasMediaInfo(item))
            {
                Plugin.MediaSourceInfoStore.WriteToFile(item);
                if (item is Audio)
                {
                    Plugin.AudioMetadataStore.WriteToFile(item);
                    Plugin.LyricsStore.WriteToFile(item);
                    Plugin.EmbeddedCoverStore.WriteToFile(item);
                }

                if (item is Video &&
                    !Plugin.ChaptersStore.HasInFile(item) &&
                    Plugin.IntroScanService.HasIntroMarkers(item))
                {
                    Plugin.ChaptersStore.WriteToFile(item);
                }
            }
        }
    }
}
