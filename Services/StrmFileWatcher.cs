using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.IO;

namespace MediaInfoKeeper.Services
{
    /// <summary>
    /// 监听媒体库路径下的新入库 .strm 与视频文件，独立于元数据刷新链路进行媒体信息恢复校验。
    /// </summary>
    public sealed class StrmFileWatcher : IDisposable
    {
        private readonly ILibraryManager libraryManager;
        private readonly LibraryService libraryService;
        private readonly ILogger logger;
        private readonly DirectoryService directoryService;
        private readonly object syncRoot = new object();
        private readonly Dictionary<string, FileSystemWatcher> watchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> pendingPaths =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Timer flushTimer;

        private volatile bool enabled;
        private volatile bool disposed;
        private volatile int refreshDelaySeconds;

        private static readonly TimeSpan RecentCreationThreshold = TimeSpan.FromSeconds(3);

        public StrmFileWatcher(ILibraryManager libraryManager, LibraryService libraryService, ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.libraryService = libraryService;
            this.logger = logger;
            this.directoryService = new DirectoryService(logger, Plugin.FileSystem);
        }

        /// <summary>
        /// 配置监听开关和延迟扫描时间。
        /// </summary>
        public void Configure(bool isEnabled, int delaySeconds)
        {
            if (this.disposed)
            {
                return;
            }

            this.enabled = isEnabled;
            this.refreshDelaySeconds = Math.Max(-1, delaySeconds);
            RebuildWatchers(isEnabled);
        }

        /// <summary>
        /// 根据当前配置重建文件监听器。
        /// </summary>
        private void RebuildWatchers(bool isEnabled)
        {
            lock (this.syncRoot)
            {
                foreach (var existing in this.watchers.Values)
                {
                    try
                    {
                        existing.EnableRaisingEvents = false;
                        existing.Dispose();
                    }
                    catch
                    {
                        // ignore cleanup failure
                    }
                }

                this.watchers.Clear();
                DisposeFlushTimer();
                this.pendingPaths.Clear();

                if (!isEnabled)
                {
                    this.logger?.Info("StrmFileWatcher 已禁用");
                    return;
                }

                var roots = (this.libraryService?.GetAllLibraryPaths() ?? new List<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var root in roots)
                {
                    try
                    {
                        var watcher = new FileSystemWatcher(root, "*")
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                            InternalBufferSize = 64 * 1024,
                            EnableRaisingEvents = true
                        };

                        watcher.Created += (sender, args) => PathQueue(args?.FullPath, isChangedEvent: false);
                        watcher.Changed += (sender, args) => PathQueue(args?.FullPath, isChangedEvent: true);
                        watcher.Renamed += (sender, args) => PathQueue(args?.FullPath, isChangedEvent: false);

                        this.watchers[root] = watcher;
                    }
                    catch (Exception ex)
                    {
                        this.logger?.Warn($"StrmFileWatcher 监听路径失败: {root}");
                        this.logger?.Warn(ex.Message);
                    }
                }

                this.logger?.Debug(
                    $"StrmFileWatcher 已启动，监听路径: {string.Join(", ", this.watchers.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))}");
            }
        }

