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
            var lyricsPath = MediaInfoDocument.GetLyricsJsonPath(item);
            var coverPath = MediaInfoDocument.GetCoverPath(item);
            var streams = item.GetMediaStreams().ToList();

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
                    HasMediaInfo = Plugin.MediaInfoService.HasMediaInfo(item),
                    HasCover = Plugin.LibraryService?.HasCover(item) == true,
                    MediaStreamCount = streams.Count,
                    AudioStreamCount = streams.Count(i => i.Type == MediaStreamType.Audio),
                    VideoStreamCount = streams.Count(i => i.Type == MediaStreamType.Video),
                    SubtitleStreamCount = streams.Count(i => i.Type == MediaStreamType.Subtitle),
                    RunTimeTicks = item.RunTimeTicks
                },
                MediaInfoJson = new DebugFileInfo
                {
                    Path = mediaInfoPath,
                    Exists = File.Exists(mediaInfoPath),
                    Content = ReadJsonFile<List<MediaInfoDocument>>(mediaInfoPath)
                },
                LyricsJson = new DebugFileInfo
                {
                    Path = lyricsPath,
                    Exists = File.Exists(lyricsPath),
                    Content = ReadJsonFile<List<EmbeddedLyricsSnapshot>>(lyricsPath)
                },
                Cover = new DebugBinaryFileInfo
                {
                    Path = coverPath,
                    Exists = File.Exists(coverPath),
                    Length = File.Exists(coverPath) ? new FileInfo(coverPath).Length : 0
                }
            };
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
