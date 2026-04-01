using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using MediaInfoKeeper.Services;
using MediaInfoKeeper.Web.Handler;

namespace MediaInfoKeeper.Web
{
    [Unauthenticated]
    public class PluginWebResourceService : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory _resultFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ExtractMediaInfoRouteHandler _extractHandler;
        private readonly DeleteMediaInfoPersistRouteHandler _deletePersistHandler;
        private readonly ScanIntroRouteHandler _scanIntroHandler;
        private readonly SetIntroRouteHandler _setIntroHandler;
        private readonly ClearIntroRouteHandler _clearIntroHandler;

        public PluginWebResourceService(
            IHttpResultFactory resultFactory,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer)
        {
            _resultFactory = resultFactory;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;
            _extractHandler = new ExtractMediaInfoRouteHandler(ExpandToTargetItems);
            _deletePersistHandler = new DeleteMediaInfoPersistRouteHandler(ExpandToTargetItems, libraryManager, itemRepository);
            _scanIntroHandler = new ScanIntroRouteHandler(ExpandToTargetItems);
            _setIntroHandler = new SetIntroRouteHandler(ExpandToTargetItems, libraryManager, itemRepository);
            _clearIntroHandler = new ClearIntroRouteHandler(ExpandToTargetItems, libraryManager, itemRepository);
        }

        public IRequest Request { get; set; }

        public object Get(MediaInfoKeeperJsRequest request)
        {
            return _resultFactory.GetResult(Request,
                (ReadOnlyMemory<byte>)PluginWebResourceLoader.MediaInfoKeeperJs.GetBuffer(), "application/x-javascript");
        }

        public object Get(EdeJsRequest request)
        {
            return _resultFactory.GetResult(Request,
                (ReadOnlyMemory<byte>)PluginWebResourceLoader.EdeJs.GetBuffer(), "application/x-javascript");
        }

        public object Get(ShortcutMenuRequest request)
        {
            return _resultFactory.GetResult(PluginWebResourceLoader.ModifiedShortcutsString.AsSpan(),
                "application/x-javascript");
        }

        public MediaInfoMenuResponse Post(ExtractMediaInfoRequest request)
        {
            return _extractHandler.HandleAsync(request).GetAwaiter().GetResult();
        }

        public MediaInfoMenuResponse Post(DeleteMediaInfoPersistRequest request)
        {
            return _deletePersistHandler.Handle(request);
        }

        public MediaInfoMenuResponse Post(ScanIntroRequest request)
        {
            return _scanIntroHandler.Handle(request);
        }

        public MediaInfoMenuResponse Post(SetIntroRequest request)
        {
            return _setIntroHandler.Handle(request);
        }

        public MediaInfoMenuResponse Post(ClearIntroRequest request)
        {
            return _clearIntroHandler.Handle(request);
        }

