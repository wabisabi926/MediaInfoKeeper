using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Patch;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public class MediaInfoService
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IFileSystem fileSystem;
        private readonly IMediaMountManager mediaMountManager;

        /// <summary>创建 MediaInfo 处理辅助类并注入所需服务。</summary>
        public MediaInfoService(
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            IMediaMountManager mediaMountManager)
        {
            this.logger = Plugin.Instance.Logger;
            this.libraryManager = libraryManager;
            this.fileSystem = fileSystem;
            this.mediaMountManager = mediaMountManager;
        }

        /// <summary>判断条目是否已存在 MediaInfo。</summary>
        public bool HasMediaInfo(BaseItem item)
        {
            if (!item.RunTimeTicks.HasValue)
            {
                return false;
            }

            if (item.Size == 0)
            {
                return false;
            }

            return item.GetMediaStreams().Any(i => i.Type == MediaStreamType.Video || i.Type == MediaStreamType.Audio);
        }

        /// <summary>构建 MediaInfo 提取所需的刷新选项。</summary>
        public MetadataRefreshOptions GetMediaInfoRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = Plugin.Instance.Options.MetaData.EnableImageCapture,
                EnableSubtitleDownloading = false
            };
        }

        /// <summary>为片头扫描执行最小刷新，确保条目进入可扫描状态。</summary>
        public async Task<Episode> TryRefreshEpisodeForIntroScanAsync(Episode episode, string source)
        {
            if (episode == null)
            {
                return null;
            }

            try
            {
                var metadataRefreshOptions = new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllMetadata = false,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllImages = false,
                    EnableThumbnailImageExtraction = false,
                    EnableSubtitleDownloading = false
                };
                var collectionFolders = this.libraryManager.GetCollectionFolders(episode).Cast<BaseItem>().ToArray();
                var libraryOptions = this.libraryManager.GetLibraryOptions(episode);
                using (FfprobeGuard.Allow())
                {
                    episode.DateLastRefreshed = new DateTimeOffset();
                    await Plugin.ProviderManager
                        .RefreshSingleItem(episode, metadataRefreshOptions, collectionFolders, libraryOptions, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                return episode;
            }
            catch (Exception ex)
            {
                this.logger.Error($"{source} 片头扫描: 未刷新条目触发刷新失败 {episode.Path} InternalId: {episode.InternalId}");
                this.logger.Error(ex.Message);
                this.logger.Debug(ex.StackTrace);
                return null;
            }
        }

        /// <summary>片头扫描前预提取：执行挂载解析、恢复与探测。</summary>
        public async Task<Episode> PrepareEpisodeForIntroScanAsync(Episode episode, IDirectoryService directoryService, string source)
        {
            if (episode == null)
            {
                return null;
            }

            var workEpisode = this.libraryManager.GetItemById(episode.InternalId) as Episode ?? episode;
            var ds = directoryService ?? new DirectoryService(this.logger, this.fileSystem);

            if (workEpisode.IsShortcut)
            {
                var mountedPath = await GetStrmMountPathAsync(workEpisode.Path).ConfigureAwait(false);
                if (string.IsNullOrEmpty(mountedPath))
                {
                    this.logger.Warn($"{source} 片头扫描预提取: {workEpisode.FileName} InternalId: {workEpisode.InternalId} 挂载路径解析失败，跳过扫描");
                    return null;
                }
            }

            var restoreSucceeded = false;
            if (!Plugin.MediaInfoService.HasMediaInfo(workEpisode))
            {
                logger.Info($"{source} 片头扫描预提取: {workEpisode.FileName} 无 MediaInfo，尝试从 JSON 恢复");
                var restoreResult = Plugin.MediaSourceInfoStore.ApplyToItem(workEpisode);
                Plugin.ChaptersStore.ApplyToItem(workEpisode);
                restoreSucceeded = restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored ||
                                  restoreResult == MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists;

                if (!restoreSucceeded)
                {
                    this.logger.Info($"{source} 片头扫描预提取: {workEpisode.FileName} 开始提取媒体信息");
                    workEpisode = await TryRefreshEpisodeForIntroScanAsync(workEpisode, source + " Extract")
                        .ConfigureAwait(false);
                    if (workEpisode == null)
                    {
                        return null;
                    }
                    // 将扫描后的结果持久化
                    if (Plugin.MediaInfoService.HasMediaInfo(workEpisode))
                    {
                        Plugin.MediaSourceInfoStore.OverWriteToFile(workEpisode);
                    }
                }
            }

            workEpisode = this.libraryManager.GetItemById(workEpisode.InternalId) as Episode ?? workEpisode;
            if (!Plugin.MediaInfoService.HasMediaInfo(workEpisode))
            {
                this.logger.Warn($"{source} 片头扫描预提取: {workEpisode.FileName} 提取后仍无 MediaInfo，跳过扫描");
                return null;
            }

            var hasAudioStream = workEpisode.GetMediaStreams().Any(s => s.Type == MediaStreamType.Audio);
            if (!hasAudioStream)
            {
                this.logger.Warn($"{source} 片头扫描预提取: {workEpisode.FileName} MediaInfo 存在但无音频流，跳过扫描");
                return null;
            }

            return workEpisode;
        }

        private async Task<string> GetStrmMountPathAsync(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                return null;
            }

            if (this.mediaMountManager == null)
            {
                this.logger.Warn("片头扫描预提取: IMediaMountManager 为空，无法解析 strm 挂载路径");
                return null;
            }

            try
            {
                using var mediaMount = await this.mediaMountManager.Mount(strmPath, null, CancellationToken.None)
                    .ConfigureAwait(false);
                return mediaMount?.MountedPathInfo?.FullName;
            }
            catch (Exception ex)
            {
                this.logger.Warn($"片头扫描预提取: strm 挂载路径解析异常 {strmPath}");
                this.logger.Warn(ex.Message);
                return null;
            }
        }
        /// <summary>获取指定条目的静态媒体源。</summary>
        public List<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enableAlternateMediaSources)
        {
            var options = this.libraryManager.GetLibraryOptions(item);
            return item.GetMediaSources(enableAlternateMediaSources, false, options);
        }

        /// <summary>生成相对路径并处理基础兼容逻辑。</summary>
        private static string GetRelativePathCompat(string rootPath, string fullPath)
        {
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(fullPath))
            {
                return fullPath;
            }

            var root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;

            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(root.Length)
                : fullPath;
        }
    }
}
