using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Patch;

namespace MediaInfoKeeper.Services
{
    internal static class MediaInfoRecoveryService
    {
        private const int MaxRestoreAttempts = 3;
        private const long QueueDebounceMilliseconds = 2000;
        private static ILogger logger;
        private static long restoreSequence;
        private static readonly ConcurrentDictionary<long, long> restoreVersionMap =
            new ConcurrentDictionary<long, long>();
        private static readonly ConcurrentDictionary<long, byte> restoreExecutionMap =
            new ConcurrentDictionary<long, byte>();
        private static readonly ConcurrentDictionary<long, long> restoreEnqueueTicks =
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
                var now = Environment.TickCount64;

                while (restoreExecutionMap.ContainsKey(itemId))
                {
                    logger?.Debug($"媒体信息恢复已在执行，忽略重复提交: {workItem.FileName ?? workItem.Path} InternalId:{itemId}");
                    return;
                }

                while (true)
                {
                    if (!restoreEnqueueTicks.TryGetValue(itemId, out var lastEnqueue))
                    {
                        if (restoreEnqueueTicks.TryAdd(itemId, now))
                        {
                            break;
                        }

                        continue;
                    }

                    if (now - lastEnqueue < QueueDebounceMilliseconds)
                    {
                        logger?.Debug($"媒体信息恢复短时间内重复排队，忽略提交: {workItem.FileName ?? workItem.Path} InternalId:{itemId}");
                        return;
                    }

                    if (restoreEnqueueTicks.TryUpdate(itemId, now, lastEnqueue))
                    {
                        break;
                    }
                }

                var version = Interlocked.Increment(ref restoreSequence);
                restoreVersionMap[itemId] = version;

                logger?.Debug($"已加入媒体信息延迟检查队列: {workItem.FileName ?? workItem.Path} InternalId:{itemId}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);

                        if (!restoreExecutionMap.TryAdd(itemId, 0))
                        {
                            logger?.Debug($"媒体信息恢复已在执行，忽略重复提交: {workItem.FileName ?? workItem.Path} InternalId:{itemId}");
                            return;
                        }

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
                            var hasRefreshBackup = workItem is Video && Plugin.CoverStore?.HasInFile(workItem) == true;
                            var hasPrimaryImage = workItem is Video && workItem.HasImage(ImageType.Primary);
                            var needsVideoPrimaryRestore = hasRefreshBackup && !hasPrimaryImage;
                            var isHealthy = workItem switch
                            {
                                Audio => hasMediaInfo && hasCover,
                                Video => hasMediaInfo && !needsVideoPrimaryRestore,
                                _ => hasMediaInfo
                            };

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
                                    logger?.Info($"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查发现 MediaInfo 和封面都缺失，开始恢复");
                                }
                                else if (!hasMediaInfo)
                                {
                                    logger?.Info($"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查发现 MediaInfo 缺失，开始恢复");
                                }
                                else
                                {
                                    logger?.Info($"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查发现封面缺失，开始恢复");
                                }
                            }
                            else if (workItem is Video && needsVideoPrimaryRestore)
                            {
                                logger?.Info($"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查发现 Primary 图片缺失，开始恢复");
                            }
                            else
                            {
                                logger?.Info($"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查发现 MediaInfo 缺失，开始恢复");
                            }

                            var restoreResult = Plugin.MediaSourceInfoStore?.ApplyToItem(workItem)
                                ?? MediaInfoDocument.MediaInfoRestoreResult.Failed;

                            var imageRestoreResult = MediaInfoDocument.MediaInfoRestoreResult.Failed;
                            if (workItem is Audio)
                            {
                                Plugin.AudioMetadataStore?.ApplyToItem(workItem);
                                imageRestoreResult = Plugin.CoverStore?.ApplyToItem(workItem)
                                    ?? MediaInfoDocument.MediaInfoRestoreResult.Failed;
                            }
                            else if (workItem is Video)
                            {
                                imageRestoreResult = Plugin.CoverStore?.ApplyToItem(workItem)
                                    ?? MediaInfoDocument.MediaInfoRestoreResult.Failed;
                            }

                            var hasMediaInfoAfterRestore = Plugin.MediaInfoService?.HasMediaInfo(workItem) == true;
                            var hasCoverAfterRestore = workItem is Audio && Plugin.LibraryService.HasCover(workItem);
                            var hasPrimaryImageAfterRestore = workItem is Video && workItem.HasImage(ImageType.Primary);
                            var isHealthyAfterRestore = workItem switch
                            {
                                Audio => hasMediaInfoAfterRestore && hasCoverAfterRestore,
                                Video => hasMediaInfoAfterRestore && (hasPrimaryImageAfterRestore || !hasRefreshBackup),
                                _ => hasMediaInfoAfterRestore
                            };

                            if (isHealthyAfterRestore)
                            {
                                if (workItem is Video &&!Plugin.IntroScanService.HasIntroMarkers(workItem))
                                {
                                    Plugin.ChaptersStore?.ApplyToItem(workItem);
                                }

                                logger?.Info($"恢复媒体信息完成: {workItem.FileName ?? workItem.Path}");
                                restoreVersionMap.TryRemove(itemId, out _);
                                return;
                            }

                            var incompleteReason = !hasMediaInfoAfterRestore
                                ? "MediaInfo 缺失"
                                : workItem is Audio && !hasCoverAfterRestore
                                    ? "封面缺失"
                                    : workItem is Video && hasRefreshBackup && !hasPrimaryImageAfterRestore
                                        ? "Primary 图片缺失"
                                        : "状态未完整";
                            logger?.Warn($"{workItem.FileName ?? workItem.Path} 恢复后仍不完整: {incompleteReason}");
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
                    finally
                    {
                        // 删除之前备份的图片
                        if (workItem is Video)
                        {
                            Plugin.CoverStore?.DeleteCoverPersist(workItem);
                        }

                        restoreExecutionMap.TryRemove(itemId, out _);
                        restoreEnqueueTicks.TryRemove(itemId, out _);
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

            // 刷新元数据前备份封面
            if (item is Video && item.HasImage(ImageType.Primary))
            {
                Plugin.CoverStore?.OverWriteToFile(item);
            }

            if (!Plugin.MediaSourceInfoStore.HasInFile(item) && Plugin.MediaInfoService.HasMediaInfo(item))
            {
                Plugin.MediaSourceInfoStore.WriteToFile(item);
                if (item is Audio)
                {
                    Plugin.AudioMetadataStore.WriteToFile(item);
                    Plugin.CoverStore.WriteToFile(item);
                }

                if (item is Video && !Plugin.ChaptersStore.HasInFile(item) && Plugin.IntroScanService.HasIntroMarkers(item))
                {
                    Plugin.ChaptersStore.WriteToFile(item);
                }
                
                if (item is Video && item.HasImage(ImageType.Primary))
                {
                    Plugin.CoverStore?.WriteToFile(item);
                }
            }
        }
    }
}