        public DebugMediaInfoResponse Get(DebugMediaInfoRequest request)
        {
            if (request == null || request.InternalId <= 0)
            {
                return new DebugMediaInfoResponse
                {
                    Found = false,
                    Message = "invalid internalId"
                };
            }

            var item = _libraryManager.GetItemById(request.InternalId);
            if (item == null)
            {
                return new DebugMediaInfoResponse
                {
                    Found = false,
                    Message = "item not found"
                };
            }

            var mediaInfoPath = MediaInfoDocument.GetMediaInfoJsonPath(item);
            var coverPath = MediaInfoDocument.GetCoverPath(item);
            var streams = item.GetMediaStreams().ToList();
            var directoryService = new DirectoryService(Plugin.Instance.Logger, Plugin.FileSystem);
            var primaryImage = BuildPrimaryImageInfo(item);
            var chapterImages = BuildChapterImagesInfo(item);
            var thumbnailSets = BuildThumbnailSetsInfo(item, directoryService);

            return new DebugMediaInfoResponse
            {
                Found = true,
                Message = "ok",
                Item = new DebugItemInfo
                {
                    InternalId = item.InternalId,
                    Type = item.GetType().Name,
                    Name = item.Name,
                    Path = item.Path,
                    FileName = item.FileName,
                    ContainingFolderPath = item.ContainingFolderPath,
                    ItemId = item.Id.ToString(),
                    ParentId = item.ParentId,
                    ImageDisplayParentId = item.ImageDisplayParentId,
                    IsShortcut = item.IsShortcut,
                    ExtraType = item.ExtraType?.ToString(),
                    HasMediaInfo = Plugin.MediaInfoService.HasMediaInfo(item),
                    HasCover = Plugin.LibraryService?.HasCover(item) == true,
                    HasPrimaryImage = item.HasImage(ImageType.Primary),
                    IsInScope = Plugin.LibraryService?.IsItemInScope(item) == true,
                    IsRefreshedRecently = Plugin.LibraryService?.IsItemRefreshedRecently(item) == true,
                    MediaStreamCount = streams.Count,
                    AudioStreamCount = streams.Count(i => i.Type == MediaStreamType.Audio),
                    VideoStreamCount = streams.Count(i => i.Type == MediaStreamType.Video),
                    SubtitleStreamCount = streams.Count(i => i.Type == MediaStreamType.Subtitle),
                    RunTimeTicks = item.RunTimeTicks,
                    Size = item.Size,
                    Container = item.Container,
                    Width = item.Width,
                    Height = item.Height,
                    DateCreated = item.DateCreated == default ? null : item.DateCreated.ToString("O"),
                    DateModified = item.DateModified == default ? null : item.DateModified.ToString("O"),
                    DateLastRefreshed = item.DateLastRefreshed == default ? null : item.DateLastRefreshed.ToString("O"),
                    PremiereDate = item.PremiereDate?.ToString("O"),
                    ProductionYear = item.ProductionYear,
                    OfficialRating = item.OfficialRating,
                    SupportsThumbnails = item is Video itemVideo ? itemVideo.SupportsThumbnails : (bool?)null
                },
                MediaInfoJson = new DebugFileInfo
                {
                    Path = mediaInfoPath,
                    Exists = File.Exists(mediaInfoPath),
                    Content = ReadJsonFile<List<MediaInfoDocument>>(mediaInfoPath)
                },
                Cover = new DebugBinaryFileInfo
                {
                    Path = coverPath,
                    Exists = File.Exists(coverPath),
                    Length = File.Exists(coverPath) ? new FileInfo(coverPath).Length : 0
                },
                PrimaryImage = primaryImage,
                ChapterImages = chapterImages,
                ThumbnailSets = thumbnailSets
            };
        }

        private DebugPrimaryImageInfo BuildPrimaryImageInfo(BaseItem item)
        {
            var primaryImage = item.GetImageInfo(ImageType.Primary, 0);
            var displayParentId = item.ImageDisplayParentId;
            var displayParent = displayParentId == 0 || displayParentId == item.InternalId
                ? null
                : _libraryManager.GetItemById(displayParentId);
            var displayParentPrimaryImage = displayParent?.GetImageInfo(ImageType.Primary, 0);

            return new DebugPrimaryImageInfo
            {
                HasPrimaryImage = item.HasImage(ImageType.Primary),
                PrimaryImagePath = primaryImage?.Path,
                PrimaryImagePathExists = FileExists(primaryImage?.Path),
                ImageDisplayParentId = displayParentId,
                HasDisplayParentPrimaryImage = displayParent?.HasImage(ImageType.Primary) == true,
                DisplayParentPrimaryImagePath = displayParentPrimaryImage?.Path,
                DisplayParentPrimaryImagePathExists = FileExists(displayParentPrimaryImage?.Path)
            };
        }

