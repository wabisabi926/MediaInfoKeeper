using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace MediaInfoKeeper.Patch
{
    public class ExternalSubtitle
    {
        private static readonly HashSet<string> ProbeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sub",
            ".smi",
            ".sami",
            ".mpl"
        };

        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IFileSystem fileSystem;
        private readonly IItemRepository itemRepository;
        private readonly object subtitleResolver;
        private readonly MethodInfo getExternalTracksMethod;
        private readonly object ffProbeSubtitleInfo;
        private readonly MethodInfo updateExternalSubtitleStreamMethod;

        public ExternalSubtitle(
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager,
            IItemRepository itemRepository)
        {
            this.logger = Plugin.Instance.Logger;
            this.libraryManager = libraryManager;
            this.fileSystem = fileSystem;
            this.itemRepository = itemRepository;

            try
            {
                var embyProvidersAssembly = Assembly.Load("Emby.Providers");
                var embyProvidersVersion = embyProvidersAssembly.GetName().Version;
                var subtitleResolverType = embyProvidersAssembly.GetType("Emby.Providers.MediaInfo.SubtitleResolver");
                var baseTrackResolverType = embyProvidersAssembly.GetType("Emby.Providers.MediaInfo.BaseTrackResolver");
                var ffProbeSubtitleInfoType = embyProvidersAssembly.GetType("Emby.Providers.MediaInfo.FFProbeSubtitleInfo");
                var localizationManagerType = Assembly.Load("MediaBrowser.Model")
                    .GetType("MediaBrowser.Model.Globalization.ILocalizationManager");
                var fileSystemType = Assembly.Load("MediaBrowser.Model")
                    .GetType("MediaBrowser.Model.IO.IFileSystem");
                var libraryManagerType = Assembly.Load("MediaBrowser.Controller")
                    .GetType("MediaBrowser.Controller.Library.ILibraryManager");
                var libraryOptionsType = Assembly.Load("MediaBrowser.Model")
                    .GetType("MediaBrowser.Model.Configuration.LibraryOptions");
                var baseItemType = Assembly.Load("MediaBrowser.Controller")
                    .GetType("MediaBrowser.Controller.Entities.BaseItem");
                var mediaStreamType = Assembly.Load("MediaBrowser.Model")
                    .GetType("MediaBrowser.Model.Entities.MediaStream");
                var metadataRefreshOptionsType = Assembly.Load("MediaBrowser.Controller")
                    .GetType("MediaBrowser.Controller.Providers.MetadataRefreshOptions");
                var directoryServiceType = Assembly.Load("MediaBrowser.Controller")
                    .GetType("MediaBrowser.Controller.Providers.IDirectoryService");
                var namingOptionsType = libraryManager.GetNamingOptions()?.GetType();

                if (subtitleResolverType == null ||
                    baseTrackResolverType == null ||
                    ffProbeSubtitleInfoType == null ||
                    localizationManagerType == null ||
                    fileSystemType == null ||
                    libraryManagerType == null ||
                    libraryOptionsType == null ||
                    baseItemType == null ||
                    mediaStreamType == null ||
                    metadataRefreshOptionsType == null ||
                    directoryServiceType == null ||
                    namingOptionsType == null)
                {
                    PatchLog.InitFailed(this.logger, nameof(ExternalSubtitle), "关键运行时类型缺失");
                    return;
                }

                this.subtitleResolver = Activator.CreateInstance(
                    subtitleResolverType,
                    localizationManager,
                    fileSystem,
                    libraryManager);
                if (this.subtitleResolver == null)
                {
                    PatchLog.InitFailed(this.logger, nameof(ExternalSubtitle), "SubtitleResolver 初始化失败");
                    return;
                }

                this.getExternalTracksMethod = PatchMethodResolver.Resolve(
                    baseTrackResolverType,
                    embyProvidersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "BaseTrackResolver.GetExternalTracks",
                        MethodName = "GetExternalTracks",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[]
                        {
                            baseItemType,
                            typeof(int),
                            directoryServiceType,
                            libraryOptionsType,
                            namingOptionsType,
                            typeof(bool)
                        }
                    },
                    this.logger,
                    nameof(ExternalSubtitle));

                this.ffProbeSubtitleInfo = Activator.CreateInstance(ffProbeSubtitleInfoType, mediaProbeManager);
                if (this.ffProbeSubtitleInfo == null)
                {
                    PatchLog.InitFailed(this.logger, nameof(ExternalSubtitle), "FFProbeSubtitleInfo 初始化失败");
                    return;
                }

                this.updateExternalSubtitleStreamMethod = PatchMethodResolver.Resolve(
                    ffProbeSubtitleInfoType,
                    embyProvidersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "FFProbeSubtitleInfo.UpdateExternalSubtitleStream",
                        MethodName = "UpdateExternalSubtitleStream",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[]
                        {
                            baseItemType,
                            mediaStreamType,
                            metadataRefreshOptionsType,
                            libraryOptionsType,
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<bool>)
                    },
                    this.logger,
                    nameof(ExternalSubtitle));
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(this.logger, nameof(ExternalSubtitle), ex.Message);
                this.logger.Debug(ex.StackTrace);
            }
        }

        public bool IsAvailable =>
            this.subtitleResolver != null &&
            this.getExternalTracksMethod != null &&
            this.ffProbeSubtitleInfo != null &&
            this.updateExternalSubtitleStreamMethod != null;

        public MetadataRefreshOptions GetRefreshOptions()
        {
            return new MetadataRefreshOptions(Plugin.DirectoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        public bool HasExternalSubtitleChanged(BaseItem item, IDirectoryService directoryService, bool clearCache)
        {
            if (item == null || !IsAvailable)
            {
                return false;
            }

            try
            {
                var currentSet = new HashSet<string>(
                    item.GetMediaStreams()
                        .Where(stream =>
                            stream.IsExternal &&
                            stream.Type == MediaStreamType.Subtitle &&
                            !string.IsNullOrWhiteSpace(stream.Path))
                        .Select(stream => NormalizePath(stream.Path)),
                    StringComparer.OrdinalIgnoreCase);

                var newSet = new HashSet<string>(
                    GetExternalSubtitleStreams(item, 0, directoryService, clearCache)
                        .Where(stream => !string.IsNullOrWhiteSpace(stream.Path))
                        .Select(stream => NormalizePath(stream.Path)),
                    StringComparer.OrdinalIgnoreCase);

                return !currentSet.SetEquals(newSet);
            }
            catch (Exception ex)
            {
                this.logger.Warn($"外挂字幕变更检测失败: {item.Path ?? item.Name}");
                this.logger.Warn(ex.Message);
                this.logger.Debug(ex.StackTrace);
                return false;
            }
        }

        public async Task UpdateExternalSubtitles(
            BaseItem item,
            MetadataRefreshOptions refreshOptions,
            bool clearCache,
            CancellationToken cancellationToken)
        {
            if (item == null || !IsAvailable)
            {
                return;
            }

            var directoryService = refreshOptions.DirectoryService;
            var currentStreams = item.GetMediaStreams()
                .FindAll(stream =>
                    !(stream.IsExternal && stream.Type == MediaStreamType.Subtitle && stream.Protocol == MediaProtocol.File));
            var nextIndex = currentStreams.Count == 0 ? 0 : currentStreams.Max(stream => stream.Index) + 1;
            var externalSubtitleStreams = GetExternalSubtitleStreams(item, nextIndex, directoryService, clearCache);

            foreach (var subtitleStream in externalSubtitleStreams)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var extension = Path.GetExtension(subtitleStream.Path);
                if (!string.IsNullOrWhiteSpace(extension) && ProbeExtensions.Contains(extension))
                {
                    bool updated;
                    using (FfProcessGuard.Allow())
                    {
                        updated = await UpdateExternalSubtitleStream(item, subtitleStream, refreshOptions, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (!updated)
                    {
                        this.logger.Warn($"外挂字幕探测未返回结果: {subtitleStream.Path}");
                    }
                }

                this.logger.Info($"外挂字幕已处理: {subtitleStream.Path}");
            }

            currentStreams.AddRange(externalSubtitleStreams);
            this.itemRepository.SaveMediaStreams(item.InternalId, currentStreams, cancellationToken);
            Plugin.MediaSourceInfoStore?.OverWriteToFile(item);
        }

        private List<MediaStream> GetExternalSubtitleStreams(
            BaseItem item,
            int startIndex,
            IDirectoryService directoryService,
            bool clearCache)
        {
            if (string.IsNullOrWhiteSpace(item?.Path))
            {
                return new List<MediaStream>();
            }

            if (string.IsNullOrWhiteSpace(item.ContainingFolderPath) || !Directory.Exists(item.ContainingFolderPath))
            {
                return new List<MediaStream>();
            }

            var libraryOptions = this.libraryManager.GetLibraryOptions(item);
            var namingOptions = this.libraryManager.GetNamingOptions();
            var externalSubtitleStreams = this.getExternalTracksMethod.Invoke(
                this.subtitleResolver,
                new object[]
                {
                    item,
                    startIndex,
                    directoryService,
                    libraryOptions,
                    namingOptions,
                    clearCache
                }) as List<MediaStream>;

            if (externalSubtitleStreams == null)
            {
                return new List<MediaStream>();
            }

            return externalSubtitleStreams
                .Where(stream =>
                    stream != null &&
                    stream.Type == MediaStreamType.Subtitle &&
                    !string.IsNullOrWhiteSpace(stream.Path))
                .Select(stream =>
                {
                    stream.IsExternal = true;
                    stream.Protocol = MediaProtocol.File;
                    return stream;
                })
                .ToList();
        }

        private Task<bool> UpdateExternalSubtitleStream(
            BaseItem item,
            MediaStream subtitleStream,
            MetadataRefreshOptions refreshOptions,
            CancellationToken cancellationToken)
        {
            var libraryOptions = this.libraryManager.GetLibraryOptions(item);
            return (Task<bool>)this.updateExternalSubtitleStreamMethod.Invoke(
                this.ffProbeSubtitleInfo,
                new object[]
                {
                    item,
                    subtitleStream,
                    refreshOptions,
                    libraryOptions,
                    cancellationToken
                });
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return path.Trim();
        }
    }
}