        /// <summary>
        /// 校验事件路径并加入延迟扫描队列。
        /// Created 和 Renamed 仅触发扫描，Changed 同时触发扫描与 restore，差异仅由 isChangedEvent 控制。
        /// </summary>
        private void PathQueue(string path, bool isChangedEvent)
        {
            if (!this.enabled || this.disposed || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!isChangedEvent && !IsRefreshNotificationEnabled())
            {
                return;
            }

            try
            {
                if (!this.libraryManager.IsVideoFile(path.AsSpan()))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                this.logger?.Debug($"StrmFileWatcher 判断视频文件失败: {path}");
                this.logger?.Debug(ex.Message);
                return;
            }

            if (isChangedEvent && IsRecentlyCreated(path))
            {
                this.logger?.Debug($"StrmFileWatcher 忽略疑似新建触发的 Changed 事件: {path}");
                return;
            }

            lock (this.syncRoot)
            {
                this.pendingPaths[path] = isChangedEvent ||
                    (this.pendingPaths.TryGetValue(path, out var requiresRestore) && requiresRestore);

                var delay = TimeSpan.FromSeconds(Math.Max(0, this.refreshDelaySeconds));
                if (this.flushTimer == null)
                {
                    this.flushTimer = new Timer(_ => _ = Task.Run(FlushPendingPathsAsync), null, delay, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    this.flushTimer.Change(delay, Timeout.InfiniteTimeSpan);
                }
            }
        }

        /// <summary>
        /// 处理当前累计的新入库路径并触发扫描与恢复。
        /// Created 和 Renamed 进入的路径只扫描，Changed 进入的路径在扫描后还会尝试执行 restore。
        /// </summary>
        private async Task FlushPendingPathsAsync()
        {
            KeyValuePair<string, bool>[] pendingEntries;
            lock (this.syncRoot)
            {
                if (this.pendingPaths.Count == 0 || !this.enabled || this.disposed)
                {
                    return;
                }

                pendingEntries = this.pendingPaths.ToArray();
                this.pendingPaths.Clear();
            }

            var refreshTargets = new Dictionary<long, BaseItem>();
            var restorePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in pendingEntries)
            {
                var path = entry.Key;
                var target = ResolveRefreshTarget(path);
                if (target != null && target.InternalId != 0)
                {
                    refreshTargets[target.InternalId] = target;
                }

                if (!entry.Value)
                {
                    continue;
                }

                try
                {
                    var item = this.libraryManager.FindByPath(path, false) as BaseItem
                        ?? this.libraryManager.FindByPath(path, true) as BaseItem;
                    if (item != null && LibraryService.IsFileShortcut(item.Path ?? item.FileName))
                    {
                        restorePaths.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    this.logger?.Debug($"StrmFileWatcher 判断是否需要恢复失败: {path}");
                    this.logger?.Debug(ex.Message);
                    if (LibraryService.IsFileShortcut(path))
                    {
                        restorePaths.Add(path);
                    }
                }
            }

            foreach (var target in refreshTargets.Values)
            {
                if (!IsRefreshNotificationEnabled())
                {
                    break;
                }

                await QueueRefreshAsync(target).ConfigureAwait(false);
            }

            foreach (var restorePath in restorePaths)
            {
                QueueRestore(restorePath);
            }
        }

        /// <summary>
        /// 扫描指定目标条目，促使 Emby 发现新入库文件。
        /// </summary>
        private async Task QueueRefreshAsync(BaseItem target)
        {
            try
            {
                if (!this.enabled || this.disposed || target == null || target.InternalId == 0)
                {
                    return;
                }

                var refreshOptions = new MetadataRefreshOptions(this.directoryService)
                {
                    Recursive = true,
                    EnableRemoteContentProbe = false,
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllMetadata = false,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllImages = false,
                    EnableThumbnailImageExtraction = false,
                    EnableSubtitleDownloading = false
                };

                var collectionFolders = this.libraryManager.GetCollectionFolders(target).Cast<BaseItem>().ToArray();
                var libraryOptions = this.libraryManager.GetLibraryOptions(target);
                this.logger?.Info($"新入库文件，通知 Emby 刷新 {target.Path ?? target.Name}");

                await RefreshTaskRunner.RunAsync(
                        () => Plugin.ProviderManager.RefreshSingleItem(
                            target,
                            refreshOptions,
                            collectionFolders,
                            libraryOptions,
                            CancellationToken.None))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger?.Error("StrmFileWatcher 提前刷新失败");
                this.logger?.Error(ex.Message);
            }
        }

        /// <summary>
        /// 根据文件路径解析用于扫描的目标条目。
        /// </summary>
        private BaseItem ResolveRefreshTarget(string path)
        {
            BaseItem item = null;
            try
            {
                item = this.libraryManager.FindByPath(path, false) as BaseItem;
                if (item == null)
                {
                    item = this.libraryManager.FindByPath(path, true) as BaseItem;
                }
            }
            catch
            {
            }

            if (item != null)
            {
                if (item is Folder folder)
                {
                    return folder;
                }

                var parent = item.GetParent();
                if (parent != null)
                {
                    return parent;
                }

                return item;
            }

            var currentPath = Path.GetDirectoryName(path);
            while (!string.IsNullOrWhiteSpace(currentPath))
            {
                var folder = this.libraryManager.FindByPath(currentPath, true) as Folder;
                if (folder != null)
                {
                    return folder;
                }

                currentPath = Path.GetDirectoryName(currentPath);
            }

            return null;
        }

        private bool IsRefreshNotificationEnabled()
        {
            return this.refreshDelaySeconds >= 0;
        }

        /// <summary>
        /// 为 strm 条目排队执行媒体信息恢复。
        /// </summary>
        private void QueueRestore(string strmPath)
        {
            try
            {
                if (!this.enabled || this.disposed)
                {
                    return;
                }

                var item = this.libraryManager.FindByPath(strmPath, false) as BaseItem;
                if (item == null)
                {
                    item = this.libraryManager.FindByPath(strmPath, true) as BaseItem;
                }

                if (item == null || item.InternalId == 0)
                {
                    this.logger?.Debug($"StrmFileWatcher 未找到条目: {strmPath}");
                    return;
                }

                if (!LibraryService.IsFileShortcut(item.Path ?? item.FileName))
                {
                    return;
                }

                MediaInfoRecoveryService.QueueRestore(item, 5);
            }
            catch (Exception ex)
            {
                this.logger?.Error("StrmFileWatcher 排队恢复失败");
                this.logger?.Error(ex.Message);
            }
        }

        /// <summary>
        /// 释放文件监听和延迟扫描资源。
        /// </summary>
        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.enabled = false;

            lock (this.syncRoot)
            {
                foreach (var watcher in this.watchers.Values)
                {
                    try
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }
                }

                this.watchers.Clear();
                this.pendingPaths.Clear();
                DisposeFlushTimer();
            }
        }

        /// <summary>
        /// 判断路径是否对应刚创建不久的文件。
        /// </summary>
        private bool IsRecentlyCreated(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                if (!Plugin.FileSystem.FileExists(path))
                {
                    return false;
                }

                var createdUtc = File.GetCreationTimeUtc(path);
                if (createdUtc == DateTime.MinValue || createdUtc == DateTime.MaxValue)
                {
                    return false;
                }

                return DateTime.UtcNow - createdUtc <= RecentCreationThreshold;
            }
            catch (Exception ex)
            {
                this.logger?.Debug($"StrmFileWatcher 获取创建时间失败: {path}");
                this.logger?.Debug(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 释放延迟扫描计时器。
        /// </summary>
        private void DisposeFlushTimer()
        {
            if (this.flushTimer == null)
            {
                return;
            }

            try
            {
                this.flushTimer.Dispose();
            }
            catch
            {
            }

            this.flushTimer = null;
        }
    }
}