        private DebugChapterImagesInfo BuildChapterImagesInfo(BaseItem item)
        {
            var chapters = _itemRepository.GetChapters(item) ?? new List<ChapterInfo>();
            var entries = chapters
                .Select(chapter => new DebugChapterImageEntry
                {
                    Name = chapter.Name,
                    MarkerType = chapter.MarkerType.ToString(),
                    StartPositionTicks = chapter.StartPositionTicks,
                    ImagePath = chapter.ImagePath,
                    ImagePathExists = FileExists(chapter.ImagePath),
                    ImageTag = chapter.ImageTag,
                    ImageDateModified = chapter.ImageDateModified == default
                        ? null
                        : chapter.ImageDateModified.ToString("O")
                })
                .ToArray();

            return new DebugChapterImagesInfo
            {
                ChapterCount = chapters.Count,
                ChaptersWithImagePath = entries.Count(i => !string.IsNullOrWhiteSpace(i.ImagePath)),
                ExistingImageFiles = entries.Count(i => i.ImagePathExists),
                Entries = entries
            };
        }

        private DebugThumbnailSetsInfo BuildThumbnailSetsInfo(BaseItem item, IDirectoryService directoryService)
        {
            if (item is not Video video)
            {
                return new DebugThumbnailSetsInfo
                {
                    SupportsThumbnails = false,
                    Count = 0,
                    Entries = Array.Empty<DebugThumbnailSetEntry>()
                };
            }

            var thumbnailSets = Video.GetThumbnailSetInfos(
                    video.Path,
                    video.Id,
                    directoryService,
                    0,
                    false)
                ?? Array.Empty<ThumbnailSetInfo>();

            return new DebugThumbnailSetsInfo
            {
                SupportsThumbnails = video.SupportsThumbnails,
                Count = thumbnailSets.Length,
                Entries = thumbnailSets
                    .Select(set => new DebugThumbnailSetEntry
                    {
                        Path = set.Path,
                        Exists = DirectoryExists(set.Path) || FileExists(set.Path),
                        IsDirectory = DirectoryExists(set.Path),
                        Width = set.Width,
                        IntervalSeconds = set.IntervalSeconds
                    })
                    .ToArray()
            };
        }

        private static bool FileExists(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }

        private static bool DirectoryExists(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }

        private List<BaseItem> ExpandToTargetItems(IEnumerable<string> ids)
        {
            var targets = new List<BaseItem>();
            var known = new HashSet<long>();

            foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                var item = _libraryManager.GetItemById(id);
                if (item == null)
                {
                    continue;
                }

                if (item is Episode episode)
                {
                    if (episode.ExtraType == null && known.Add(episode.InternalId))
                    {
                        targets.Add(episode);
                    }

                    continue;
                }

                if (item is Video video)
                {
                    if (video.ExtraType == null && known.Add(video.InternalId))
                    {
                        targets.Add(video);
                    }

                    continue;
                }

                if (item is Audio audio)
                {
                    if (audio.ExtraType == null && known.Add(audio.InternalId))
                    {
                        targets.Add(audio);
                    }

                    continue;
                }

                if (item is MusicAlbum || item is MusicArtist || item is MusicGenre)
                {
                    foreach (var audioItem in ExpandToAudioItems(item))
                    {
                        if (audioItem.ExtraType == null && known.Add(audioItem.InternalId))
                        {
                            targets.Add(audioItem);
                        }
                    }

                    continue;
                }

                if (!(item is Series || item is Season))
                {
                    continue;
                }

                var episodes = Plugin.LibraryService.GetSeriesEpisodesFromItem(item);
                foreach (var episodeItem in episodes)
                {
                    if (episodeItem?.ExtraType == null && known.Add(episodeItem.InternalId))
                    {
                        targets.Add(episodeItem);
                    }
                }
            }

            return targets;
        }

        private IEnumerable<Audio> ExpandToAudioItems(BaseItem item)
        {
            if (item == null)
            {
                return Array.Empty<Audio>();
            }

            return _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    HasPath = true,
                    MediaTypes = new[] { MediaBrowser.Model.Entities.MediaType.Audio },
                    IncludeItemTypes = new[] { nameof(Audio) },
                    ParentIds = new[] { item.InternalId }
                })
                .OfType<Audio>();
        }

        private T ReadJsonFile<T>(string path) where T : class
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return _jsonSerializer.DeserializeFromFile<T>(path);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
