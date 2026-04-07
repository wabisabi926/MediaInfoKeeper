using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
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
        private readonly IMediaSourceManager mediaSourceManager;
        private readonly IFileSystem fileSystem;

        /// <summary>创建 MediaInfo 处理辅助类并注入所需服务。</summary>
        public MediaInfoService(
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            IFileSystem fileSystem)
        {
            this.logger = Plugin.Instance.Logger;
            this.libraryManager = libraryManager;
            this.mediaSourceManager = mediaSourceManager;
            this.fileSystem = fileSystem;
        }

        /// <summary>判断条目是否已存在 MediaInfo。</summary>
        public bool HasMediaInfo(BaseItem item)
        {
            if (item is not IHasMediaSources)
            {
                return false;
            }

            var mediaSources = GetStaticMediaSources(item, false);

            return mediaSources.Any(source =>
                source?.RunTimeTicks.HasValue == true &&
                (source.MediaStreams ?? new List<MediaStream>()).Any(stream =>
                    stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio));
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

        /// <summary>获取指定条目的静态媒体源。</summary>
        public List<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enableAlternateMediaSources)
        {
            if (item is not IHasMediaSources)
            {
                return new List<MediaSourceInfo>();
            }

            var collectionFolders = this.libraryManager.GetCollectionFolders(item).Cast<BaseItem>().ToArray();
            var libraryOptions = this.libraryManager.GetLibraryOptions(item);

            return this.mediaSourceManager.GetStaticMediaSources(
                item,
                enableAlternateMediaSources,
                enablePathSubstitution: false,
                fillChapters: false,
                collectionFolders: collectionFolders,
                libraryOptions: libraryOptions,
                deviceProfile: null,
                user: null);
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
