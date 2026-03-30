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
        private readonly IFileSystem fileSystem;

        /// <summary>创建 MediaInfo 处理辅助类并注入所需服务。</summary>
        public MediaInfoService(
            ILibraryManager libraryManager,
            IFileSystem fileSystem)
        {
            this.logger = Plugin.Instance.Logger;
            this.libraryManager = libraryManager;
            this.fileSystem = fileSystem;
        }

        /// <summary>判断条目是否已存在 MediaInfo。</summary>
        public bool HasMediaInfo(BaseItem item)
        {
            if (!item.RunTimeTicks.HasValue)
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
