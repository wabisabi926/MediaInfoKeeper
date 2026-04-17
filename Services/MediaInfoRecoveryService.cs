using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
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

            try
            {
                if (item?.InternalId <= 0 || !LibraryService.IsFileShortcut(item.Path ?? item.FileName))
                {
                    return;
                }

                var workItem = item;
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

                BackupNow(workItem);

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
                                !LibraryService.IsFileShortcut(workItem.Path ?? workItem.FileName))
                            {
                                return;
                            }

                            var hasMediaInfo = Plugin.MediaInfoService?.HasMediaInfo(workItem) == true;
                            var isHealthy = workItem switch
                            {
                                Audio => hasMediaInfo,
                                Video => hasMediaInfo,
                                _ => hasMediaInfo
                            };

                            if (isHealthy)
                            {
                                if (attempt < MaxRestoreAttempts)
                                {
                                logger?.Debug(
                                    $"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查 MediaInfo 存在，继续观察");
                                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds * attempt * attempt)).ConfigureAwait(false);
                                    continue;
                                }

                                logger?.Debug($"{workItem.FileName ?? workItem.Path} 连续检查后 MediaInfo 存在，跳过恢复");
                                restoreVersionMap.TryRemove(itemId, out _);
                                return;
                            }

                            logger?.Info($"{workItem.FileName ?? workItem.Path} 第 {attempt}/{MaxRestoreAttempts} 次检查发现 MediaInfo 缺失，开始恢复");

                            RestoreNow(workItem);

                            var hasMediaInfoAfterRestore = Plugin.MediaInfoService?.HasMediaInfo(workItem) == true;
                            var isHealthyAfterRestore = workItem switch
                            {
                                Audio => hasMediaInfoAfterRestore,
                                Video => hasMediaInfoAfterRestore,
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

                            var incompleteReason = !hasMediaInfoAfterRestore ? "MediaInfo 缺失" : "状态未完整";
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

        internal static void BackupNow(BaseItem item)
        {
            logger ??= Plugin.Instance?.Logger;

            logger.Info($"备份媒体信息: {item.FileName ?? item.Path}");

            if (Plugin.MediaInfoService?.HasMediaInfo(item) == true &&
                Plugin.MediaSourceInfoStore?.HasInFile(item) != true)
            {
                Plugin.MediaSourceInfoStore?.WriteToFile(item);
            }

            if (item is Audio)
            {
                if (Plugin.AudioMetadataStore?.HasInFile(item) != true)
                {
                    Plugin.AudioMetadataStore?.WriteToFile(item);
                }

                return;
            }

            if (item is Video)
            {
                if (Plugin.ChaptersStore?.HasInFile(item) != true &&
                    Plugin.IntroScanService?.HasIntroMarkers(item) == true)
                {
                    Plugin.ChaptersStore?.WriteToFile(item);
                }
            }
        }

        internal static ItemUpdateType RestoreNow(BaseItem item)
        {
            logger ??= Plugin.Instance?.Logger;

            logger.Info($"恢复媒体信息 : {item.FileName ?? item.Path}");
            
            var updateType = ItemUpdateType.None;

            try
            {
                if (Plugin.MediaInfoService?.HasMediaInfo(item) != true)
                {
                    var restoreResult = Plugin.MediaSourceInfoStore?.ApplyToItem(item)
                        ?? MediaInfoDocument.MediaInfoRestoreResult.Failed;
                    if (restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored)
                    {
                        updateType |= ItemUpdateType.MetadataImport;
                    }
                }

                if (item is Audio)
                {
                    Plugin.AudioMetadataStore?.ApplyToItem(item);
                }
                else if (item is Video)
                {
                    if (Plugin.IntroScanService?.HasIntroMarkers(item) != true)
                    {
                        var chapterRestoreResult = Plugin.ChaptersStore?.ApplyToItem(item)
                            ?? MediaInfoDocument.MediaInfoRestoreResult.Failed;
                        if (chapterRestoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored)
                        {
                            updateType |= ItemUpdateType.MetadataImport;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Error("立即恢复 MediaInfo 失败");
                logger?.Error(ex.Message);
            }

            return updateType;
        }
    }
}
