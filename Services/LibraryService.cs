using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace MediaInfoKeeper.Services
{
    public class LibraryService
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly IUserDataManager userDataManager;

        /// <summary>创建库处理辅助类并注入所需服务。</summary>
        public LibraryService(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, IUserDataManager userDataManager)
        {
            this.logger = Plugin.Instance.Logger;
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.userDataManager = userDataManager;
        }

        /// <summary>构建媒体库选择列表，用于配置 UI 复用。</summary>
        public List<EditorSelectOption> BuildLibrarySelectOptions()
        {
            var list = new List<EditorSelectOption>();
            foreach (var folder in this.libraryManager.GetVirtualFolders())
            {
                if (folder == null)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(folder.Name) ? folder.ItemId : folder.Name;
                list.Add(new EditorSelectOption
                {
                    Value = folder.ItemId,
                    Name = name,
                    IsEnabled = true
                });
            }

            return list;
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

        /// <summary>根据配置判断条目是否属于选定媒体库。</summary>
        public bool IsItemInScope(BaseItem item)
        {
            var scopedLibraries = GetScopedLibraryKeys();
            if (scopedLibraries.Count == 0)
            {
                return true;
            }

            foreach (var collectionFolder in this.libraryManager.GetCollectionFolders(item))
            {
                if (collectionFolder == null)
                {
                    continue;
                }

                var name = collectionFolder.Name?.Trim();
                if (!string.IsNullOrEmpty(name) &&
                    scopedLibraries.Contains(name))
                {
                    return true;
                }

                if (scopedLibraries.Contains(collectionFolder.InternalId.ToString()))
                {
                    return true;
                }

                var id = collectionFolder.Id.ToString();
                if (scopedLibraries.Contains(id))
                {
                    return true;
                }
            }

            return false;
        }

        private HashSet<string> GetScopedLibraryKeys()
        {
            var raw = Plugin.Instance.Options.MainPage.CatchupLibraries;
            return ParseScopedLibraryTokens(raw);
        }

        /// <summary>根据配置生成媒体库路径列表。</summary>
        public List<string> GetScopedLibraryPaths(string scopedLibraries, out bool hasScope)
        {
            var tokens = ParseScopedLibraryTokens(scopedLibraries);
            hasScope = tokens.Count > 0;

            var libraries = this.libraryManager.GetVirtualFolders();
            if (tokens.Count > 0)
            {
                libraries = libraries
                    .Where(folder =>
                        (!string.IsNullOrWhiteSpace(folder.ItemId) && tokens.Contains(folder.ItemId)) ||
                        (!string.IsNullOrWhiteSpace(folder.Name) && tokens.Contains(folder.Name.Trim())))
                    .ToList();
            }

            return NormalizeLibraryPaths(libraries.SelectMany(folder => folder.Locations ?? Array.Empty<string>()));
        }

        /// <summary>按路径范围获取视频条目。</summary>
        public List<BaseItem> FetchScopedVideoItems(IReadOnlyCollection<string> scopePaths, bool orderByDateCreatedDesc = false, int? take = null)
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            if (scopePaths != null && scopePaths.Count > 0)
            {
                query.PathStartsWithAny = scopePaths.ToArray();
            }

            var items = this.libraryManager.GetItemList(query)
                .Where(i => i.ExtraType is null);

            if (orderByDateCreatedDesc)
            {
                items = items.OrderByDescending(i => i.DateCreated);
            }

            if (take.HasValue)
            {
                items = items.Take(take.Value);
            }

            return items.ToList();
        }

        /// <summary>按时间窗口获取最近条目。</summary>
        public List<BaseItem> FetchRecentItems(DateTime? cutoff, bool orderByDateCreatedDesc = true)
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            var items = this.libraryManager.GetItemList(query)
                .Where(i => i.ExtraType is null)
                .Where(i => cutoff == null || i.DateCreated >= cutoff);

            if (orderByDateCreatedDesc)
            {
                items = items.OrderByDescending(i => i.DateCreated);
            }

            return items.ToList();
        }

        /// <summary>按时间倒序获取最近的剧集条目。</summary>
        public List<Episode> FetchRecentItems(int limit)
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            var items = this.libraryManager.GetItemList(query)
                .OfType<Episode>()
                .Where(i => i.ExtraType is null)
                .OrderByDescending(i => i.DateCreated)
                .Take(Math.Max(1, limit))
                .ToList();

            return items;
        }

        /// <summary>获取全局收藏的视频条目。</summary>
        public List<BaseItem> FetchFavoriteVideoItems()
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            var items = this.libraryManager.GetItemList(query)
                .Where(i => i.ExtraType is null)
                .Where(IsFavoriteByAnyUser)
                .ToList();

            return items;
        }

        public IReadOnlyList<Episode> FetchSeriesEpisodes(Series series)
        {
            if (series == null)
            {
                return Array.Empty<Episode>();
            }

            var episodes = new List<Episode>();
            var known = new HashSet<long>();

            var rootEpisodes = this.libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video },
                    ParentIds = new[] { series.InternalId }
                })
                .OfType<Episode>();

            foreach (var episode in rootEpisodes)
            {
                if (known.Add(episode.InternalId))
                {
                    episodes.Add(episode);
                }
            }

            var seasons = this.libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Season) },
                    ParentIds = new[] { series.InternalId }
                })
                .OfType<Season>();

            foreach (var season in seasons)
            {
                var seasonEpisodes = this.libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { nameof(Episode) },
                        HasPath = true,
                        MediaTypes = new[] { MediaType.Video },
                        ParentIds = new[] { season.InternalId }
                    })
                    .OfType<Episode>();

                foreach (var episode in seasonEpisodes)
                {
                    if (known.Add(episode.InternalId))
                    {
                        episodes.Add(episode);
                    }
                }
            }

            return episodes;
        }

        // 如果是 Series 就返回全剧集；如果是 Episode 或 Season 就只返回该季的所有剧集
        public IReadOnlyList<Episode> GetSeriesEpisodesFromItem(BaseItem item)
        {
            if (item is Series series)
            {
                return FetchSeriesEpisodes(series);
            }

            if (item is Episode episode)
            {
                var seasonFromEpisode = this.libraryManager.GetItemById(episode.ParentId) as Season;
                return seasonFromEpisode != null
                    ? FetchSeasonEpisodes(seasonFromEpisode)
                    : Array.Empty<Episode>();
            }

            if (item is Season season)
            {
                return FetchSeasonEpisodes(season);
            }

            return Array.Empty<Episode>();
        }

        public Series GetSeries(long seriesId)
        {
            return seriesId == 0
                ? null
                : this.libraryManager.GetItemById(seriesId) as Series;
        }

        private IReadOnlyList<Episode> FetchSeasonEpisodes(Season season)
        {
            if (season == null)
            {
                return Array.Empty<Episode>();
            }

            return this.libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video },
                    ParentIds = new[] { season.InternalId }
                })
                .OfType<Episode>()
                .ToList();
        }
        
        private bool IsFavoriteByAnyUser(BaseItem item)
        {
            var userDataList = this.userDataManager.GetAllUserData(item.InternalId);
            if (userDataList == null || userDataList.Count == 0)
            {
                return false;
            }

            return userDataList.Any(data => data?.IsFavorite == true);
        }

        private static HashSet<string> ParseScopedLibraryTokens(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var tokens = raw
                .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrEmpty(value));

            return new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> NormalizeLibraryPaths(IEnumerable<string> paths)
        {
            var separator = Path.DirectorySeparatorChar.ToString();
            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.EndsWith(separator, StringComparison.Ordinal)
                    ? path
                    : path + separator)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>复制库配置，用于元数据刷新流程。</summary>
        public static LibraryOptions CopyLibraryOptions(LibraryOptions sourceOptions)
        {
            var targetOptions = new LibraryOptions
            {
                EnableArchiveMediaFiles = sourceOptions.EnableArchiveMediaFiles,
                EnablePhotos = sourceOptions.EnablePhotos,
                EnableRealtimeMonitor = sourceOptions.EnableRealtimeMonitor,
                EnableMarkerDetection = sourceOptions.EnableMarkerDetection,
                EnableMarkerDetectionDuringLibraryScan = sourceOptions.EnableMarkerDetectionDuringLibraryScan,
                IntroDetectionFingerprintLength = sourceOptions.IntroDetectionFingerprintLength,
                EnableChapterImageExtraction = sourceOptions.EnableChapterImageExtraction,
                ExtractChapterImagesDuringLibraryScan = sourceOptions.ExtractChapterImagesDuringLibraryScan,
                DownloadImagesInAdvance = sourceOptions.DownloadImagesInAdvance,
                CacheImages = sourceOptions.CacheImages,
                PathInfos =
                    sourceOptions.PathInfos?.Select(p => new MediaPathInfo
                    {
                        Path = p.Path,
                        NetworkPath = p.NetworkPath,
                        Username = p.Username,
                        Password = p.Password
                    })
                        .ToArray() ?? Array.Empty<MediaPathInfo>(),
                IgnoreHiddenFiles = sourceOptions.IgnoreHiddenFiles,
                IgnoreFileExtensions =
                    sourceOptions.IgnoreFileExtensions?.Clone() as string[] ?? Array.Empty<string>(),
                SaveLocalMetadata = sourceOptions.SaveLocalMetadata,
                SaveMetadataHidden = sourceOptions.SaveMetadataHidden,
                SaveLocalThumbnailSets = sourceOptions.SaveLocalThumbnailSets,
                ImportPlaylists = sourceOptions.ImportPlaylists,
                EnableAutomaticSeriesGrouping = sourceOptions.EnableAutomaticSeriesGrouping,
                ShareEmbeddedMusicAlbumImages = sourceOptions.ShareEmbeddedMusicAlbumImages,
                EnableEmbeddedTitles = sourceOptions.EnableEmbeddedTitles,
                EnableAudioResume = sourceOptions.EnableAudioResume,
                AutoGenerateChapters = sourceOptions.AutoGenerateChapters,
                AutomaticRefreshIntervalDays = sourceOptions.AutomaticRefreshIntervalDays,
                PlaceholderMetadataRefreshIntervalDays = sourceOptions.PlaceholderMetadataRefreshIntervalDays,
                PreferredMetadataLanguage = sourceOptions.PreferredMetadataLanguage,
                PreferredImageLanguage = sourceOptions.PreferredImageLanguage,
                ContentType = sourceOptions.ContentType,
                MetadataCountryCode = sourceOptions.MetadataCountryCode,
                MetadataSavers = sourceOptions.MetadataSavers?.Clone() as string[] ?? Array.Empty<string>(),
                DisabledLocalMetadataReaders =
                    sourceOptions.DisabledLocalMetadataReaders?.Clone() as string[] ?? Array.Empty<string>(),
                LocalMetadataReaderOrder = sourceOptions.LocalMetadataReaderOrder?.Clone() as string[] ?? null,
                DisabledLyricsFetchers =
                    sourceOptions.DisabledLyricsFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                SaveLyricsWithMedia = sourceOptions.SaveLyricsWithMedia,
                LyricsDownloadMaxAgeDays = sourceOptions.LyricsDownloadMaxAgeDays,
                LyricsFetcherOrder = sourceOptions.LyricsFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                LyricsDownloadLanguages =
                    sourceOptions.LyricsDownloadLanguages?.Clone() as string[] ?? Array.Empty<string>(),
                DisabledSubtitleFetchers =
                    sourceOptions.DisabledSubtitleFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                SubtitleFetcherOrder =
                    sourceOptions.SubtitleFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                SkipSubtitlesIfEmbeddedSubtitlesPresent = sourceOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent,
                SkipSubtitlesIfAudioTrackMatches = sourceOptions.SkipSubtitlesIfAudioTrackMatches,
                SubtitleDownloadLanguages =
                    sourceOptions.SubtitleDownloadLanguages?.Clone() as string[] ?? Array.Empty<string>(),
                SubtitleDownloadMaxAgeDays = sourceOptions.SubtitleDownloadMaxAgeDays,
                RequirePerfectSubtitleMatch = sourceOptions.RequirePerfectSubtitleMatch,
                SaveSubtitlesWithMedia = sourceOptions.SaveSubtitlesWithMedia,
                ForcedSubtitlesOnly = sourceOptions.ForcedSubtitlesOnly,
                HearingImpairedSubtitlesOnly = sourceOptions.HearingImpairedSubtitlesOnly,
                CollapseSingleItemFolders = sourceOptions.CollapseSingleItemFolders,
                EnableAdultMetadata = sourceOptions.EnableAdultMetadata,
                ImportCollections = sourceOptions.ImportCollections,
                MinCollectionItems = sourceOptions.MinCollectionItems,
                MusicFolderStructure = sourceOptions.MusicFolderStructure,
                MinResumePct = sourceOptions.MinResumePct,
                MaxResumePct = sourceOptions.MaxResumePct,
                MinResumeDurationSeconds = sourceOptions.MinResumeDurationSeconds,
                ThumbnailImagesIntervalSeconds = sourceOptions.ThumbnailImagesIntervalSeconds,
                SampleIgnoreSize = sourceOptions.SampleIgnoreSize,
                TypeOptions = sourceOptions.TypeOptions.Select(t => new TypeOptions
                {
                    Type = t.Type,
                    MetadataFetchers = t.MetadataFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                    MetadataFetcherOrder = t.MetadataFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                    ImageFetchers = t.ImageFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                    ImageFetcherOrder = t.ImageFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                    ImageOptions = t.ImageOptions?.Select(i =>
                            new ImageOption { Type = i.Type, Limit = i.Limit, MinWidth = i.MinWidth })
                            .ToArray() ?? Array.Empty<ImageOption>()
                })
                    .ToArray()
            };

            return targetOptions;
        }
    }
}
