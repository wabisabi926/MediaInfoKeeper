using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System.Linq;
using MediaInfoKeeper.External;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 为 Emby “查看缺少的集”接入自定义 TMDB provider，并支持剧集组映射。
    /// </summary>
    public static class EnhanceMissingEpisodes
    {
        private static readonly object InitLock = new object();

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool patchesInstalled;
        private static MethodInfo getAllEpisodesBySeries;
        private static MethodInfo getAllEpisodesBySeriesInfo;
        private static MethodInfo getMissingEpisodesApi;
        private static PropertyInfo includeUnairedProperty;

        public static bool IsReady => patchesInstalled;

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            logger = pluginLogger;
            isEnabled = enable;

            lock (InitLock)
            {
                if (patchesInstalled)
                {
                    return;
                }

                try
                {
                    var embyProviders = Assembly.Load("Emby.Providers");
                    var providerManager = embyProviders?.GetType("Emby.Providers.Manager.ProviderManager", false);
                    if (providerManager == null)
                    {
                        PatchLog.InitFailed(logger, nameof(EnhanceMissingEpisodes), "未找到 ProviderManager");
                        return;
                    }

                    var version = providerManager.Assembly.GetName().Version;
                    getAllEpisodesBySeries = PatchMethodResolver.Resolve(
                        providerManager,
                        version,
                        new MethodSignatureProfile
                        {
                            Name = "provider-manager-get-all-episodes-series",
                            MethodName = "GetAllEpisodes",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                            ParameterTypes = new[]
                            {
                                typeof(Series),
                                typeof(LibraryOptions),
                                typeof(CancellationToken)
                            },
                            ReturnType = typeof(Task<MediaBrowser.Model.Providers.RemoteSearchResult[]>)
                        },
                        logger,
                        "EnhanceMissingEpisodes.GetAllEpisodesSeries");

                    getAllEpisodesBySeriesInfo = PatchMethodResolver.Resolve(
                        providerManager,
                        version,
                        new MethodSignatureProfile
                        {
                            Name = "provider-manager-get-all-episodes-seriesinfo",
                            MethodName = "GetAllEpisodes",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                            ParameterTypes = new[]
                            {
                                typeof(SeriesInfo),
                                typeof(LibraryOptions),
                                typeof(CancellationToken)
                            },
                            ReturnType = typeof(Task<MediaBrowser.Model.Providers.RemoteSearchResult[]>)
                        },
                        logger,
                        "EnhanceMissingEpisodes.GetAllEpisodesSeriesInfo");

                    if (getAllEpisodesBySeries == null && getAllEpisodesBySeriesInfo == null)
                    {
                        PatchLog.InitFailed(logger, nameof(EnhanceMissingEpisodes), "GetAllEpisodes 重载未找到");
                    }

                    var embyApi = Assembly.Load("Emby.Api");
                    var tvShowsService = embyApi?.GetType("Emby.Api.TvShowsService", false);
                    var getMissingEpisodesRequestType = embyApi?.GetType("Emby.Api.GetMissingEpisodes", false);
                    includeUnairedProperty = getMissingEpisodesRequestType?.GetProperty("IncludeUnaired", BindingFlags.Instance | BindingFlags.Public);

                    if (tvShowsService == null || getMissingEpisodesRequestType == null || includeUnairedProperty == null)
                    {
                        PatchLog.InitFailed(logger, nameof(EnhanceMissingEpisodes), "未找到缺失剧集 API 类型");
                    }
                    else
                    {
                        getMissingEpisodesApi = PatchMethodResolver.Resolve(
                            tvShowsService,
                            tvShowsService.Assembly.GetName().Version,
                            new MethodSignatureProfile
                            {
                                Name = "tvshowsservice-get-missing-episodes",
                                MethodName = "Get",
                                BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                                ParameterTypes = new[]
                                {
                                    getMissingEpisodesRequestType
                                },
                                ReturnType = typeof(Task<object>)
                            },
                            logger,
                            "EnhanceMissingEpisodes.GetMissingEpisodesApi");
                    }

                    if (getAllEpisodesBySeries == null && getAllEpisodesBySeriesInfo == null && getMissingEpisodesApi == null)
                    {
                        return;
                    }

                    harmony ??= new Harmony("mediainfokeeper.missingepisodes");

                    if (getAllEpisodesBySeries != null)
                    {
                        harmony.Patch(
                            getAllEpisodesBySeries,
                            postfix: new HarmonyMethod(typeof(EnhanceMissingEpisodes), nameof(GetAllEpisodesSeriesPostfix)));
                        PatchLog.Patched(logger, nameof(EnhanceMissingEpisodes), getAllEpisodesBySeries);
                    }

                    if (getAllEpisodesBySeriesInfo != null)
                    {
                        harmony.Patch(
                            getAllEpisodesBySeriesInfo,
                            postfix: new HarmonyMethod(typeof(EnhanceMissingEpisodes), nameof(GetAllEpisodesSeriesInfoPostfix)));
                        PatchLog.Patched(logger, nameof(EnhanceMissingEpisodes), getAllEpisodesBySeriesInfo);
                    }

                    if (getMissingEpisodesApi != null)
                    {
                        harmony.Patch(
                            getMissingEpisodesApi,
                            prefix: new HarmonyMethod(typeof(EnhanceMissingEpisodes), nameof(GetMissingEpisodesApiPrefix)));
                        PatchLog.Patched(logger, nameof(EnhanceMissingEpisodes), getMissingEpisodesApi);
                    }

                    patchesInstalled = getAllEpisodesBySeries != null ||
                                       getAllEpisodesBySeriesInfo != null ||
                                       getMissingEpisodesApi != null;
                }
                catch (Exception ex)
                {
                    PatchLog.InitFailed(logger, nameof(EnhanceMissingEpisodes), ex.Message);
                    logger?.Error("补丁异常：模块={0}，详情={1}", nameof(EnhanceMissingEpisodes), ex);
                    harmony = null;
                }
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;
        }

        [HarmonyPrefix]
        private static void GetMissingEpisodesApiPrefix(object request)
        {
            if (!isEnabled || request == null || includeUnairedProperty == null || !includeUnairedProperty.CanWrite)
            {
                return;
            }

            includeUnairedProperty.SetValue(request, true);
        }

        [HarmonyPostfix]
        private static void GetAllEpisodesSeriesPostfix(
            Series series,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken,
            ref Task<MediaBrowser.Model.Providers.RemoteSearchResult[]> __result)
        {
            if (!isEnabled || series == null || __result == null)
            {
                return;
            }

            MovieDbSeriesProvider.CurrentSeriesContainingFolderPath = series.ContainingFolderPath;
            __result = MovieDbSeriesProvider.RewriteMissingEpisodesAsync(
                series.GetLookupInfo(libraryOptions),
                __result,
                logger,
                ((Folder)series).GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { typeof(Episode).Name },
                    Recursive = true
                })
                .OfType<Episode>()
                .SelectMany(episode =>
                {
                    if (episode.ParentIndexNumber == null || episode.IndexNumber == null)
                    {
                        return Enumerable.Empty<(int SeasonNumber, int EpisodeNumber)>();
                    }

                    var seasonNumber = episode.ParentIndexNumber.Value;
                    var endNumber = episode.IndexNumberEnd ?? episode.IndexNumber;
                    return Enumerable.Range(episode.IndexNumber.Value, endNumber.Value - episode.IndexNumber.Value + 1)
                        .Select(episodeNumber => (seasonNumber, episodeNumber));
                })
                .ToHashSet());
        }

        [HarmonyPostfix]
        private static void GetAllEpisodesSeriesInfoPostfix(
            SeriesInfo seriesInfo,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken,
            ref Task<MediaBrowser.Model.Providers.RemoteSearchResult[]> __result)
        {
            if (!isEnabled || seriesInfo == null || __result == null)
            {
                return;
            }

            var episodeGroupId = seriesInfo.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName)?.Trim();
            if (string.IsNullOrWhiteSpace(episodeGroupId))
            {
                return;
            }

            __result = MovieDbSeriesProvider.RewriteMissingEpisodesAsync(seriesInfo, __result, logger, null);
        }
    }
}
