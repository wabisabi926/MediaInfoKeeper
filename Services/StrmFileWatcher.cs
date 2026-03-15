using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    /// <summary>
    /// 监听媒体库路径下的 strm 文件变更，独立于元数据刷新链路进行媒体信息恢复校验。
    /// </summary>
    public sealed class StrmFileWatcher : IDisposable
    {
        private readonly ILibraryManager libraryManager;
        private readonly LibraryService libraryService;
        private readonly ILogger logger;
        private readonly object syncRoot = new object();
        private readonly Dictionary<string, FileSystemWatcher> watchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> pathVersionMap =
            new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        private volatile bool enabled;
        private volatile bool disposed;

        private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);

        public StrmFileWatcher(ILibraryManager libraryManager, LibraryService libraryService, ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.libraryService = libraryService;
            this.logger = logger;
        }

        public void Configure(bool isEnabled)
        {
            if (this.disposed)
            {
                return;
            }

            this.enabled = isEnabled;
            RebuildWatchers(isEnabled);
        }

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
                        var watcher = new FileSystemWatcher(root, "*.strm")
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                            InternalBufferSize = 64 * 1024,
                            EnableRaisingEvents = true
                        };

                        watcher.Changed += OnChanged;

                        this.watchers[root] = watcher;
                    }
                    catch (Exception ex)
                    {
                        this.logger?.Warn($"StrmFileWatcher 监听路径失败: {root}");
                        this.logger?.Warn(ex.Message);
                    }
                }

                this.logger?.Info(
                    $"StrmFileWatcher 已启动，监听路径: {string.Join(", ", this.watchers.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))}");
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            QueuePathForVerification(e?.FullPath);
        }

        private void QueuePathForVerification(string path)
        {
            if (!this.enabled || this.disposed || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            this.pathVersionMap[path] = version;

            _ = Task.Run(async () =>
            {
                await Task.Delay(DebounceDelay).ConfigureAwait(false);

                if (!this.pathVersionMap.TryGetValue(path, out var latest) || latest != version)
                {
                    return;
                }

                this.pathVersionMap.TryRemove(path, out _);
                this.logger?.Info($"StrmFileWatcher 检测到 strm 内容变更: {path}");
                QueueRestore(path);
            });
        }

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

                if (Plugin.LibraryService != null && !Plugin.LibraryService.IsItemInScope(item))
                {
                    return;
                }

                MediaInfoRestoreService.QueueRestore("StrmFileWatcher", item, 10);
            }
            catch (Exception ex)
            {
                this.logger?.Error("StrmFileWatcher 排队恢复失败");
                this.logger?.Error(ex.Message);
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
                        // ignore
                    }
                }

                this.watchers.Clear();
            }

            this.pathVersionMap.Clear();
        }

        private string TryReadItemPathContent(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "<empty-path>";
            }

            try
            {
                if (!File.Exists(path))
                {
                    return "<file-not-found>";
                }

                var content = File.ReadAllText(path);
                return string.IsNullOrWhiteSpace(content) ? "<empty-content>" : content.Trim();
            }
            catch (Exception ex)
            {
                this.logger?.Warn($"StrmFileWatcher 读取文件内容失败: {path}");
                this.logger?.Warn(ex.Message);
                return "<read-failed>";
            }
        }
    }
}
