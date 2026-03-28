using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 记录 TMDB 与 TVDB 元数据上下文，并优先返回原语言海报。
    /// </summary>
    public static class OriginalPoster
    {
        private sealed class ContextItem
        {
            public string TmdbId { get; set; }

            public string ImdbId { get; set; }

            public string TvdbId { get; set; }

            public string OriginalLanguage { get; set; }
        }

        private static readonly object InitLock = new object();
        private static readonly AsyncLocal<ContextItem> CurrentLookupItem = new AsyncLocal<ContextItem>();
        private static readonly AsyncLocal<bool> WasCalledByImageProvider = new AsyncLocal<bool>();

        private static readonly ConcurrentDictionary<string, ContextItem> CurrentItemsByTmdbId =
            new ConcurrentDictionary<string, ContextItem>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ContextItem> CurrentItemsByImdbId =
            new ConcurrentDictionary<string, ContextItem>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ContextItem> CurrentItemsByTvdbId =
            new ConcurrentDictionary<string, ContextItem>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> BackdropByLanguage =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;

        private static bool providerHookInstalled;
        private static bool movieDbHookInstalled;
        private static bool tvdbHookInstalled;
        private static bool waitingForMovieDbAssembly;
        private static bool waitingForTvdbAssembly;

        private static Assembly movieDbAssembly;
        private static Assembly tvdbAssembly;

        private static MethodInfo providerGetAvailableRemoteImages;
        private static MethodInfo providerGetAvailableRemoteImagesAsync;

        private static MethodInfo movieDbGetMovieInfo;
        private static MethodInfo movieDbEnsureSeriesInfo;
        private static MethodInfo movieDbGetBackdrops;
        private static PropertyInfo tmdbIdMovieDataTmdb;
        private static PropertyInfo imdbIdMovieDataTmdb;
        private static PropertyInfo originalLanguageMovieDataTmdb;
        private static PropertyInfo tmdbIdSeriesDataTmdb;
        private static PropertyInfo languagesSeriesDataTmdb;
        private static PropertyInfo tmdbFilePath;
        private static PropertyInfo tmdbIso6391;

        private static MethodInfo tvdbEnsureMovieInfo;
        private static MethodInfo tvdbEnsureSeriesInfo;
        private static PropertyInfo tvdbIdMovieDataTvdb;
        private static PropertyInfo originalLanguageMovieDataTvdb;
        private static PropertyInfo tvdbIdSeriesDataTvdb;
        private static PropertyInfo originalLanguageSeriesDataTvdb;

        public static bool IsReady => providerHookInstalled && (movieDbHookInstalled || tvdbHookInstalled);

        public static bool IsWaiting => (waitingForMovieDbAssembly && !movieDbHookInstalled) ||
                                       (waitingForTvdbAssembly && !tvdbHookInstalled);

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            logger = pluginLogger;
            isEnabled = enable;

            lock (InitLock)
            {
                harmony ??= new Harmony("mediainfokeeper.preferoriginalposter");

                if (!providerHookInstalled)
                {
                    TryInstallProviderHooks();
                }

                if (!movieDbHookInstalled)
                {
                    if (TryGetLoadedAssembly("MovieDb", out var movieDb))
                    {
                        TryInstallMovieDbHooks(movieDb);
                    }
                    else if (!waitingForMovieDbAssembly)
                    {
                        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                        waitingForMovieDbAssembly = true;
                        PatchLog.Waiting(logger, nameof(OriginalPoster), "MovieDb", isEnabled);
                    }
                }

                if (!tvdbHookInstalled)
                {
                    if (TryGetLoadedAssembly("Tvdb", out var tvdb))
                    {
                        TryInstallTvdbHooks(tvdb);
                    }
                    else if (!waitingForTvdbAssembly)
                    {
                        if (!waitingForMovieDbAssembly)
                        {
                            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                        }

                        waitingForTvdbAssembly = true;
                        PatchLog.Waiting(logger, nameof(OriginalPoster), "Tvdb", isEnabled);
                    }
                }
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            var assembly = args?.LoadedAssembly;
            if (assembly == null)
            {
                return;
            }

            var name = assembly.GetName().Name;
            lock (InitLock)
            {
                if (!movieDbHookInstalled &&
                    string.Equals(name, "MovieDb", StringComparison.OrdinalIgnoreCase))
                {
                    TryInstallMovieDbHooks(assembly);
                    waitingForMovieDbAssembly = false;
                }

                if (!tvdbHookInstalled &&
                    string.Equals(name, "Tvdb", StringComparison.OrdinalIgnoreCase))
                {
                    TryInstallTvdbHooks(assembly);
                    waitingForTvdbAssembly = false;
                }

                if ((!waitingForMovieDbAssembly || movieDbHookInstalled) &&
                    (!waitingForTvdbAssembly || tvdbHookInstalled))
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                }
            }
        }

        private static bool TryGetLoadedAssembly(string assemblyName, out Assembly assembly)
        {
            assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            return assembly != null;
        }

        private static void TryInstallProviderHooks()
        {
            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var version = embyProviders.GetName().Version;
                var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager", false);
                if (providerManager == null)
                {
                    PatchLog.InitFailed(logger, nameof(OriginalPoster), "ProviderManager 未找到");
                    return;
                }

                providerGetAvailableRemoteImages = PatchMethodResolver.Resolve(
                    providerManager,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "providermanager-getavailableremoteimages-sync",
                        MethodName = "GetAvailableRemoteImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(RemoteImageQuery),
                            typeof(CancellationToken)
                        },
                        // match by parameters first.
                        ReturnType = null
                    },
                    logger,
                    "OriginalPoster.ProviderManager.GetAvailableRemoteImages(sync)");

                providerGetAvailableRemoteImagesAsync = PatchMethodResolver.Resolve(
                    providerManager,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "providermanager-getavailableremoteimages-async",
                        MethodName = "GetAvailableRemoteImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(RemoteImageQuery),
                            typeof(IDirectoryService),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<IEnumerable<RemoteImageInfo>>)
                    },
                    logger,
                    "OriginalPoster.ProviderManager.GetAvailableRemoteImages(async)");

                var patched = 0;
                patched += PatchMethod(providerGetAvailableRemoteImages,
                    prefix: nameof(GetAvailableRemoteImagesPrefix),
                    postfix: SelectGetAvailableRemoteImagesPostfix(providerGetAvailableRemoteImages));
                patched += PatchMethod(providerGetAvailableRemoteImagesAsync,
                    prefix: nameof(GetAvailableRemoteImagesPrefix),
                    postfix: nameof(GetAvailableRemoteImagesAsyncPostfix));

                providerHookInstalled = patched > 0;
                if (!providerHookInstalled)
                {
                    PatchLog.InitFailed(logger, nameof(OriginalPoster), "Provider hooks 安装失败");
                }
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(OriginalPoster), ex.Message);
                logger?.Error("OriginalPoster provider hooks failed: {0}", ex);
            }
        }

        private static void TryInstallMovieDbHooks(Assembly assembly)
        {
            try
            {
                movieDbAssembly = assembly;
                var version = assembly.GetName().Version;

                var movieDbImageProviderType = assembly.GetType("MovieDb.MovieDbImageProvider", false);
                movieDbGetMovieInfo = PatchMethodResolver.Resolve(
                    movieDbImageProviderType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "moviedbimageprovider-getmovieinfo-exact",
                        MethodName = "GetMovieInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(BaseItem),
                            typeof(string),
                            typeof(IJsonSerializer),
                            typeof(CancellationToken)
                        }
                    },
                    logger,
                    "OriginalPoster.MovieDbImageProvider.GetMovieInfo");

                var movieDbSeriesProviderType = assembly.GetType("MovieDb.MovieDbSeriesProvider", false);
                movieDbEnsureSeriesInfo = PatchMethodResolver.Resolve(
                    movieDbSeriesProviderType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "moviedbseriesprovider-ensureseriesinfo-exact",
                        MethodName = "EnsureSeriesInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(string),
                            typeof(string),
                            typeof(CancellationToken)
                        }
                    },
                    logger,
                    "OriginalPoster.MovieDbSeriesProvider.EnsureSeriesInfo");

                var movieDbProviderBaseType = assembly.GetType("MovieDb.MovieDbProviderBase", false);
                var tmdbImagesType = assembly.GetType("MovieDb.TmdbImages", false);
                movieDbGetBackdrops = PatchMethodResolver.Resolve(
                    movieDbProviderBaseType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "moviedbproviderbase-getbackdrops-exact",
                        MethodName = "GetBackdrops",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = tmdbImagesType == null ? null : new[] { tmdbImagesType }
                    },
                    logger,
                    "OriginalPoster.MovieDbProviderBase.GetBackdrops");

                var completeMovieDataType = assembly.GetType("MovieDb.MovieDbProvider+CompleteMovieData", false);
                tmdbIdMovieDataTmdb = completeMovieDataType?.GetProperty("id");
                imdbIdMovieDataTmdb = completeMovieDataType?.GetProperty("imdb_id");
                originalLanguageMovieDataTmdb = completeMovieDataType?.GetProperty("original_language");

                var seriesRootObjectType = assembly.GetType("MovieDb.MovieDbSeriesProvider+SeriesRootObject", false);
                tmdbIdSeriesDataTmdb = seriesRootObjectType?.GetProperty("id");
                languagesSeriesDataTmdb = seriesRootObjectType?.GetProperty("languages");

                var tmdbImageType = assembly.GetType("MovieDb.TmdbImage", false);
                tmdbFilePath = tmdbImageType?.GetProperty("file_path");
                tmdbIso6391 = tmdbImageType?.GetProperty("iso_639_1");

                var patched = 0;
                patched += PatchMethod(movieDbGetMovieInfo, postfix: nameof(GetMovieInfoTmdbPostfix));
                patched += PatchMethod(movieDbEnsureSeriesInfo, postfix: nameof(EnsureSeriesInfoTmdbPostfix));
                patched += PatchMethod(movieDbGetBackdrops, postfix: nameof(GetBackdropsPostfix));

                movieDbHookInstalled = patched > 0;
                if (!movieDbHookInstalled)
                {
                    PatchLog.InitFailed(logger, nameof(OriginalPoster), "MovieDb hooks 安装失败");
                }
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(OriginalPoster), ex.Message);
                logger?.Error("OriginalPoster movieDb hooks failed: {0}", ex);
            }
        }

        private static void TryInstallTvdbHooks(Assembly assembly)
        {
            try
            {
                tvdbAssembly = assembly;
                var version = assembly.GetName().Version;

                var tvdbMovieProviderType = assembly.GetType("Tvdb.TvdbMovieProvider", false);
                tvdbEnsureMovieInfo = PatchMethodResolver.Resolve(
                    tvdbMovieProviderType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "tvdbmovieprovider-ensuremovieinfo-exact",
                        MethodName = "EnsureMovieInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(string),
                            typeof(IDirectoryService),
                            typeof(CancellationToken)
                        }
                    },
                    logger,
                    "OriginalPoster.TvdbMovieProvider.EnsureMovieInfo");

                var tvdbSeriesProviderType = assembly.GetType("Tvdb.TvdbSeriesProvider", false);
                tvdbEnsureSeriesInfo = PatchMethodResolver.Resolve(
                    tvdbSeriesProviderType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "tvdbseriesprovider-ensureseriesinfo-exact",
                        MethodName = "EnsureSeriesInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(string),
                            typeof(IDirectoryService),
                            typeof(CancellationToken)
                        }
                    },
                    logger,
                    "OriginalPoster.TvdbSeriesProvider.EnsureSeriesInfo");

                var movieDataType = assembly.GetType("Tvdb.MovieData", false);
                tvdbIdMovieDataTvdb = movieDataType?.GetProperty("id");
                originalLanguageMovieDataTvdb = movieDataType?.GetProperty("originalLanguage");

                var seriesDataType = assembly.GetType("Tvdb.SeriesData", false);
                tvdbIdSeriesDataTvdb = seriesDataType?.GetProperty("id");
                originalLanguageSeriesDataTvdb = seriesDataType?.GetProperty("originalLanguage");

                var patched = 0;
                patched += PatchMethod(tvdbEnsureMovieInfo, postfix: nameof(EnsureMovieInfoTvdbPostfix));
                patched += PatchMethod(tvdbEnsureSeriesInfo, postfix: nameof(EnsureSeriesInfoTvdbPostfix));

                tvdbHookInstalled = patched > 0;
                if (!tvdbHookInstalled)
                {
                    PatchLog.InitFailed(logger, nameof(OriginalPoster), "Tvdb hooks 安装失败");
                }
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(OriginalPoster), ex.Message);
                logger?.Error("OriginalPoster tvdb hooks failed: {0}", ex);
            }
        }

        private static int PatchMethod(MethodInfo method, string prefix = null, string postfix = null)
        {
            if (method == null || harmony == null)
            {
                return 0;
            }

            var prefixMethod = !string.IsNullOrWhiteSpace(prefix)
                ? new HarmonyMethod(typeof(OriginalPoster).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic))
                : null;
            var postfixMethod = !string.IsNullOrWhiteSpace(postfix)
                ? new HarmonyMethod(typeof(OriginalPoster).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic))
                : null;

            harmony.Patch(method, prefix: prefixMethod, postfix: postfixMethod);
            PatchLog.Patched(logger, nameof(OriginalPoster), method);
            return 1;
        }

        private static string SelectGetAvailableRemoteImagesPostfix(MethodInfo method)
        {
            if (method?.ReturnType == typeof(Task<IEnumerable<RemoteImageInfo>>))
            {
                return nameof(GetAvailableRemoteImagesAsyncPostfix);
            }

            return nameof(GetAvailableRemoteImagesPostfix);
        }

        private static void AddContextItem(string tmdbId, string imdbId, string tvdbId)
        {
            if (string.IsNullOrWhiteSpace(tmdbId) && string.IsNullOrWhiteSpace(imdbId) && string.IsNullOrWhiteSpace(tvdbId))
            {
                return;
            }

            var context = new ContextItem
            {
                TmdbId = tmdbId,
                ImdbId = imdbId,
                TvdbId = tvdbId
            };

            if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                CurrentItemsByTmdbId[tmdbId] = context;
            }

            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                CurrentItemsByImdbId[imdbId] = context;
            }

            if (!string.IsNullOrWhiteSpace(tvdbId))
            {
                CurrentItemsByTvdbId[tvdbId] = context;
            }

            CurrentLookupItem.Value = context;
        }

        private static void UpdateOriginalLanguage(string tmdbId, string imdbId, string tvdbId, string originalLanguage)
        {
            var language = NormalizeLanguage(originalLanguage);
            if (string.IsNullOrWhiteSpace(language))
            {
                return;
            }

            ContextItem context = null;
            if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                CurrentItemsByTmdbId.TryGetValue(tmdbId, out context);
            }

            if (context == null && !string.IsNullOrWhiteSpace(imdbId))
            {
                CurrentItemsByImdbId.TryGetValue(imdbId, out context);
            }

            if (context == null && !string.IsNullOrWhiteSpace(tvdbId))
            {
                CurrentItemsByTvdbId.TryGetValue(tvdbId, out context);
            }

            if (context == null)
            {
                context = new ContextItem
                {
                    TmdbId = tmdbId,
                    ImdbId = imdbId,
                    TvdbId = tvdbId
                };
            }

            context.OriginalLanguage = language;

            if (!string.IsNullOrWhiteSpace(context.TmdbId))
            {
                CurrentItemsByTmdbId[context.TmdbId] = context;
            }

            if (!string.IsNullOrWhiteSpace(context.ImdbId))
            {
                CurrentItemsByImdbId[context.ImdbId] = context;
            }

            if (!string.IsNullOrWhiteSpace(context.TvdbId))
            {
                CurrentItemsByTvdbId[context.TvdbId] = context;
            }
        }

        private static ContextItem GetAndKeepContextItem()
        {
            var lookup = CurrentLookupItem.Value;
            CurrentLookupItem.Value = null;
            if (lookup == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(lookup.TmdbId) &&
                CurrentItemsByTmdbId.TryGetValue(lookup.TmdbId, out var tmdbContext))
            {
                return tmdbContext;
            }

            if (!string.IsNullOrWhiteSpace(lookup.ImdbId) &&
                CurrentItemsByImdbId.TryGetValue(lookup.ImdbId, out var imdbContext))
            {
                return imdbContext;
            }

            if (!string.IsNullOrWhiteSpace(lookup.TvdbId) &&
                CurrentItemsByTvdbId.TryGetValue(lookup.TvdbId, out var tvdbContext))
            {
                return tvdbContext;
            }

            return null;
        }

        private static string GetOriginalLanguage(BaseItem item)
        {
            var context = GetAndKeepContextItem();
            return NormalizeLanguage(context?.OriginalLanguage);
        }

        [HarmonyPrefix]
        private static void GetAvailableRemoteImagesPrefix(BaseItem item, ref RemoteImageQuery query)
        {
            if (!isEnabled || item == null || query == null)
            {
                return;
            }

            query.IncludeAllLanguages = true;

            var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
            var imdbId = item.GetProviderId(MetadataProviders.Imdb);
            var tvdbId = item.GetProviderId(MetadataProviders.Tvdb);

            if ((item is Season season) && season.Series != null)
            {
                tmdbId ??= season.Series.GetProviderId(MetadataProviders.Tmdb);
                tvdbId ??= season.Series.GetProviderId(MetadataProviders.Tvdb);
            }
            else if ((item is Episode episode) && episode.Series != null)
            {
                tmdbId ??= episode.Series.GetProviderId(MetadataProviders.Tmdb);
                tvdbId ??= episode.Series.GetProviderId(MetadataProviders.Tvdb);
            }

            AddContextItem(tmdbId, imdbId, tvdbId);
        }

        [HarmonyPostfix]
        private static void GetAvailableRemoteImagesPostfix(BaseItem item, LibraryOptions libraryOptions, ref IEnumerable<RemoteImageInfo> __result)
        {
            if (!isEnabled || item == null || __result == null)
            {
                return;
            }

            var originalLanguage = GetOriginalLanguage(item);
            if (string.IsNullOrWhiteSpace(originalLanguage))
            {
                logger?.Debug("OriginalPoster 跳过原语言图片优先: item={0}, 原语言为空", GetItemLabel(item));
                return;
            }

            __result = ApplyBackdropLanguageAndOrder(__result, libraryOptions, originalLanguage);
        }

        [HarmonyPostfix]
        private static void GetAvailableRemoteImagesAsyncPostfix(
            BaseItem item,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken,
            ref Task<IEnumerable<RemoteImageInfo>> __result)
        {
            if (!isEnabled || item == null || __result == null)
            {
                return;
            }

            var originalTask = __result;
            __result = originalTask.ContinueWith(task =>
                {
                    if (!task.IsCompletedSuccessfully || task.Result == null)
                    {
                        return task.GetAwaiter().GetResult();
                    }

                    var originalLanguage = GetOriginalLanguage(item);
                    if (string.IsNullOrWhiteSpace(originalLanguage))
                    {
                        logger?.Debug("OriginalPoster 跳过原语言图片优先: item={0}, 原语言为空", GetItemLabel(item));
                        return task.Result;
                    }

                    var beforeList = task.Result.ToList();
                    var beforeFirst = beforeList.FirstOrDefault(i => i != null && i.Type == ImageType.Primary);

                    var reordered = ApplyBackdropLanguageAndOrder(task.Result, libraryOptions, originalLanguage).ToList();
                    var afterFirst = reordered.FirstOrDefault(i => i != null && i.Type == ImageType.Primary);

                    logger?.Debug(
                        "OriginalPoster 原语言图片优先完成: item={0}, 原语言={1}, 原首图语言={2}, 优先原语言={3}, total={4}",
                        GetItemLabel(item),
                        originalLanguage,
                        NormalizeLanguage(beforeFirst?.Language) ?? "<null>",
                        NormalizeLanguage(afterFirst?.Language) ?? "<null>",
                        reordered.Count);

                    return reordered;
                },
                cancellationToken,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        [HarmonyPostfix]
        private static void GetMovieInfoTmdbPostfix(Task __result)
        {
            if (!isEnabled || __result == null)
            {
                return;
            }

            __result.ContinueWith(task =>
                {
                    if (!task.IsCompletedSuccessfully)
                    {
                        return;
                    }

                    var movieData = GetTaskResult(task);
                    var tmdbId = GetPropertyValueAsString(tmdbIdMovieDataTmdb, movieData);
                    var imdbId = GetPropertyValueAsString(imdbIdMovieDataTmdb, movieData);
                    var originalLanguage = GetPropertyValueAsString(originalLanguageMovieDataTmdb, movieData);
                    UpdateOriginalLanguage(tmdbId, imdbId, null, originalLanguage);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        [HarmonyPostfix]
        private static void EnsureSeriesInfoTmdbPostfix(string tmdbId, Task __result)
        {
            if (!isEnabled || __result == null)
            {
                return;
            }

            if (WasCalledByMethod(movieDbAssembly, "FetchImages"))
            {
                WasCalledByImageProvider.Value = true;
            }

            __result.ContinueWith(task =>
                {
                    if (!task.IsCompletedSuccessfully || !WasCalledByImageProvider.Value)
                    {
                        return;
                    }

                    var seriesInfo = GetTaskResult(task);
                    var id = GetPropertyValueAsString(tmdbIdSeriesDataTmdb, seriesInfo) ?? tmdbId;
                    var originalLanguage = GetFirstLanguageFromList(seriesInfo);
                    UpdateOriginalLanguage(id, null, null, originalLanguage);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        [HarmonyPostfix]
        private static void EnsureMovieInfoTvdbPostfix(string tvdbId, IDirectoryService directoryService, CancellationToken cancellationToken, Task __result)
        {
            if (!isEnabled || __result == null)
            {
                return;
            }

            if (WasCalledByMethod(tvdbAssembly, "GetImages"))
            {
                WasCalledByImageProvider.Value = true;
            }

            __result.ContinueWith(task =>
                {
                    if (!task.IsCompletedSuccessfully || !WasCalledByImageProvider.Value)
                    {
                        return;
                    }

                    var movieData = GetTaskResult(task);
                    var id = GetPropertyValueAsString(tvdbIdMovieDataTvdb, movieData) ?? tvdbId;
                    var originalLanguage = GetPropertyValueAsString(originalLanguageMovieDataTvdb, movieData);
                    UpdateOriginalLanguage(null, null, id, originalLanguage);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        [HarmonyPostfix]
        private static void EnsureSeriesInfoTvdbPostfix(string tvdbId, IDirectoryService directoryService, CancellationToken cancellationToken, Task __result)
        {
            if (!isEnabled || __result == null)
            {
                return;
            }

            if (WasCalledByMethod(tvdbAssembly, "GetImages"))
            {
                WasCalledByImageProvider.Value = true;
            }

            __result.ContinueWith(task =>
                {
                    if (!task.IsCompletedSuccessfully || !WasCalledByImageProvider.Value)
                    {
                        return;
                    }

                    var seriesData = GetTaskResult(task);
                    var id = GetPropertyValueAsString(tvdbIdSeriesDataTvdb, seriesData) ?? tvdbId;
                    var originalLanguage = GetPropertyValueAsString(originalLanguageSeriesDataTvdb, seriesData);
                    UpdateOriginalLanguage(null, null, id, originalLanguage);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        [HarmonyPostfix]
        private static void GetBackdropsPostfix(IEnumerable __result)
        {
            if (!isEnabled || __result == null || tmdbFilePath == null || tmdbIso6391 == null)
            {
                return;
            }

            try
            {
                foreach (var item in __result.Cast<object>())
                {
                    var filePath = GetPropertyValueAsString(tmdbFilePath, item);
                    var language = GetPropertyValueAsString(tmdbIso6391, item);
                    if (!string.IsNullOrWhiteSpace(filePath) && !string.IsNullOrWhiteSpace(language))
                    {
                        BackdropByLanguage[filePath] = NormalizeLanguage(language);
                        tmdbIso6391.SetValue(item, null);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("OriginalPoster GetBackdropsPostfix failed: {0}", ex.Message);
            }
        }

        private static IEnumerable<RemoteImageInfo> ApplyBackdropLanguageAndOrder(
            IEnumerable<RemoteImageInfo> images,
            LibraryOptions libraryOptions,
            string originalLanguage)
        {
            var list = (images ?? Enumerable.Empty<RemoteImageInfo>()).ToList();
            var preferredImageLanguage = NormalizeLanguage(libraryOptions?.PreferredImageLanguage);

            foreach (var image in list.Where(i => i != null && i.Type == ImageType.Backdrop))
            {
                var path = BackdropByLanguage.Keys.FirstOrDefault(k => image.Url != null && image.Url.EndsWith(k, StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(path) && BackdropByLanguage.TryRemove(path, out var backdropLanguage))
                {
                    image.Language = backdropLanguage;
                }
            }

            return list
                .Select((image, index) => new
                {
                    Image = image,
                    Index = index,
                    Score = GetImagePriority(image, preferredImageLanguage, originalLanguage)
                })
                .OrderBy(v => v.Score)
                .ThenBy(v => v.Index)
                .Select(v => v.Image)
                .ToList();
        }

        private static int GetImagePriority(RemoteImageInfo image, string preferredImageLanguage, string originalLanguage)
        {
            var imageLanguage = NormalizeLanguage(image?.Language);

            if (!string.IsNullOrWhiteSpace(originalLanguage) &&
                string.Equals(imageLanguage, NormalizeLanguage(originalLanguage), StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(preferredImageLanguage) &&
                string.Equals(imageLanguage, preferredImageLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 2;
        }

        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            var value = language.Trim();
            var dashIndex = value.IndexOf('-');
            if (dashIndex > 0)
            {
                value = value.Substring(0, dashIndex);
            }

            return value.ToLowerInvariant();
        }

        private static bool WasCalledByMethod(Assembly assembly, string methodName)
        {
            if (assembly == null || string.IsNullOrWhiteSpace(methodName))
            {
                return false;
            }

            try
            {
                var frames = new StackTrace().GetFrames();
                if (frames == null)
                {
                    return false;
                }

                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    if (method?.DeclaringType?.Assembly == assembly &&
                        string.Equals(method.Name, methodName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static object GetTaskResult(Task task)
        {
            if (task == null)
            {
                return null;
            }

            try
            {
                return task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task);
            }
            catch
            {
                return null;
            }
        }

        private static string GetPropertyValueAsString(PropertyInfo property, object instance)
        {
            if (property == null || instance == null)
            {
                return null;
            }

            try
            {
                return property.GetValue(instance)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string GetFirstLanguageFromList(object instance)
        {
            if (instance == null || languagesSeriesDataTmdb == null)
            {
                return null;
            }

            try
            {
                if (languagesSeriesDataTmdb.GetValue(instance) is IEnumerable<string> list)
                {
                    return list.FirstOrDefault();
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string GetItemLabel(BaseItem item)
        {
            return item?.Name ?? item?.FileName ?? item?.Path ?? item?.InternalId.ToString() ?? "<null>";
        }
    }
}
