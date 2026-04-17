using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private volatile bool enabled;
        private volatile bool disposed;

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
                if (!this.libraryManager.IsVideoFile(path.AsSpan()) || !this.libraryManager.IsAudioFile(path.AsSpan()))
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
                this.logger?.Info($"新增媒体文件，{Path.GetFileName(path) ?? path}");
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
            }
        }
    }
}
