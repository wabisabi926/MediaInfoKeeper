using System.Collections.Generic;
using System.Linq;
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
            this.logger = Plugin.SharedLogger;
            this.libraryManager = libraryManager;
            this.mediaSourceManager = mediaSourceManager;
            this.fileSystem = fileSystem;
        }

        /// <summary>判断条目是否已存在可用的 MediaInfo。</summary>
        public bool HasMediaInfo(BaseItem item)
        {
            if (item is not IHasMediaSources)
            {
                return false;
            }

            foreach (var source in GetStaticMediaSources(item, false))
            {
                if (source?.RunTimeTicks.HasValue != true)
                {
                    continue;
                }

                foreach (var stream in source.MediaStreams ?? Enumerable.Empty<MediaStream>())
                {
                    if (stream.Type == MediaStreamType.Audio || stream.Type == MediaStreamType.Video)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>判断条目当前 MediaInfo 中是否存在音频流。</summary>
        public bool HasAudioStream(BaseItem item)
        {
            return HasStreamType(item, MediaStreamType.Audio);
        }

        /// <summary>判断条目当前 MediaInfo 中是否存在视频流。</summary>
        public bool HasVideoStream(BaseItem item)
        {
            return HasStreamType(item, MediaStreamType.Video);
        }

        /// <summary>构建 MediaInfo 提取所需的刷新选项。</summary>
        public MetadataRefreshOptions GetMediaInfoRefreshOptions()
        {
            return new MetadataRefreshOptions(Plugin.DirectoryService)
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

        private bool HasStreamType(BaseItem item, MediaStreamType streamType)
        {
            if (item is not IHasMediaSources)
            {
                return false;
            }

            foreach (var source in GetStaticMediaSources(item, false))
            {
                foreach (var stream in source?.MediaStreams ?? Enumerable.Empty<MediaStream>())
                {
                    if (stream.Type == streamType)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
