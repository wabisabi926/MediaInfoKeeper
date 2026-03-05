using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaInfoKeeper.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// TMDB metadata fallback patch adapted from StrmAssistant ChineseMovieDb.
    /// </summary>
    public static class MovieDbTitle
    {
        private static readonly object InitLock = new object();
        private static readonly AsyncLocal<string> CurrentLookupLanguageCountryCode = new AsyncLocal<string>();

        private static readonly Regex ChineseRegex = new Regex(@"[\u4E00-\u9FFF]", RegexOptions.Compiled);
        private static readonly Regex JapaneseRegex = new Regex(@"[\u3040-\u30FF]", RegexOptions.Compiled);
        private static readonly Regex DefaultChineseEpisodeNameRegex =
            new Regex(@"^第\s*\d+\s*集$", RegexOptions.Compiled);
        private static readonly Regex DefaultJapaneseEpisodeNameRegex =
            new Regex(@"^第\s*\d+\s*話$", RegexOptions.Compiled);

        private static readonly string[] SupportedFallbackLanguages =
        {
            "zh-CN",
            "zh-SG",
            "zh-HK",
            "zh-TW",
            "ja-JP"
        };

        private static Harmony harmony;
        private static ILogger logger;

        private static bool isEnabled = true;
        private static bool waitingForMovieDbAssembly;
        private static bool patchesInstalled;
        public static bool IsReady => patchesInstalled;
        public static bool IsWaiting => waitingForMovieDbAssembly && !patchesInstalled;

        private static Assembly movieDbAssembly;
        private static Version movieDbAssemblyVersion;

        private static MethodInfo genericMovieDbInfoProcessMainInfoMovie;
        private static MethodInfo genericMovieDbInfoIsCompleteMovie;
        private static MethodInfo getTitleMovieData;

        private static MethodInfo getMovieDbMetadataLanguages;
        private static MethodInfo mapLanguageToProviderLanguage;
        private static MethodInfo getImageLanguagesParam;

        private static MethodInfo movieDbSeriesProviderIsComplete;
        private static MethodInfo movieDbSeriesProviderImportData;
        private static MethodInfo ensureSeriesInfo;
        private static MethodInfo getTitleSeriesInfo;

        private static MethodInfo movieDbSeasonProviderIsComplete;
        private static MethodInfo movieDbSeasonProviderImportData;

        private static MethodInfo movieDbEpisodeProviderIsComplete;
        private static MethodInfo movieDbEpisodeProviderImportData;

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

                if (TryGetLoadedMovieDbAssembly(out var assembly))
                {
                    TryInstallPatches(assembly);
                    return;
                }

                if (!waitingForMovieDbAssembly)
                {
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    waitingForMovieDbAssembly = true;
                    PatchLog.Waiting(logger, nameof(MovieDbTitle), "MovieDb", isEnabled);
                }
            }
        }

        public static void Configure(bool enable)
        {
            if (isEnabled == enable)
            {
                return;
            }

            isEnabled = enable;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            var loadedAssembly = args?.LoadedAssembly;
            if (loadedAssembly == null)
            {
                return;
            }

            var name = loadedAssembly.GetName().Name;
            if (!string.Equals(name, "MovieDb", StringComparison.OrdinalIgnoreCase))
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
                movieDbAssembly = assembly;
                ResolveMethods(assembly);

                harmony ??= new Harmony("mediainfokeeper.moviedb.chinesefallback");

                var patchCount = 0;
                patchCount += PatchMethod(genericMovieDbInfoProcessMainInfoMovie,
                    prefix: new HarmonyMethod(typeof(MovieDbTitle), nameof(ProcessMainInfoMoviePrefix)));
                patchCount += PatchMethod(genericMovieDbInfoIsCompleteMovie,
                    prefix: new HarmonyMethod(typeof(MovieDbTitle), nameof(IsCompletePrefix)),
                    postfix: new HarmonyMethod(typeof(MovieDbTitle), nameof(IsCompletePostfix)));

                patchCount += PatchMethod(getMovieDbMetadataLanguages,
                    postfix: new HarmonyMethod(typeof(MovieDbTitle), nameof(MetadataLanguagesPostfix)));
                patchCount += PatchMethod(getImageLanguagesParam,
                    postfix: new HarmonyMethod(typeof(MovieDbTitle), nameof(GetImageLanguagesParamPostfix)));

                patchCount += PatchMethod(movieDbSeriesProviderIsComplete,
                    prefix: new HarmonyMethod(typeof(MovieDbTitle), nameof(IsCompletePrefix)),
                    postfix: new HarmonyMethod(typeof(MovieDbTitle), nameof(IsCompletePostfix)));
                patchCount += PatchMethod(movieDbSeriesProviderImportData,
                    prefix: new HarmonyMethod(typeof(MovieDbTitle), nameof(SeriesImportDataPrefix)));
                patchCount += PatchMethod(ensureSeriesInfo,
                    postfix: new HarmonyMethod(typeof(MovieDbTitle), nameof(EnsureSeriesInfoPostfix)));

                patchCount += PatchMethod(movieDbSeasonProviderIsComplete,
                    prefix: new HarmonyMethod(typeof(MovieDbTitle), nameof(IsCompletePrefix)),
                    postfix: new HarmonyMethod(typeof(MovieDbTitle), nameof(IsCompletePostfix)));
                patchCount += PatchMethod(movieDbSeasonProviderImportData,
                    prefix: new HarmonyMethod(typeof(MovieDbTitle), nameof(SeasonImportDataPrefix)));

                patchCount += PatchMethod(movieDbEpisodeProviderIsComplete,
                    prefix: new HarmonyMethod(typeof(MovieDbTitle), nameof(IsCompletePrefix)),
                    postfix: new HarmonyMethod(typeof(MovieDbTitle), nameof(IsCompletePostfix)));
                patchCount += PatchMethod(movieDbEpisodeProviderImportData,
                    prefix: new HarmonyMethod(typeof(MovieDbTitle), nameof(EpisodeImportDataPrefix)));

                patchesInstalled = patchCount > 0;

                if (waitingForMovieDbAssembly)
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                    waitingForMovieDbAssembly = false;
                }

            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(MovieDbTitle), ex.Message);
                logger?.Error("补丁异常：模块={0}，详情={1}", nameof(MovieDbTitle), ex);
                harmony = null;
            }
        }

        private static void ResolveMethods(Assembly assembly)
        {
            movieDbAssemblyVersion = assembly.GetName().Version;
            var tmdbSettingsResult = assembly.GetType("MovieDb.TmdbSettingsResult", false);
            var genericMovieDbInfo = assembly.GetType("MovieDb.GenericMovieDbInfo`1", false);
            var genericMovieDbInfoMovie = genericMovieDbInfo?.MakeGenericType(typeof(Movie));
            if (genericMovieDbInfo != null)
            {
                genericMovieDbInfoIsCompleteMovie = FindInstanceMethod(
                    genericMovieDbInfoMovie,
                    "IsComplete",
                    new[] { typeof(Movie) },
                    typeof(bool));
            }

            var movieDbProvider = assembly.GetType("MovieDb.MovieDbProvider", false);
            var completeMovieData = movieDbProvider?.GetNestedType("CompleteMovieData", BindingFlags.NonPublic);
            var completeMovieDataParams = completeMovieData == null || tmdbSettingsResult == null
                ? null
                : new[] { typeof(MetadataResult<Movie>), tmdbSettingsResult, typeof(string), completeMovieData, typeof(bool) };
            if (completeMovieDataParams != null)
            {
                genericMovieDbInfoProcessMainInfoMovie = FindInstanceMethod(
                    genericMovieDbInfoMovie,
                    "ProcessMainInfo",
                    completeMovieDataParams);
            }
            getTitleMovieData = FindInstanceMethod(
                completeMovieData,
                "GetTitle",
                Type.EmptyTypes,
                typeof(string));

            var movieDbProviderBase = assembly.GetType("MovieDb.MovieDbProviderBase", false);
            var episodeRootObject = movieDbProviderBase?.GetNestedType("EpisodeRootObject", BindingFlags.Public | BindingFlags.NonPublic);
            getMovieDbMetadataLanguages = FindInstanceMethod(
                movieDbProviderBase,
                "GetMovieDbMetadataLanguages",
                new[] { typeof(ItemLookupInfo), typeof(string[]) },
                typeof(string[]));
            mapLanguageToProviderLanguage = FindInstanceMethod(
                movieDbProviderBase,
                "MapLanguageToProviderLanguage",
                new[] { typeof(string), typeof(string), typeof(bool), typeof(string[]) },
                typeof(string));
            getImageLanguagesParam = FindInstanceMethod(
                movieDbProviderBase,
                "GetImageLanguagesParam",
                new[] { typeof(string[]) },
                typeof(string));

            var movieDbSeriesProvider = assembly.GetType("MovieDb.MovieDbSeriesProvider", false);
            var seriesRootObject = movieDbSeriesProvider?.GetNestedType("SeriesRootObject", BindingFlags.Public | BindingFlags.NonPublic);
            movieDbSeriesProviderIsComplete = FindInstanceMethod(
                movieDbSeriesProvider,
                "IsComplete",
                new[] { typeof(Series) },
                typeof(bool));
            movieDbSeriesProviderImportData = FindInstanceMethod(
                movieDbSeriesProvider,
                "ImportData",
                seriesRootObject == null || tmdbSettingsResult == null
                    ? null
                    : new[] { typeof(MetadataResult<Series>), seriesRootObject, typeof(string), tmdbSettingsResult, typeof(bool) });
            ensureSeriesInfo = FindInstanceMethod(
                movieDbSeriesProvider,
                "EnsureSeriesInfo",
                new[] { typeof(string), typeof(string), typeof(CancellationToken) });
            getTitleSeriesInfo = FindInstanceMethod(
                seriesRootObject,
                "GetTitle",
                Type.EmptyTypes,
                typeof(string));

            var movieDbSeasonProvider = assembly.GetType("MovieDb.MovieDbSeasonProvider", false);
            var seasonRootObject = movieDbSeasonProvider?.GetNestedType("SeasonRootObject", BindingFlags.Public | BindingFlags.NonPublic);
            movieDbSeasonProviderIsComplete = FindInstanceMethod(
                movieDbSeasonProvider,
                "IsComplete",
                new[] { typeof(Season) },
                typeof(bool));
            movieDbSeasonProviderImportData = FindInstanceMethod(
                movieDbSeasonProvider,
                "ImportData",
                seasonRootObject == null
                    ? null
                    : new[] { typeof(Season), seasonRootObject, typeof(string), typeof(int), typeof(bool) });

            var movieDbEpisodeProvider = assembly.GetType("MovieDb.MovieDbEpisodeProvider", false);
            movieDbEpisodeProviderIsComplete = FindInstanceMethod(
                movieDbEpisodeProvider,
                "IsComplete",
                new[] { typeof(Episode) },
                typeof(bool));
            movieDbEpisodeProviderImportData = FindInstanceMethod(
                movieDbProviderBase,
                "ImportData",
                episodeRootObject == null || tmdbSettingsResult == null
                    ? null
                    : new[] { typeof(MetadataResult<Episode>), typeof(EpisodeInfo), episodeRootObject, tmdbSettingsResult, typeof(bool) });
        }

        private static MethodInfo FindInstanceMethod(Type type, string name, Type[] parameterTypes, Type returnType = null)
        {
            if (type == null || string.IsNullOrWhiteSpace(name) || parameterTypes == null)
            {
                return null;
            }

            return PatchMethodResolver.Resolve(
                type,
                movieDbAssemblyVersion,
                new MethodSignatureProfile
                {
                    Name = string.Format("{0}.{1}.exact", type.Name, name),
                    MethodName = name,
                    BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    IsStatic = false,
                    ParameterTypes = parameterTypes,
                    ReturnType = returnType
                },
                logger,
                string.Format("MovieDbTitle.{0}.{1}", type.Name, name));
        }

        private static int PatchMethod(MethodInfo method, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            if (method == null || harmony == null)
            {
                return 0;
            }

            harmony.Patch(method, prefix: prefix, postfix: postfix);
            PatchLog.Patched(logger, nameof(MovieDbTitle), method);
            return 1;
        }

        [HarmonyPrefix]
        private static void ProcessMainInfoMoviePrefix(
            MetadataResult<Movie> resultItem,
            object settings,
            string preferredCountryCode,
            object movieData,
            bool isFirstLanguage)
        {
            if (!isEnabled || resultItem?.Item == null || movieData == null)
            {
                return;
            }

            try
            {
                var item = resultItem.Item;
                logger?.Debug(
                    "TMDB Movie ProcessMainInfo: item={0}, firstLanguage={1}, preferredCountry={2}, currentName='{3}'",
                    item.FileName ?? item.Path ?? item.Id.ToString(),
                    isFirstLanguage,
                    preferredCountryCode ?? string.Empty,
                    item.Name ?? string.Empty);

                if (IsUpdateNeeded(item.Name))
                {
                    var title = InvokeGetTitle(getTitleMovieData, movieData);
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        item.Name = title;
                        logger?.Debug("TMDB Movie 标题更新: '{0}'", title);
                    }
                }

                var overview = GetPropertyString(movieData, "overview");
                if (IsUpdateNeeded(item.Overview) && !string.IsNullOrWhiteSpace(overview))
                {
                    item.Overview = DecodeOverview(overview);
                    logger?.Debug("TMDB Movie 简介更新: len={0}", item.Overview?.Length ?? 0);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("ProcessMainInfoMoviePrefix failed: {0}", ex.Message);
            }
        }

        [HarmonyPrefix]
        private static bool IsCompletePrefix(BaseItem item, ref bool __result, out bool __state)
        {
            __state = false;
            if (!isEnabled || item == null)
            {
                return true;
            }

            var name = item.Name ?? string.Empty;
            var overview = item.Overview ?? string.Empty;
            var isJapaneseFallback = HasMovieDbJapaneseFallback();

            if (item is Movie || item is Series || item is Season)
            {
                __state = true;
                __result = !isJapaneseFallback
                    ? IsChinese(name) && IsChinese(overview)
                    : IsChineseJapanese(name) && IsChineseJapanese(overview);
                logger?.Debug(
                    "TMDB IsComplete: type={0}, nameZh={1}, overviewZh={2}, jpFallback={3}, result={4}",
                    item.GetType().Name,
                    !isJapaneseFallback ? IsChinese(name) : IsChineseJapanese(name),
                    !isJapaneseFallback ? IsChinese(overview) : IsChineseJapanese(overview),
                    isJapaneseFallback,
                    __result);
                return false;
            }

            if (item is Episode)
            {
                __state = true;

                if (!isJapaneseFallback)
                {
                    if (IsDefaultChineseEpisodeName(name))
                    {
                        __result = false;
                    }
                    else
                    {
                        __result = IsChinese(overview);
                    }
                }
                else
                {
                    if (IsDefaultChineseEpisodeName(name) || IsDefaultJapaneseEpisodeName(name))
                    {
                        __result = false;
                    }
                    else
                    {
                        __result = IsChineseJapanese(overview);
                    }
                }

                logger?.Debug(
                    "TMDB IsComplete Episode: name='{0}', overviewLen={1}, jpFallback={2}, result={3}",
                    name,
                    overview.Length,
                    isJapaneseFallback,
                    __result);
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void IsCompletePostfix(BaseItem item, ref bool __result, bool __state)
        {
            if (!isEnabled || !__state || item == null)
            {
                return;
            }

            if (BlockMovieDbNonFallbackLanguage(item.Overview))
            {
                item.Overview = null;
                logger?.Debug("TMDB IsCompletePostfix: 清空非回退语言简介 item={0}", item.FileName ?? item.Path ?? item.Id.ToString());
            }

            if (!string.IsNullOrWhiteSpace(item.Tagline))
            {
                item.Tagline = null;
                logger?.Debug("TMDB IsCompletePostfix: 清空 Tagline item={0}", item.FileName ?? item.Path ?? item.Id.ToString());
            }
        }

        [HarmonyPrefix]
        private static void SeriesImportDataPrefix(
            MetadataResult<Series> seriesResult,
            object seriesInfo,
            string preferredCountryCode,
            object settings,
            bool isFirstLanguage)
        {
            if (!isEnabled || seriesResult?.Item == null || seriesInfo == null)
            {
                return;
            }

            try
            {
                var item = seriesResult.Item;
                logger?.Debug(
                    "TMDB Series ImportData: item={0}, firstLanguage={1}, preferredCountry={2}, currentName='{3}'",
                    item.FileName ?? item.Path ?? item.Id.ToString(),
                    isFirstLanguage,
                    preferredCountryCode ?? string.Empty,
                    item.Name ?? string.Empty);

                if (IsUpdateNeeded(item.Name))
                {
                    var title = InvokeGetTitle(getTitleSeriesInfo, seriesInfo);
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        item.Name = title;
                        logger?.Debug("TMDB Series 标题更新: '{0}'", title);
                    }
                }

                var overview = GetPropertyString(seriesInfo, "overview");
                if (IsUpdateNeeded(item.Overview) && !string.IsNullOrWhiteSpace(overview))
                {
                    item.Overview = DecodeOverview(overview);
                    logger?.Debug("TMDB Series 简介更新: len={0}", item.Overview?.Length ?? 0);
                }

                if (isFirstLanguage &&
                    string.Equals(CurrentLookupLanguageCountryCode.Value, "CN", StringComparison.OrdinalIgnoreCase))
                {
                    ReplaceCnGenreName(seriesInfo, "Sci-Fi & Fantasy", "科幻奇幻");
                    ReplaceCnGenreName(seriesInfo, "War & Politics", "战争政治");
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("SeriesImportDataPrefix failed: {0}", ex.Message);
            }
        }

        [HarmonyPostfix]
        private static void EnsureSeriesInfoPostfix(
            string tmdbId,
            string language,
            CancellationToken cancellationToken,
            Task __result)
        {
            if (!isEnabled || __result == null || WasCalledByMethod("FetchImages"))
            {
                return;
            }

            try
            {
                CurrentLookupLanguageCountryCode.Value =
                    !string.IsNullOrEmpty(language) && language.Contains("-")
                        ? language.Split('-')[1]
                        : null;
                logger?.Debug(
                    "TMDB EnsureSeriesInfo: tmdbId={0}, language={1}, country={2}",
                    tmdbId ?? string.Empty,
                    language ?? string.Empty,
                    CurrentLookupLanguageCountryCode.Value ?? string.Empty);

                var seriesInfo = GetTaskResult(__result);
                if (seriesInfo == null)
                {
                    return;
                }

                var name = GetPropertyString(seriesInfo, "name");
                if (!HasMovieDbJapaneseFallback() ? !IsChinese(name) : !IsChineseJapanese(name))
                {
                    var alternativeTitlesRoot = GetPropertyValue(seriesInfo, "alternative_titles");
                    var alternativeTitles = GetPropertyValue(alternativeTitlesRoot, "results") as IEnumerable;
                    if (alternativeTitles == null)
                    {
                        return;
                    }

                    if (TrySelectAlternativeSeriesTitle(
                            alternativeTitles,
                            language,
                            CurrentLookupLanguageCountryCode.Value,
                            out var selectedIso,
                            out var selectedTitle))
                    {
                        SetPropertyValue(seriesInfo, "name", selectedTitle);
                        logger?.Debug("TMDB EnsureSeriesInfo: 命中备选标题 iso={0}, title='{1}'", selectedIso, selectedTitle);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("EnsureSeriesInfoPostfix failed: {0}", ex.Message);
            }
        }

        private static bool TrySelectAlternativeSeriesTitle(
            IEnumerable alternativeTitles,
            string language,
            string countryCode,
            out string selectedIso,
            out string selectedTitle)
        {
            selectedIso = null;
            selectedTitle = null;
            if (alternativeTitles == null)
            {
                return false;
            }

            var entries = new List<(string Iso, string Title)>();
            foreach (var altTitle in alternativeTitles)
            {
                var iso = GetPropertyString(altTitle, "iso_3166_1");
                var title = GetPropertyString(altTitle, "title");
                if (string.IsNullOrWhiteSpace(iso) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                entries.Add((iso.Trim().ToUpperInvariant(), title));
            }

            if (entries.Count == 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                var exact = entries.FirstOrDefault(v =>
                    string.Equals(v.Iso, countryCode, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(exact.Title))
                {
                    selectedIso = exact.Iso;
                    selectedTitle = exact.Title;
                    return true;
                }
            }

            var isChineseLookup = !string.IsNullOrWhiteSpace(language) &&
                                  language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            if (!isChineseLookup)
            {
                return false;
            }

            var priority = new[] { "CN", "SG", "HK", "TW" };
            foreach (var iso in priority)
            {
                var hit = entries.FirstOrDefault(v =>
                    string.Equals(v.Iso, iso, StringComparison.OrdinalIgnoreCase) &&
                    IsChinese(v.Title));
                if (!string.IsNullOrWhiteSpace(hit.Title))
                {
                    selectedIso = hit.Iso;
                    selectedTitle = hit.Title;
                    return true;
                }
            }

            var anyChinese = entries.FirstOrDefault(v => IsChinese(v.Title));
            if (!string.IsNullOrWhiteSpace(anyChinese.Title))
            {
                selectedIso = anyChinese.Iso;
                selectedTitle = anyChinese.Title;
                return true;
            }

            return false;
        }

        [HarmonyPrefix]
        private static void SeasonImportDataPrefix(
            Season item,
            object seasonInfo,
            string name,
            int seasonNumber,
            bool isFirstLanguage)
        {
            if (!isEnabled || item == null || seasonInfo == null)
            {
                return;
            }

            try
            {
                logger?.Debug(
                    "TMDB Season ImportData: item={0}, season={1}, firstLanguage={2}, currentName='{3}'",
                    item.FileName ?? item.Path ?? item.Id.ToString(),
                    seasonNumber,
                    isFirstLanguage,
                    item.Name ?? string.Empty);
                if (IsUpdateNeeded(item.Name))
                {
                    var seasonName = GetPropertyString(seasonInfo, "name");
                    if (!string.IsNullOrWhiteSpace(seasonName))
                    {
                        item.Name = seasonName;
                        logger?.Debug("TMDB Season 标题更新: '{0}'", seasonName);
                    }
                }

                if (IsUpdateNeeded(item.Overview))
                {
                    var overview = GetPropertyString(seasonInfo, "overview");
                    if (!string.IsNullOrWhiteSpace(overview))
                    {
                        item.Overview = DecodeOverview(overview);
                        logger?.Debug("TMDB Season 简介更新: len={0}", item.Overview?.Length ?? 0);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("SeasonImportDataPrefix failed: {0}", ex.Message);
            }
        }

        [HarmonyPrefix]
        private static void EpisodeImportDataPrefix(
            MetadataResult<Episode> result,
            EpisodeInfo info,
            object response,
            object settings,
            bool isFirstLanguage)
        {
            if (!isEnabled || result?.Item == null || response == null)
            {
                return;
            }

            try
            {
                var item = result.Item;
                logger?.Debug(
                    "TMDB Episode ImportData: item={0}, firstLanguage={1}, currentName='{2}'",
                    item.FileName ?? item.Path ?? item.Id.ToString(),
                    isFirstLanguage,
                    item.Name ?? string.Empty);

                var nameValue = GetPropertyString(response, "name");
                if (IsUpdateNeeded(item.Name, nameValue) && !string.IsNullOrWhiteSpace(nameValue))
                {
                    item.Name = nameValue;
                    logger?.Info("TMDB Episode 标题更新: '{0}'", nameValue);
                }

                if (IsUpdateNeeded(item.Overview))
                {
                    var overview = GetPropertyString(response, "overview");
                    if (!string.IsNullOrWhiteSpace(overview))
                    {
                        item.Overview = DecodeOverview(overview);
                        var preview = item.Overview ?? string.Empty;
                        if (preview.Length > 20)
                        {
                            preview = preview.Substring(0, 20) + "...";
                        }
                        else
                        {
                            preview += "...";
                        }

                        logger?.Info("TMDB Episode 简介更新: '{0}'", preview);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("EpisodeImportDataPrefix failed: {0}", ex.Message);
            }
        }

        [HarmonyPostfix]
        private static void MetadataLanguagesPostfix(
            object __instance,
            ItemLookupInfo searchInfo,
            string[] providerLanguages,
            ref string[] __result)
        {
            if (!isEnabled || searchInfo == null)
            {
                return;
            }

            var metadataLanguage = searchInfo.MetadataLanguage ?? string.Empty;
            if (!metadataLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                logger?.Debug("TMDB MetadataLanguages: skip metadataLanguage={0}", metadataLanguage);
                return;
            }

            try
            {
                var before = string.Join(",", __result ?? Array.Empty<string>());
                var list = (__result ?? Array.Empty<string>()).ToList();
                var index = list.FindIndex(v =>
                    string.Equals(v, "en", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v, "en-us", StringComparison.OrdinalIgnoreCase));

                var fallbackLanguages = GetMovieDbFallbackLanguages();
                foreach (var fallbackLanguage in fallbackLanguages)
                {
                    if (list.Contains(fallbackLanguage, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var mapped = MapLanguageToProviderLanguage(__instance, fallbackLanguage, providerLanguages);
                    if (string.IsNullOrWhiteSpace(mapped))
                    {
                        logger?.Debug("TMDB MetadataLanguages: fallback={0} 映射失败", fallbackLanguage);
                        continue;
                    }

                    if (index >= 0)
                    {
                        list.Insert(index, mapped);
                        index++;
                    }
                    else
                    {
                        list.Add(mapped);
                    }
                }

                __result = list.ToArray();
                logger?.Debug(
                    "TMDB MetadataLanguages: metadataLanguage={0}, before=[{1}], after=[{2}]",
                    metadataLanguage,
                    before,
                    string.Join(",", __result));
            }
            catch (Exception ex)
            {
                logger?.Debug("MetadataLanguagesPostfix failed: {0}", ex.Message);
            }
        }

        [HarmonyPostfix]
        private static void GetImageLanguagesParamPostfix(ref string __result)
        {
            if (!isEnabled || string.IsNullOrWhiteSpace(__result))
            {
                return;
            }

            try
            {
                var before = __result;
                var list = __result
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim())
                    .Where(v => v.Length > 0)
                    .ToList();

                if (list.Any(i => i.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) &&
                    !list.Contains("zh", StringComparer.OrdinalIgnoreCase))
                {
                    var firstZh = list.FindIndex(i => i.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
                    list.Insert(firstZh + 1, "zh");
                }

                __result = string.Join(",", list);
                logger?.Debug("TMDB ImageLanguages: before=[{0}], after=[{1}]", before, __result);
            }
            catch (Exception ex)
            {
                logger?.Debug("GetImageLanguagesParamPostfix failed: {0}", ex.Message);
            }
        }

        private static string InvokeGetTitle(MethodInfo method, object instance)
        {
            if (method == null || instance == null)
            {
                return null;
            }

            try
            {
                return method.Invoke(instance, null) as string;
            }
            catch
            {
                return null;
            }
        }

        private static string DecodeOverview(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return WebUtility.HtmlDecode(value).Replace("\n\n", "\n");
        }

        private static void ReplaceCnGenreName(object seriesInfo, string from, string to)
        {
            var genres = GetPropertyValue(seriesInfo, "genres") as IEnumerable;
            if (genres == null)
            {
                return;
            }

            foreach (var genre in genres)
            {
                var name = GetPropertyString(genre, "name");
                if (string.Equals(name, from, StringComparison.OrdinalIgnoreCase))
                {
                    SetPropertyValue(genre, "name", to);
                }
            }
        }

        private static string MapLanguageToProviderLanguage(object instance, string language, string[] providerLanguages)
        {
            if (instance == null || mapLanguageToProviderLanguage == null)
            {
                return null;
            }

            try
            {
                return mapLanguageToProviderLanguage.Invoke(instance, new object[]
                {
                    language,
                    null,
                    false,
                    providerLanguages
                }) as string;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsUpdateNeeded(string currentValue, string newEpisodeName = null)
        {
            if (string.IsNullOrEmpty(currentValue))
            {
                return true;
            }

            var isEpisodeName = newEpisodeName != null;
            var isJapaneseFallback = HasMovieDbJapaneseFallback();

            if (!isEpisodeName)
            {
                return !isJapaneseFallback ? !IsChinese(currentValue) : !IsChineseJapanese(currentValue);
            }

            if (!isJapaneseFallback)
            {
                return IsDefaultChineseEpisodeName(currentValue) &&
                       IsChinese(newEpisodeName) &&
                       !IsDefaultChineseEpisodeName(newEpisodeName);
            }

            if (IsDefaultChineseEpisodeName(currentValue))
            {
                if (IsChinese(newEpisodeName) && !IsDefaultChineseEpisodeName(newEpisodeName))
                {
                    return true;
                }

                if (IsJapanese(newEpisodeName) && !IsDefaultJapaneseEpisodeName(newEpisodeName))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsChinese(string input)
        {
            return !string.IsNullOrEmpty(input) &&
                   ChineseRegex.IsMatch(input) &&
                   !JapaneseRegex.IsMatch(input.Replace("\u30FB", string.Empty));
        }

        private static bool IsJapanese(string input)
        {
            return !string.IsNullOrEmpty(input) &&
                   JapaneseRegex.IsMatch(input.Replace("\u30FB", string.Empty));
        }

        private static bool IsChineseJapanese(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return false;
            }

            var normalized = input.Replace("\u30FB", string.Empty);
            return ChineseRegex.IsMatch(normalized) || JapaneseRegex.IsMatch(normalized);
        }

        private static bool IsDefaultChineseEpisodeName(string input)
        {
            return !string.IsNullOrEmpty(input) && DefaultChineseEpisodeNameRegex.IsMatch(input);
        }

        private static bool IsDefaultJapaneseEpisodeName(string input)
        {
            return !string.IsNullOrEmpty(input) && DefaultJapaneseEpisodeNameRegex.IsMatch(input);
        }

        private static bool BlockMovieDbNonFallbackLanguage(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var options = GetTmdbOptions();
            return options.BlockNonFallbackLanguage &&
                   (!HasMovieDbJapaneseFallback() || !IsJapanese(input));
        }

        private static bool HasMovieDbJapaneseFallback()
        {
            return GetMovieDbFallbackLanguages().Any(v => string.Equals(v, "ja-jp", StringComparison.OrdinalIgnoreCase));
        }

        private static List<string> GetMovieDbFallbackLanguages()
        {
            var options = GetTmdbOptions();
            var configured = options.FallbackLanguages;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return new List<string> { "zh-sg" };
            }

            var selected = configured
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var ordered = SupportedFallbackLanguages
                .Where(v => selected.Contains(v))
                .Select(v => v.ToLowerInvariant())
                .ToList();

            if (ordered.Count == 0)
            {
                ordered.Add("zh-sg");
            }

            return ordered;
        }

        private static MetaDataOptions GetTmdbOptions()
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return new MetaDataOptions();
            }

            return plugin.MetaDataOptionsStore?.GetOptions() ?? new MetaDataOptions();
        }

        private static object GetTaskResult(Task task)
        {
            if (task == null)
            {
                return null;
            }

            var taskType = task.GetType();
            var resultProperty = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            if (resultProperty == null)
            {
                return null;
            }

            try
            {
                return resultProperty.GetValue(task);
            }
            catch
            {
                return null;
            }
        }

        private static object GetPropertyValue(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var property = instance.GetType().GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null)
            {
                return null;
            }

            try
            {
                return property.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        private static string GetPropertyString(object instance, string name)
        {
            return GetPropertyValue(instance, name) as string;
        }

        private static void SetPropertyValue(object instance, string name, string value)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var property = instance.GetType().GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || !property.CanWrite || property.PropertyType != typeof(string))
            {
                return;
            }

            try
            {
                property.SetValue(instance, value);
            }
            catch
            {
                // ignore
            }
        }

        private static bool WasCalledByMethod(string methodName)
        {
            if (movieDbAssembly == null || string.IsNullOrWhiteSpace(methodName))
            {
                return false;
            }

            try
            {
                var frames = new StackTrace().GetFrames();
                if (frames == null || frames.Length == 0)
                {
                    return false;
                }

                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    if (method == null)
                    {
                        continue;
                    }

                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (method.DeclaringType?.Assembly == movieDbAssembly)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }
    }
}
