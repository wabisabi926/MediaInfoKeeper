using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    /// <summary>
    /// 监听媒体库路径下的新入库 .strm 与视频文件，仅记录 Created 事件日志。
    /// </summary>
    public sealed class StrmFileWatcher : IDisposable
    {
        private readonly ILibraryManager libraryManager;
        private readonly ILibraryMonitor libraryMonitor;
        private readonly LibraryService libraryService;
        private readonly ILogger logger;
        private readonly object syncRoot = new object();
        private readonly Dictionary<string, FileSystemWatcher> watchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> pendingPaths =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Timer flushTimer;

        private volatile bool enabled;
        private volatile bool disposed;
        private volatile int refreshDelaySeconds;

        public StrmFileWatcher(
            ILibraryManager libraryManager,
            ILibraryMonitor libraryMonitor,
            LibraryService libraryService,
            ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.libraryMonitor = libraryMonitor;
            this.libraryService = libraryService;
            this.logger = logger;
        }

        /// <summary>
        /// 配置监听开关。
        /// </summary>
        public void Configure(bool isEnabled, int delaySeconds)
        {
            if (this.disposed)
            {
                return;
            }

            this.enabled = isEnabled;
            this.refreshDelaySeconds = Math.Max(0, delaySeconds);
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
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                            InternalBufferSize = 64 * 1024,
                            EnableRaisingEvents = true
                        };

                        watcher.Created += (sender, args) => OnCreated(args?.FullPath);
                        watcher.Changed += (sender, args) => QueueRestorePath(args?.FullPath);
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
        /// 记录新增文件事件。
        /// </summary>
        private void OnCreated(string path)
        {
            if (!this.enabled || this.disposed || string.IsNullOrWhiteSpace(path))
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

            if (LibraryService.IsFileShortcut(path))
            {
                this.logger?.Info($"新入库文件，{Path.GetFileName(path) ?? path}");
                try
                {
                    this.libraryMonitor?.ReportFileSystemChanged(path);
                }
                catch (Exception ex)
                {
                    this.logger?.Error("StrmFileWatcher 通知 Emby 入库扫描失败");
                    this.logger?.Error(ex.Message);
                }
            }
        }

        /// <summary>
        /// 收集 Changed 事件，延迟后执行 restore。
        /// </summary>
        private void QueueRestorePath(string path)
        {
            if (!this.enabled || this.disposed || string.IsNullOrWhiteSpace(path))
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

            if (!LibraryService.IsFileShortcut(path))
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.pendingPaths[path] = true;

                var delay = TimeSpan.FromSeconds(this.refreshDelaySeconds);
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

        private async Task FlushPendingPathsAsync()
        {
            string[] pendingEntries;
            lock (this.syncRoot)
            {
                if (this.pendingPaths.Count == 0 || !this.enabled || this.disposed)
                {
                    return;
                }

                pendingEntries = this.pendingPaths.Keys.ToArray();
                this.pendingPaths.Clear();
            }

            foreach (var path in pendingEntries)
            {
                QueueRestore(path);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private void QueueRestore(string strmPath)
        {
            try
            {
                if (!this.enabled || this.disposed)
                {
                    return;
                }

                var item = this.libraryManager.FindByPath(strmPath, false) as BaseItem
                    ?? this.libraryManager.FindByPath(strmPath, true) as BaseItem;
                if (item == null || item.InternalId == 0)
                {
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
                    }
                }

                this.watchers.Clear();
                this.pendingPaths.Clear();
                DisposeFlushTimer();
            }
        }
    }
}
