using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Provider;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 按 TMDB 剧集组或本地映射重定向剧集元数据与图片抓取。
    /// </summary>
    public static class MovieDbEpisodeGroup
    {
        private sealed class SeasonGroupName
        {
            public int? LookupSeasonNumber { get; set; }

            public string GroupName { get; set; }
        }

        private sealed class SeasonEpisodeMapping
        {
            public string EpisodeGroupId { get; set; }

            public int? LookupSeasonNumber { get; set; }

            public int? LookupEpisodeNumber { get; set; }

            public int? MappedSeasonNumber { get; set; }

            public int? MappedEpisodeNumber { get; set; }
        }

        private sealed class EpisodeGroupResponse
        {
            public string id { get; set; }

            public string description { get; set; }

            public List<EpisodeGroup> groups { get; set; }
        }

        private sealed class EpisodeGroup
        {
            public string name { get; set; }

            public int order { get; set; }

            public List<GroupEpisode> episodes { get; set; }
        }

        private sealed class GroupEpisode
        {
            public int episode_number { get; set; }

            public int season_number { get; set; }

            public int order { get; set; }
        }

        private static readonly object InitLock = new object();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

        private static readonly AsyncLocal<Series> CurrentSeries = new AsyncLocal<Series>();
        private static readonly ConcurrentDictionary<string, (DateTimeOffset At, EpisodeGroupResponse Data)> OnlineCache =
            new ConcurrentDictionary<string, (DateTimeOffset At, EpisodeGroupResponse Data)>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (DateTime LastWrite, EpisodeGroupResponse Data)> LocalCache =
            new ConcurrentDictionary<string, (DateTime LastWrite, EpisodeGroupResponse Data)>(StringComparer.OrdinalIgnoreCase);

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool localEpisodeGroupEnabled;
        private static bool waitingForMovieDbAssembly;
        private static bool patchesInstalled;
        private static bool warnedMissingApiKey;

        private static MethodInfo seriesGetMetadata;
        private static MethodInfo seasonGetMetadata;
        private static MethodInfo episodeGetMetadata;
        private static MethodInfo seasonGetImages;
        private static MethodInfo episodeGetImages;
        private static MethodInfo canRefreshMetadata;

        public static bool IsReady => patchesInstalled;

        public static bool IsWaiting => waitingForMovieDbAssembly && !patchesInstalled;

        public static string LocalEpisodeGroupFileName => "episodegroup.json";

        public static void Initialize(ILogger pluginLogger, bool enable, bool enableLocalEpisodeGroup)
        {
            logger = pluginLogger;
            isEnabled = enable;
            localEpisodeGroupEnabled = enableLocalEpisodeGroup;

            lock (InitLock)
            {
                if (patchesInstalled)
                {
                    return;
                }

                if (TryGetLoadedMovieDbAssembly(out var assembly))
                {
                    TryInstallPatches(assembly);
                    return;
                }

                if (!waitingForMovieDbAssembly)
                {
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    waitingForMovieDbAssembly = true;
                    PatchLog.Waiting(logger, nameof(MovieDbEpisodeGroup), "MovieDb", isEnabled);
                }
            }
        }

        public static void Configure(bool enable, bool enableLocalEpisodeGroup)
        {
            isEnabled = enable;
            localEpisodeGroupEnabled = enableLocalEpisodeGroup;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            var loadedAssembly = args?.LoadedAssembly;
            if (loadedAssembly == null)
            {
                return;
            }

            if (!string.Equals(loadedAssembly.GetName().Name, "MovieDb", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (InitLock)
            {
                if (patchesInstalled)
                {
                    return;
                }

                TryInstallPatches(loadedAssembly);
            }
        }

        private static bool TryGetLoadedMovieDbAssembly(out Assembly assembly)
        {
            assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "MovieDb", StringComparison.OrdinalIgnoreCase));
            return assembly != null;
        }

        private static void TryInstallPatches(Assembly assembly)
        {
            try
            {
                ResolveMethods(assembly);
                harmony ??= new Harmony("mediainfokeeper.moviedb.episodegroup");

                var patchCount = 0;
                patchCount += PatchMethod(seriesGetMetadata,
                    prefix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(SeriesGetMetadataPrefix)),
                    postfix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(SeriesGetMetadataPostfix)));
                patchCount += PatchMethod(seasonGetMetadata,
                    prefix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(SeasonGetMetadataPrefix)),
                    postfix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(SeasonGetMetadataPostfix)));
                patchCount += PatchMethod(episodeGetMetadata,
                    prefix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(EpisodeGetMetadataPrefix)),
                    postfix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(EpisodeGetMetadataPostfix)));
                patchCount += PatchMethod(seasonGetImages,
                    prefix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(SeasonGetImagesPrefix)),
                    postfix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(SeasonGetImagesPostfix)));
                patchCount += PatchMethod(episodeGetImages,
                    prefix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(EpisodeGetImagesPrefix)),
                    postfix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(EpisodeGetImagesPostfix)));
                patchCount += PatchMethod(canRefreshMetadata,
                    prefix: new HarmonyMethod(typeof(MovieDbEpisodeGroup), nameof(CanRefreshMetadataPrefix)));

                patchesInstalled = patchCount > 0;

                if (waitingForMovieDbAssembly)
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                    waitingForMovieDbAssembly = false;
                }
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(MovieDbEpisodeGroup), ex.Message);
                logger?.Error("补丁异常：模块={0}，详情={1}", nameof(MovieDbEpisodeGroup), ex);
                harmony = null;
            }
        }

        private static int PatchMethod(MethodInfo method, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            if (method == null || harmony == null)
            {
                return 0;
            }

            harmony.Patch(method, prefix: prefix, postfix: postfix);
            PatchLog.Patched(logger, nameof(MovieDbEpisodeGroup), method);
            return 1;
        }

        private static void ResolveMethods(Assembly assembly)
        {
            var assemblyVersion = assembly.GetName().Version;
            var movieDbSeriesProvider = assembly.GetType("MovieDb.MovieDbSeriesProvider", false);
            seriesGetMetadata = PatchMethodResolver.Resolve(
                movieDbSeriesProvider,
                assemblyVersion,
                new MethodSignatureProfile
                {
                    Name = "moviedbseriesprovider-getmetadata-exact",
                    MethodName = "GetMetadata",
                    BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                    ParameterTypes = new[] { typeof(SeriesInfo), typeof(CancellationToken) },
                    ReturnType = typeof(Task<>).MakeGenericType(typeof(MetadataResult<>).MakeGenericType(typeof(Series))),
                    IsStatic = false
                },
                logger,
                "MovieDbEpisodeGroup.MovieDbSeriesProvider.GetMetadata");

            var movieDbSeasonProvider = assembly.GetType("MovieDb.MovieDbSeasonProvider", false);
            seasonGetMetadata = PatchMethodResolver.Resolve(
                movieDbSeasonProvider,
                assemblyVersion,
                new MethodSignatureProfile
                {
                    Name = "moviedbseasonprovider-getmetadata-exact",
                    MethodName = "GetMetadata",
                    BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                    ParameterTypes = new[] { typeof(RemoteMetadataFetchOptions<SeasonInfo>), typeof(CancellationToken) },
                    ReturnType = typeof(Task<>).MakeGenericType(typeof(MetadataResult<>).MakeGenericType(typeof(Season))),
                    IsStatic = false
                },
                logger,
                "MovieDbEpisodeGroup.MovieDbSeasonProvider.GetMetadata");

            var movieDbEpisodeProvider = assembly.GetType("MovieDb.MovieDbEpisodeProvider", false);
            episodeGetMetadata = PatchMethodResolver.Resolve(
                movieDbEpisodeProvider,
                assemblyVersion,
                new MethodSignatureProfile
                {
                    Name = "moviedbepisodeprovider-getmetadata-exact",
                    MethodName = "GetMetadata",
                    BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                    ParameterTypes = new[] { typeof(RemoteMetadataFetchOptions<EpisodeInfo>), typeof(CancellationToken) },
                    ReturnType = typeof(Task<>).MakeGenericType(typeof(MetadataResult<>).MakeGenericType(typeof(Episode))),
                    IsStatic = false
                },
                logger,
                "MovieDbEpisodeGroup.MovieDbEpisodeProvider.GetMetadata");

            var movieDbSeasonImageProvider = assembly.GetType("MovieDb.MovieDbSeasonImageProvider", false);
            seasonGetImages = PatchMethodResolver.Resolve(
                movieDbSeasonImageProvider,
                assemblyVersion,
                new MethodSignatureProfile
                {
                    Name = "moviedbseasonimageprovider-getimages-exact",
                    MethodName = "GetImages",
                    BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                    ParameterTypes = new[] { typeof(RemoteImageFetchOptions), typeof(CancellationToken) },
                    ReturnType = typeof(Task<>).MakeGenericType(typeof(IEnumerable<RemoteImageInfo>)),
                    IsStatic = false
                },
                logger,
                "MovieDbEpisodeGroup.MovieDbSeasonImageProvider.GetImages");

            var movieDbEpisodeImageProvider = assembly.GetType("MovieDb.MovieDbEpisodeImageProvider", false);
            episodeGetImages = PatchMethodResolver.Resolve(
                movieDbEpisodeImageProvider,
                assemblyVersion,
                new MethodSignatureProfile
                {
                    Name = "moviedbepisodeimageprovider-getimages-exact",
                    MethodName = "GetImages",
                    BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                    ParameterTypes = new[] { typeof(RemoteImageFetchOptions), typeof(CancellationToken) },
                    ReturnType = typeof(Task<>).MakeGenericType(typeof(IEnumerable<RemoteImageInfo>)),
                    IsStatic = false
                },
                logger,
                "MovieDbEpisodeGroup.MovieDbEpisodeImageProvider.GetImages");

            canRefreshMetadata = ResolveCanRefresh(assemblyVersion);
        }

        private static MethodInfo ResolveCanRefresh(Version movieDbAssemblyVersion)
        {
            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager");
                if (providerManager == null)
                {
                    return null;
                }

                return PatchMethodResolver.Resolve(
                    providerManager,
                    movieDbAssemblyVersion ?? embyProviders.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "providermanager-canrefresh-exact",
                        MethodName = "CanRefresh",
                        BindingFlags = BindingFlags.Static | BindingFlags.NonPublic,
                        ParameterTypes = new[]
                        {
                            typeof(IMetadataProvider),
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(bool),
                            typeof(bool),
                            typeof(bool)
                        },
                        IsStatic = true
                    },
                    logger,
                    "MovieDbEpisodeGroup.ProviderManager.CanRefresh");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        private static bool CanRefreshMetadataPrefix(object[] __args)
        {
            if (!isEnabled || !localEpisodeGroupEnabled || __args == null || __args.Length < 2)
            {
                return true;
            }

            if (!(__args[0] is IMetadataProvider provider) || !(__args[1] is BaseItem item))
            {
                return true;
            }

            if (!(provider is IRemoteMetadataProvider) || !string.Equals(provider.Name, "TheMovieDb", StringComparison.Ordinal))
            {
                return true;
            }

            var providerName = provider.GetType().FullName;
            if (item is Episode episode && string.Equals(providerName, "MovieDb.MovieDbEpisodeProvider", StringComparison.Ordinal))
            {
                CurrentSeries.Value = episode.Series;
            }
            else if (item is Season season && string.Equals(providerName, "MovieDb.MovieDbSeasonProvider", StringComparison.Ordinal))
            {
                CurrentSeries.Value = season.Series;
            }
            else if (item is Series series && string.Equals(providerName, "MovieDb.MovieDbSeriesProvider", StringComparison.Ordinal))
            {
                CurrentSeries.Value = series;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool SeriesGetMetadataPrefix(SeriesInfo info, CancellationToken cancellationToken, out string __state)
        {
            __state = null;

            if (!isEnabled || !localEpisodeGroupEnabled || CurrentSeries.Value?.ContainingFolderPath == null)
            {
                return true;
            }

            try
            {
                var series = CurrentSeries.Value;
                CurrentSeries.Value = null;

                var localEpisodeGroupPath = Path.Combine(series.ContainingFolderPath, LocalEpisodeGroupFileName);
                var episodeGroup = FetchLocalEpisodeGroup(localEpisodeGroupPath);
                if (!string.IsNullOrWhiteSpace(episodeGroup?.id))
                {
                    __state = episodeGroup.id.Trim();
                    logger?.Info("MovieDbEpisodeGroup 命中本地剧集组: series='{0}', id={1}",
                        series.Name ?? series.Path ?? series.InternalId.ToString(),
                        __state);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("SeriesGetMetadataPrefix failed: {0}", ex.Message);
            }

            return true;
        }

        [HarmonyPostfix]
        private static void SeriesGetMetadataPostfix(Task __result, string __state)
        {
            if (string.IsNullOrWhiteSpace(__state))
            {
                return;
            }

            try
            {
                var metadataResult = GetTaskResult(__result);
                var hasMetadata = (bool?)GetPropertyValue(metadataResult, "HasMetadata") ?? false;
                var item = GetPropertyValue(metadataResult, "Item") as Series;
                if (!hasMetadata || item == null)
                {
                    return;
                }

                item.SetProviderId(MovieDbEpisodeGroupExternalId.StaticName, __state);
            }
            catch (Exception ex)
            {
                logger?.Debug("SeriesGetMetadataPostfix failed: {0}", ex.Message);
            }
        }

        [HarmonyPrefix]
        private static bool SeasonGetMetadataPrefix(RemoteMetadataFetchOptions<SeasonInfo> options,
            CancellationToken cancellationToken, out SeasonGroupName __state)
        {
            __state = null;

            if (!isEnabled)
            {
                return true;
            }

            try
            {
                var season = options?.SearchInfo;
                if (season == null)
                {
                    return true;
                }

                season.SeriesProviderIds.TryGetValue(MovieDbEpisodeGroupExternalId.StaticName, out var episodeGroupId);
                episodeGroupId = episodeGroupId?.Trim();

                string localEpisodeGroupPath = null;
                EpisodeGroupResponse episodeGroup = null;

                if (localEpisodeGroupEnabled && CurrentSeries.Value?.ContainingFolderPath != null)
                {
                    var series = CurrentSeries.Value;
                    CurrentSeries.Value = null;

                    localEpisodeGroupPath = Path.Combine(series.ContainingFolderPath, LocalEpisodeGroupFileName);
                    episodeGroup = FetchLocalEpisodeGroup(localEpisodeGroupPath);
                    if (episodeGroup != null && string.IsNullOrWhiteSpace(episodeGroupId) && !string.IsNullOrWhiteSpace(episodeGroup.id))
                    {
                        series.SetProviderId(MovieDbEpisodeGroupExternalId.StaticName, episodeGroup.id);
                        episodeGroupId = episodeGroup.id;
                    }
                }

                if (episodeGroup == null &&
                    season.IndexNumber.HasValue &&
                    season.IndexNumber.Value > 0 &&
                    season.SeriesProviderIds.TryGetValue(MetadataProviders.Tmdb.ToString(), out var seriesTmdbId) &&
                    !string.IsNullOrWhiteSpace(episodeGroupId))
                {
                    episodeGroup = FetchOnlineEpisodeGroup(seriesTmdbId, episodeGroupId, season.MetadataLanguage, localEpisodeGroupPath);
                }

                var matchingSeason = episodeGroup?.groups?.FirstOrDefault(g => g.order == season.IndexNumber.Value);
                if (matchingSeason != null)
                {
                    __state = new SeasonGroupName
                    {
                        LookupSeasonNumber = season.IndexNumber,
                        GroupName = matchingSeason.name
                    };
                    logger?.Info("MovieDbEpisodeGroup 季映射命中: S{0} -> 组名='{1}'",
                        season.IndexNumber.Value,
                        matchingSeason.name ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("SeasonGetMetadataPrefix failed: {0}", ex.Message);
            }

            return true;
        }

        [HarmonyPostfix]
        private static void SeasonGetMetadataPostfix(Task __result, SeasonGroupName __state)
        {
            if (__state == null)
            {
                return;
            }

            try
            {
                var metadataResult = GetTaskResult(__result);
                if (metadataResult == null)
                {
                    return;
                }

                var item = GetPropertyValue(metadataResult, "Item") as Season;
                if (item == null)
                {
                    item = new Season();
                    SetPropertyValue(metadataResult, "Item", item);
                }

                item.IndexNumber = __state.LookupSeasonNumber;
                if (!string.IsNullOrWhiteSpace(__state.GroupName))
                {
                    item.Name = __state.GroupName;
                }

                item.PremiereDate = null;
                item.ProductionYear = null;
                SetPropertyValue(metadataResult, "HasMetadata", true);
            }
            catch (Exception ex)
            {
                logger?.Debug("SeasonGetMetadataPostfix failed: {0}", ex.Message);
            }
        }

        [HarmonyPrefix]
        private static bool EpisodeGetMetadataPrefix(RemoteMetadataFetchOptions<EpisodeInfo> options,
            CancellationToken cancellationToken, out SeasonEpisodeMapping __state)
        {
            __state = null;

            if (!isEnabled)
            {
                return true;
            }

            try
            {
                var episode = options?.SearchInfo;
                if (episode == null)
                {
                    return true;
                }

                var localEpisodeGroupPath = default(string);
                var series = default(Series);
                if (localEpisodeGroupEnabled && CurrentSeries.Value?.ContainingFolderPath != null)
                {
                    series = CurrentSeries.Value;
                    CurrentSeries.Value = null;
                    localEpisodeGroupPath = Path.Combine(series.ContainingFolderPath, LocalEpisodeGroupFileName);
                }

                if (!episode.SeriesProviderIds.TryGetValue(MetadataProviders.Tmdb.ToString(), out var seriesTmdbId))
                {
                    return true;
                }

                episode.SeriesProviderIds.TryGetValue(MovieDbEpisodeGroupExternalId.StaticName, out var episodeGroupId);
                episodeGroupId = episodeGroupId?.Trim();

                var mapped = MapSeasonEpisode(
                    seriesTmdbId,
                    episodeGroupId,
                    episode.MetadataLanguage,
                    episode.ParentIndexNumber,
                    episode.IndexNumber,
                    localEpisodeGroupPath);
                if (mapped == null)
                {
                    return true;
                }

                __state = mapped;
                episode.ParentIndexNumber = mapped.MappedSeasonNumber;
                episode.IndexNumber = mapped.MappedEpisodeNumber;
                logger?.Debug("MovieDbEpisodeGroup 集映射命中: S{0:00}E{1:00} -> TMDB S{2:00}E{3:00}",
                    mapped.LookupSeasonNumber ?? 0,
                    mapped.LookupEpisodeNumber ?? 0,
                    mapped.MappedSeasonNumber ?? 0,
                    mapped.MappedEpisodeNumber ?? 0);

                if (series != null && string.IsNullOrWhiteSpace(episodeGroupId) && !string.IsNullOrWhiteSpace(mapped.EpisodeGroupId))
                {
                    series.SetProviderId(MovieDbEpisodeGroupExternalId.StaticName, mapped.EpisodeGroupId);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("EpisodeGetMetadataPrefix failed: {0}", ex.Message);
            }

            return true;
        }

        [HarmonyPostfix]
        private static void EpisodeGetMetadataPostfix(Task __result, SeasonEpisodeMapping __state)
        {
            if (__state == null)
            {
                return;
            }

            try
            {
                var metadataResult = GetTaskResult(__result);
                var item = GetPropertyValue(metadataResult, "Item") as Episode;
                var hasMetadata = (bool?)GetPropertyValue(metadataResult, "HasMetadata") ?? false;
                if (!hasMetadata || item == null)
                {
                    return;
                }

                item.ParentIndexNumber = __state.LookupSeasonNumber;
                item.IndexNumber = __state.LookupEpisodeNumber;
            }
            catch (Exception ex)
            {
                logger?.Debug("EpisodeGetMetadataPostfix failed: {0}", ex.Message);
            }
        }

        [HarmonyPrefix]
        private static bool SeasonGetImagesPrefix(RemoteImageFetchOptions options,
            CancellationToken cancellationToken, out int? __state)
        {
            __state = null;

            if (!isEnabled || !(options?.Item is Season season))
            {
                return true;
            }

            try
            {
                var seriesTmdbId = season.Series?.GetProviderId(MetadataProviders.Tmdb);
                var episodeGroupId = season.Series?.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName)?.Trim();
                var localEpisodeGroupPath = localEpisodeGroupEnabled && !string.IsNullOrWhiteSpace(season.Series?.ContainingFolderPath)
                    ? Path.Combine(season.Series.ContainingFolderPath, LocalEpisodeGroupFileName)
                    : null;

                EpisodeGroupResponse episodeGroup = null;
                if (localEpisodeGroupEnabled && !string.IsNullOrWhiteSpace(localEpisodeGroupPath))
                {
                    episodeGroup = FetchLocalEpisodeGroup(localEpisodeGroupPath);
                }

                if (episodeGroup == null &&
                    season.IndexNumber.HasValue &&
                    season.IndexNumber.Value > 0 &&
                    !string.IsNullOrWhiteSpace(seriesTmdbId) &&
                    !string.IsNullOrWhiteSpace(episodeGroupId))
                {
                    episodeGroup = FetchOnlineEpisodeGroup(seriesTmdbId, episodeGroupId, null, localEpisodeGroupPath);
                }

                var mappedSeasonNumber = episodeGroup?.groups?
                    .FirstOrDefault(g => g.order == season.IndexNumber)?
                    .episodes?
                    .GroupBy(e => e.season_number)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)
                    .FirstOrDefault()
                    ?.Key;

                var maxSeasonNumber = episodeGroup?.groups?
                    .SelectMany(g => g.episodes ?? new List<GroupEpisode>())
                    .Select(e => (int?)e.season_number)
                    .Max();

                if (mappedSeasonNumber.HasValue &&
                    season.IndexNumber.HasValue &&
                    maxSeasonNumber.HasValue &&
                    season.IndexNumber.Value > maxSeasonNumber.Value)
                {
                    __state = season.IndexNumber.Value;
                    season.IndexNumber = mappedSeasonNumber;
                    logger?.Debug("MovieDbEpisodeGroup 季图片映射命中: S{0:00} -> TMDB S{1:00}",
                        __state.Value,
                        mappedSeasonNumber.Value);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("SeasonGetImagesPrefix failed: {0}", ex.Message);
            }

            return true;
        }

        [HarmonyPostfix]
        private static void SeasonGetImagesPostfix(RemoteImageFetchOptions options, int? __state)
        {
            if (!__state.HasValue || !(options?.Item is Season season))
            {
                return;
            }

            season.IndexNumber = __state.Value;
        }

        [HarmonyPrefix]
        private static bool EpisodeGetImagesPrefix(RemoteImageFetchOptions options,
            CancellationToken cancellationToken, out SeasonEpisodeMapping __state)
        {
            __state = null;

            if (!isEnabled || !(options?.Item is Episode episode))
            {
                return true;
            }

            try
            {
                var seriesTmdbId = episode.Series?.GetProviderId(MetadataProviders.Tmdb);
                if (string.IsNullOrWhiteSpace(seriesTmdbId))
                {
                    return true;
                }

                var episodeGroupId = episode.Series.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName)?.Trim();
                var localEpisodeGroupPath = localEpisodeGroupEnabled && !string.IsNullOrWhiteSpace(episode.Series?.ContainingFolderPath)
                    ? Path.Combine(episode.Series.ContainingFolderPath, LocalEpisodeGroupFileName)
                    : null;
                var mapped = MapSeasonEpisode(
                    seriesTmdbId,
                    episodeGroupId,
                    null,
                    episode.ParentIndexNumber,
                    episode.IndexNumber,
                    localEpisodeGroupPath);
                if (mapped == null)
                {
                    return true;
                }

                __state = mapped;
                episode.ParentIndexNumber = mapped.MappedSeasonNumber;
                episode.IndexNumber = mapped.MappedEpisodeNumber;
                logger?.Debug("MovieDbEpisodeGroup 图片映射命中: S{0:00}E{1:00} -> TMDB S{2:00}E{3:00}",
                    mapped.LookupSeasonNumber ?? 0,
                    mapped.LookupEpisodeNumber ?? 0,
                    mapped.MappedSeasonNumber ?? 0,
                    mapped.MappedEpisodeNumber ?? 0);
            }
            catch (Exception ex)
            {
                logger?.Debug("EpisodeGetImagesPrefix failed: {0}", ex.Message);
            }

            return true;
        }

        [HarmonyPostfix]
        private static void EpisodeGetImagesPostfix(RemoteImageFetchOptions options, SeasonEpisodeMapping __state)
        {
            if (__state == null || !(options?.Item is Episode episode))
            {
                return;
            }

            episode.ParentIndexNumber = __state.LookupSeasonNumber;
            episode.IndexNumber = __state.LookupEpisodeNumber;
        }

        private static SeasonEpisodeMapping MapSeasonEpisode(
            string seriesTmdbId,
            string episodeGroupId,
            string language,
            int? lookupSeasonNumber,
            int? lookupEpisodeNumber,
            string localEpisodeGroupPath)
        {
            if (!lookupSeasonNumber.HasValue || !lookupEpisodeNumber.HasValue)
            {
                return null;
            }

            EpisodeGroupResponse episodeGroup = null;
            if (localEpisodeGroupEnabled && !string.IsNullOrWhiteSpace(localEpisodeGroupPath))
            {
                episodeGroup = FetchLocalEpisodeGroup(localEpisodeGroupPath);
            }

            if (episodeGroup == null && !string.IsNullOrWhiteSpace(episodeGroupId))
            {
                episodeGroup = FetchOnlineEpisodeGroup(seriesTmdbId, episodeGroupId, language, localEpisodeGroupPath);
            }

            if (episodeGroup?.groups == null)
            {
                return null;
            }

            var matchingEpisode = episodeGroup.groups
                .Where(g => g.order == lookupSeasonNumber.Value)
                .SelectMany(g => g.episodes ?? new List<GroupEpisode>())
                .FirstOrDefault(e => e.order + 1 == lookupEpisodeNumber.Value);
            if (matchingEpisode == null)
            {
                return null;
            }

            return new SeasonEpisodeMapping
            {
                EpisodeGroupId = episodeGroup.id,
                LookupSeasonNumber = lookupSeasonNumber,
                LookupEpisodeNumber = lookupEpisodeNumber,
                MappedSeasonNumber = matchingEpisode.season_number,
                MappedEpisodeNumber = matchingEpisode.episode_number
            };
        }

        private static EpisodeGroupResponse FetchLocalEpisodeGroup(string localEpisodeGroupPath)
        {
            if (string.IsNullOrWhiteSpace(localEpisodeGroupPath) || !File.Exists(localEpisodeGroupPath))
            {
                return null;
            }

            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(localEpisodeGroupPath);
                if (LocalCache.TryGetValue(localEpisodeGroupPath, out var cached) && cached.LastWrite == lastWrite)
                {
                    return cached.Data;
                }

                var raw = File.ReadAllText(localEpisodeGroupPath);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                var data = JsonSerializer.Deserialize<EpisodeGroupResponse>(raw, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (data != null)
                {
                    LocalCache[localEpisodeGroupPath] = (lastWrite, data);
                    logger?.Debug("MovieDbEpisodeGroup 已加载本地剧集组: file={0}, id={1}",
                        localEpisodeGroupPath,
                        data.id ?? string.Empty);
                }

                return data;
            }
            catch (Exception ex)
            {
                logger?.Debug("读取本地剧集组失败: {0}", ex.Message);
                return null;
            }
        }

        private static EpisodeGroupResponse FetchOnlineEpisodeGroup(
            string seriesTmdbId,
            string episodeGroupId,
            string language,
            string localEpisodeGroupPath)
        {
            var cacheKey = $"{seriesTmdbId}|{episodeGroupId}|{language}";
            if (OnlineCache.TryGetValue(cacheKey, out var cached) &&
                ConfiguredDateTime.NowOffset - cached.At < CacheDuration)
            {
                return cached.Data;
            }

            var url = BuildEpisodeGroupUrl(episodeGroupId, language);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            try
            {
                var httpClient = Plugin.SharedHttpClient;
                if (httpClient == null)
                {
                    logger?.Debug("获取在线剧集组失败: IHttpClient 不可用, id={0}", episodeGroupId);
                    return null;
                }

                using var response = httpClient.GetResponse(new HttpRequestOptions
                {
                    Url = url,
                    TimeoutMs = 8000
                }).GetAwaiter().GetResult();
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    logger?.Debug("获取在线剧集组失败: status={0}, id={1}", (int)response.StatusCode, episodeGroupId);
                    return null;
                }

                using var reader = new StreamReader(response.Content);
                var raw = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                var data = JsonSerializer.Deserialize<EpisodeGroupResponse>(raw, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (data == null)
                {
                    return null;
                }

                if (IsValidHttpUrl(episodeGroupId) && string.IsNullOrWhiteSpace(data.id))
                {
                    data.id = episodeGroupId;
                }

                OnlineCache[cacheKey] = (ConfiguredDateTime.NowOffset, data);
                logger?.Debug("MovieDbEpisodeGroup 已加载在线剧集组: tmdb={0}, id={1}, groups={2}",
                    seriesTmdbId,
                    data.id ?? episodeGroupId,
                    data.groups?.Count ?? 0);

                if (localEpisodeGroupEnabled && !string.IsNullOrWhiteSpace(localEpisodeGroupPath))
                {
                    TryWriteLocalEpisodeGroup(localEpisodeGroupPath, data);
                }

                return data;
            }
            catch (Exception ex)
            {
                logger?.Error("获取在线剧集组失败: {0}", ex.Message);
                return null;
            }
        }

        private static void TryWriteLocalEpisodeGroup(string localEpisodeGroupPath, EpisodeGroupResponse data)
        {
            try
            {
                var directory = Path.GetDirectoryName(localEpisodeGroupPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var raw = JsonSerializer.Serialize(data, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(localEpisodeGroupPath, raw);
                var lastWrite = File.GetLastWriteTimeUtc(localEpisodeGroupPath);
                LocalCache[localEpisodeGroupPath] = (lastWrite, data);
                logger?.Debug("MovieDbEpisodeGroup 已写入本地剧集组: file={0}, id={1}",
                    localEpisodeGroupPath,
                    data?.id ?? string.Empty);
            }
            catch (Exception ex)
            {
                logger?.Debug("写入本地剧集组失败: {0}", ex.Message);
            }
        }

        private static string BuildEpisodeGroupUrl(string episodeGroupId, string language)
        {
            if (string.IsNullOrWhiteSpace(episodeGroupId))
            {
                return null;
            }

            episodeGroupId = episodeGroupId.Trim();
            if (IsValidHttpUrl(episodeGroupId))
            {
                return episodeGroupId;
            }

            var options = Plugin.Instance?.Options;
            var apiKey = options?.GetNetWorkOptions()?.AlternativeTmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (!warnedMissingApiKey)
                {
                    warnedMissingApiKey = true;
                    logger?.Warn("MovieDbEpisodeGroup 在线刮削需要 TMDB API 密钥；请在 NetWork 页设置“自定义 TMDB API 密钥”，或改用本地 episodegroup.json。");
                }

                return null;
            }

            var baseUrl = options?.GetNetWorkOptions()?.AlternativeTmdbApiUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "https://api.themoviedb.org";
            }
            else
            {
                baseUrl = baseUrl.Trim();
                if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    baseUrl = "https://" + baseUrl;
                }
            }

            baseUrl = baseUrl.TrimEnd('/');
            var url = $"{baseUrl}/3/tv/episode_group/{Uri.EscapeDataString(episodeGroupId)}?api_key={Uri.EscapeDataString(apiKey)}";
            if (!string.IsNullOrWhiteSpace(language))
            {
                url += $"&language={Uri.EscapeDataString(language)}";
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                logger?.Warn("MovieDbEpisodeGroup URL 无效: {0}", url);
                return null;
            }

            return url;
        }

        private static bool IsValidHttpUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static object GetTaskResult(Task task)
        {
            if (task == null)
            {
                return null;
            }

            task.GetAwaiter().GetResult();
            var taskType = task.GetType();
            var resultProperty = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            return resultProperty?.GetValue(task);
        }

        private static object GetPropertyValue(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property?.CanRead == true ? property.GetValue(instance) : null;
        }

        private static void SetPropertyValue(object instance, string name, object value)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property?.CanWrite == true)
            {
                property.SetValue(instance, value);
            }
        }
    }
}
