using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// TVDB metadata fallback patch adapted from StrmAssistant ChineseTvdb.
    /// </summary>
    public static class TvdbTitle
    {
        private static readonly object InitLock = new object();
        private static readonly ThreadLocal<bool?> ConsiderJapanese = new ThreadLocal<bool?>();

        private static readonly Regex ChineseRegex = new Regex(@"[\u4E00-\u9FFF]", RegexOptions.Compiled);
        private static readonly Regex JapaneseRegex = new Regex(@"[\u3040-\u30FF]", RegexOptions.Compiled);

        private static readonly string[] SupportedTvdbFallbackLanguages =
        {
            "zho",
            "zhtw",
            "yue",
            "jpn"
        };

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled = true;
        private static bool waitingForTvdbAssembly;
        private static bool patchesInstalled;
        public static bool IsReady => patchesInstalled;
        public static bool IsWaiting => waitingForTvdbAssembly && !patchesInstalled;
        private static Assembly tvdbAssembly;

        private static MethodInfo convertToTvdbLanguages;
        private static MethodInfo getTranslation;
        private static MethodInfo addMovieInfo;
        private static MethodInfo addSeriesInfo;
        private static MethodInfo getTvdbSeason;
        private static MethodInfo findEpisode;
        private static MethodInfo getEpisodeData;

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

                if (TryGetLoadedTvdbAssembly(out var assembly))
                {
                    TryInstallPatches(assembly);
                    return;
                }

                if (!waitingForTvdbAssembly)
                {
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                    waitingForTvdbAssembly = true;
                    PatchLog.Waiting(logger, nameof(TvdbTitle), "Tvdb", isEnabled);
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
            if (!string.Equals(name, "Tvdb", StringComparison.OrdinalIgnoreCase))
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

        private static bool TryGetLoadedTvdbAssembly(out Assembly assembly)
        {
            assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "Tvdb", StringComparison.OrdinalIgnoreCase));
            return assembly != null;
        }

        private static void TryInstallPatches(Assembly assembly)
        {
            try
            {
                tvdbAssembly = assembly;
                ResolveMethods(assembly);
                harmony ??= new Harmony("mediainfokeeper.tvdb.chinesefallback");

                var patchCount = 0;
                patchCount += PatchMethod(
                    convertToTvdbLanguages,
                    postfix: new HarmonyMethod(typeof(TvdbTitle), nameof(ConvertToTvdbLanguagesPostfix)));
                patchCount += PatchMethod(
                    getTranslation,
                    prefix: new HarmonyMethod(typeof(TvdbTitle), nameof(GetTranslationPrefix)),
                    postfix: new HarmonyMethod(typeof(TvdbTitle), nameof(GetTranslationPostfix)));
                patchCount += PatchMethod(
                    addMovieInfo,
                    postfix: new HarmonyMethod(typeof(TvdbTitle), nameof(AddInfoPostfix)));
                patchCount += PatchMethod(
                    addSeriesInfo,
                    postfix: new HarmonyMethod(typeof(TvdbTitle), nameof(AddInfoPostfix)));
                patchCount += PatchMethod(
                    getTvdbSeason,
                    postfix: new HarmonyMethod(typeof(TvdbTitle), nameof(GetTvdbSeasonPostfix)));
                patchCount += PatchMethod(
                    findEpisode,
                    postfix: new HarmonyMethod(typeof(TvdbTitle), nameof(FindEpisodePostfix)));
                patchCount += PatchMethod(
                    getEpisodeData,
                    postfix: new HarmonyMethod(typeof(TvdbTitle), nameof(GetEpisodeDataPostfix)));

                patchesInstalled = patchCount > 0;

                if (waitingForTvdbAssembly)
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                    waitingForTvdbAssembly = false;
                }

            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(TvdbTitle), ex.Message);
                logger?.Error("补丁异常：模块={0}，详情={1}", nameof(TvdbTitle), ex);
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
            PatchLog.Patched(logger, nameof(TvdbTitle), method);
            return 1;
        }

        private static void ResolveMethods(Assembly assembly)
        {
            var version = assembly.GetName().Version;
            var translationField = assembly.GetType("Tvdb.TranslationField", false);
            var nameTranslation = assembly.GetType("Tvdb.NameTranslation", false);
            var movieData = assembly.GetType("Tvdb.MovieData", false);
            var seriesData = assembly.GetType("Tvdb.SeriesData", false);
            var episodesData = assembly.GetType("Tvdb.EpisodesData", false);
            var nameTranslationList = nameTranslation == null ? null : typeof(List<>).MakeGenericType(nameTranslation);
            if (translationField == null || nameTranslationList == null || movieData == null || seriesData == null ||
                episodesData == null)
            {
                PatchLog.InitFailed(logger, nameof(TvdbTitle), "Tvdb 关键类型缺失");
                return;
            }

            var entryPoint = assembly.GetType("Tvdb.EntryPoint", false);
            convertToTvdbLanguages = PatchMethodResolver.Resolve(
                entryPoint,
                version,
                new MethodSignatureProfile
                {
                    Name = "entrypoint-converttotvdblanguages-exact",
                    MethodName = "ConvertToTvdbLanguages",
                    BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                    ParameterTypes = new[] { typeof(ItemLookupInfo) }
                },
                logger,
                "TvdbTitle.ConvertToTvdbLanguages");

            var translations = assembly.GetType("Tvdb.Translations", false);
            getTranslation = PatchMethodResolver.Resolve(
                translations,
                version,
                new MethodSignatureProfile
                {
                    Name = "translations-gettranslation-exact",
                    MethodName = "GetTranslation",
                    BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                    ParameterTypes = new[] { nameTranslationList, typeof(string[]), translationField, typeof(bool) }
                },
                    logger,
                "TvdbTitle.GetTranslation");

            var tvdbMovieProvider = assembly.GetType("Tvdb.TvdbMovieProvider", false);
            addMovieInfo = PatchMethodResolver.Resolve(
                tvdbMovieProvider,
                version,
                new MethodSignatureProfile
                {
                    Name = "tvdbmovieprovider-addmovieinfo-exact",
                    MethodName = "AddMovieInfo",
                    BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                    ParameterTypes = new[] { typeof(MetadataResult<Movie>), movieData, typeof(string[]), typeof(string) }
                },
                    logger,
                "TvdbTitle.AddMovieInfo");

            var tvdbSeriesProvider = assembly.GetType("Tvdb.TvdbSeriesProvider", false);
            addSeriesInfo = PatchMethodResolver.Resolve(
                tvdbSeriesProvider,
                version,
                new MethodSignatureProfile
                {
                    Name = "tvdbseriesprovider-addseriesinfo-exact",
                    MethodName = "AddSeriesInfo",
                    BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                    ParameterTypes = new[] { typeof(MetadataResult<Series>), seriesData, typeof(string[]), typeof(string) }
                },
                    logger,
                "TvdbTitle.AddSeriesInfo");

            var tvdbSeasonProvider = assembly.GetType("Tvdb.TvdbSeasonProvider", false);
            getTvdbSeason = PatchMethodResolver.Resolve(
                tvdbSeasonProvider,
                version,
                new MethodSignatureProfile
                {
                    Name = "tvdbseasonprovider-gettvdbseason-exact",
                    MethodName = "GetTvdbSeason",
                    BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    ParameterTypes = new[] { typeof(SeasonInfo), typeof(IDirectoryService), typeof(CancellationToken) }
                },
                    logger,
                "TvdbTitle.GetTvdbSeason");

            var tvdbEpisodeProvider = assembly.GetType("Tvdb.TvdbEpisodeProvider", false);
            if (tvdbEpisodeProvider != null)
            {
                findEpisode = PatchMethodResolver.Resolve(
                    tvdbEpisodeProvider,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "tvdbepisodeprovider-findepisode-exact",
                        MethodName = "FindEpisode",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        ParameterTypes = new[] { episodesData, typeof(EpisodeInfo), typeof(int?) }
                    },
                    logger,
                    "TvdbTitle.FindEpisode");

                getEpisodeData = PatchMethodResolver.Resolve(
                    tvdbEpisodeProvider,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "tvdbepisodeprovider-getepisodedata-exact",
                        MethodName = "GetEpisodeData",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(EpisodeInfo), typeof(bool), typeof(IDirectoryService), typeof(CancellationToken) }
                    },
                    logger,
                    "TvdbTitle.GetEpisodeData");
            }
        }

        [HarmonyPostfix]
        private static void ConvertToTvdbLanguagesPostfix(ItemLookupInfo lookupInfo, ref string[] __result)
        {
            if (!isEnabled || lookupInfo == null)
            {
                return;
            }

            var metadataLanguage = lookupInfo.MetadataLanguage ?? string.Empty;
            if (!metadataLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var list = (__result ?? Array.Empty<string>()).ToList();
                var index = list.FindIndex(v => string.Equals(v, "eng", StringComparison.OrdinalIgnoreCase));

                var fallback = GetTvdbFallbackLanguages()
                    .Where(l => (ConsiderJapanese.Value ?? true) || !string.Equals(l, "jpn", StringComparison.OrdinalIgnoreCase));

                foreach (var language in fallback)
                {
                    if (list.Contains(language, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (index >= 0)
                    {
                        list.Insert(index, language);
                        index++;
                    }
                    else
                    {
                        list.Add(language);
                    }
                }

                __result = list.ToArray();
            }
            catch (Exception ex)
            {
                logger?.Debug("ConvertToTvdbLanguagesPostfix failed: {0}", ex.Message);
            }
        }

        [HarmonyPrefix]
        private static void GetTranslationPrefix(object[] __args)
        {
            if (!isEnabled || __args == null || __args.Length < 4)
            {
                return;
            }

            try
            {
                var translations = __args[0] as IList;
                var tvdbLanguages = __args[1] as string[] ?? Array.Empty<string>();
                var field = ToInt32(__args[2]);

                if (translations != null && translations.Count > 0)
                {
                    if (field == 0)
                    {
                        RemoveAliases(translations);
                    }

                    if (HasTvdbJapaneseFallback())
                    {
                        var considerJapanese = HasPrimaryJapanese(translations);
                        tvdbLanguages = tvdbLanguages
                            .Where(l => considerJapanese || !string.Equals(l, "jpn", StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    }

                    if (field == 0)
                    {
                        SortTvdbLanguagesByChinesePriority(translations, tvdbLanguages);
                    }

                    SortTranslationsByLanguageOrder(translations, tvdbLanguages);
                }

                __args[1] = tvdbLanguages;
            }
            catch (Exception ex)
            {
                logger?.Debug("GetTranslationPrefix failed: {0}", ex.Message);
            }
        }

        [HarmonyPostfix]
        private static void GetTranslationPostfix(object[] __args, ref object __result)
        {
            if (!isEnabled || __result == null || __args == null || __args.Length < 4)
            {
                return;
            }

            try
            {
                var defaultToFirst = ToBool(__args[3]);
                if (defaultToFirst)
                {
                    return;
                }

                var field = ToInt32(__args[2]);
                var name = GetPropertyString(__result, "name");

                if (field == 0)
                {
                    if (BlockTvdbNonFallbackLanguage(name))
                    {
                        SetPropertyValue(__result, "name", null);
                    }

                    return;
                }

                if (field != 1)
                {
                    return;
                }

                var overview = GetPropertyString(__result, "overview");
                if (BlockTvdbNonFallbackLanguage(overview))
                {
                    overview = null;
                    SetPropertyValue(__result, "overview", null);
                }

                if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(overview))
                {
                    SetPropertyValue(__result, "name", overview);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("GetTranslationPostfix failed: {0}", ex.Message);
            }
        }

        [HarmonyPostfix]
        private static void AddInfoPostfix(object[] __args)
        {
            if (!isEnabled || __args == null)
            {
                return;
            }

            try
            {
                foreach (var arg in __args)
                {
                    var item = TryGetMetadataResultItem(arg);
                    if (item == null)
                    {
                        continue;
                    }

                    if (BlockTvdbNonFallbackLanguage(item.Overview))
                    {
                        item.Overview = null;
                    }

                    break;
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("AddInfoPostfix failed: {0}", ex.Message);
            }
        }

        [HarmonyPostfix]
        private static void GetTvdbSeasonPostfix(object[] __args, Task __result)
        {
            if (!isEnabled || __result == null)
            {
                return;
            }

            try
            {
                var seasonInfo = __args?.OfType<SeasonInfo>().FirstOrDefault();
                var tvdbSeason = GetTaskResult(__result);
                if (tvdbSeason == null)
                {
                    return;
                }

                var name = GetPropertyString(tvdbSeason, "name");
                if (seasonInfo?.IndexNumber.HasValue == true &&
                    (string.IsNullOrEmpty(name) || BlockTvdbNonFallbackLanguage(name)))
                {
                    SetPropertyValue(tvdbSeason, "name", $"第 {seasonInfo.IndexNumber.Value} 季");
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("GetTvdbSeasonPostfix failed: {0}", ex.Message);
            }
        }

        [HarmonyPostfix]
        private static void FindEpisodePostfix(object[] __args, ref object __result)
        {
            if (!isEnabled || __result == null)
            {
                return;
            }

            try
            {
                var name = GetPropertyString(__result, "name");
                var overview = GetPropertyString(__result, "overview");

                var considerJapanese = HasTvdbJapaneseFallback() && (IsJapanese(name) || IsJapanese(overview));
                ConsiderJapanese.Value = considerJapanese;

                if (!considerJapanese)
                {
                    if (!IsChinese(name))
                    {
                        SetPropertyValue(__result, "name", null);
                    }

                    if (!IsChinese(overview))
                    {
                        SetPropertyValue(__result, "overview", null);
                    }

                    return;
                }

                if (!IsChineseJapanese(name))
                {
                    SetPropertyValue(__result, "name", null);
                }

                if (!IsChineseJapanese(overview))
                {
                    SetPropertyValue(__result, "overview", null);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("FindEpisodePostfix failed: {0}", ex.Message);
            }
        }

        [HarmonyPostfix]
        private static void GetEpisodeDataPostfix(object[] __args, Task __result)
        {
            if (!isEnabled || __result == null)
            {
                return;
            }

            try
            {
                var episodeInfo = __args?.OfType<EpisodeInfo>().FirstOrDefault();
                var taskResult = GetTaskResult(__result);
                if (taskResult == null)
                {
                    return;
                }

                var tvdbEpisode = GetPropertyValue(taskResult, "Item1") ?? taskResult;
                if (tvdbEpisode == null)
                {
                    return;
                }

                var name = GetPropertyString(tvdbEpisode, "name");
                var overview = GetPropertyString(tvdbEpisode, "overview");

                if (episodeInfo?.IndexNumber.HasValue == true &&
                    (string.IsNullOrEmpty(name) || BlockTvdbNonFallbackLanguage(name)))
                {
                    SetPropertyValue(tvdbEpisode, "name", $"第 {episodeInfo.IndexNumber.Value} 集");
                }

                if (BlockTvdbNonFallbackLanguage(overview))
                {
                    SetPropertyValue(tvdbEpisode, "overview", null);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("GetEpisodeDataPostfix failed: {0}", ex.Message);
            }
        }

        private static void RemoveAliases(IList translations)
        {
            for (var i = translations.Count - 1; i >= 0; i--)
            {
                var translation = translations[i];
                if (translation == null)
                {
                    continue;
                }

                if (GetPropertyBool(translation, "isAlias"))
                {
                    translations.RemoveAt(i);
                }
            }
        }

        private static bool HasPrimaryJapanese(IList translations)
        {
            foreach (var translation in translations.Cast<object>())
            {
                var language = GetPropertyString(translation, "language");
                if (!string.Equals(language, "jpn", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (GetPropertyBool(translation, "IsPrimary"))
                {
                    return true;
                }
            }

            return false;
        }

        private static void SortTvdbLanguagesByChinesePriority(IList translations, string[] tvdbLanguages)
        {
            if (tvdbLanguages == null || tvdbLanguages.Length == 0)
            {
                return;
            }

            var cnLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "zho",
                "zhtw",
                "yue"
            };

            Array.Sort(tvdbLanguages, (lang1, lang2) =>
            {
                if (lang1 == null && lang2 == null)
                {
                    return 0;
                }

                if (lang1 == null)
                {
                    return 1;
                }

                if (lang2 == null)
                {
                    return -1;
                }

                var t1 = FindTranslationByLanguage(translations, lang1);
                var t2 = FindTranslationByLanguage(translations, lang2);

                var name1 = GetPropertyString(t1, "name");
                var name2 = GetPropertyString(t2, "name");

                var cn1 = cnLanguages.Contains(lang1);
                var cn2 = cnLanguages.Contains(lang2);

                if (cn1 && cn2)
                {
                    var c1 = IsChinese(name1);
                    var c2 = IsChinese(name2);
                    if (c1 && !c2)
                    {
                        return -1;
                    }

                    if (!c1 && c2)
                    {
                        return 1;
                    }

                    return 0;
                }

                if (cn1)
                {
                    return -1;
                }

                if (cn2)
                {
                    return 1;
                }

                return 0;
            });
        }

        private static object FindTranslationByLanguage(IList translations, string language)
        {
            foreach (var translation in translations.Cast<object>())
            {
                var current = GetPropertyString(translation, "language");
                if (string.Equals(current, language, StringComparison.OrdinalIgnoreCase))
                {
                    return translation;
                }
            }

            return null;
        }

        private static void SortTranslationsByLanguageOrder(IList translations, string[] tvdbLanguages)
        {
            if (translations == null || translations.Count == 0)
            {
                return;
            }

            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (tvdbLanguages != null)
            {
                for (var i = 0; i < tvdbLanguages.Length; i++)
                {
                    var language = tvdbLanguages[i];
                    if (!string.IsNullOrWhiteSpace(language) && !order.ContainsKey(language))
                    {
                        order[language] = i;
                    }
                }
            }

            var sorted = translations.Cast<object>()
                .OrderBy(t =>
                {
                    var language = GetPropertyString(t, "language") ?? string.Empty;
                    return order.TryGetValue(language, out var idx) ? idx : int.MaxValue;
                })
                .ToList();

            translations.Clear();
            foreach (var item in sorted)
            {
                translations.Add(item);
            }
        }

        private static BaseItem TryGetMetadataResultItem(object metadataResult)
        {
            var item = GetPropertyValue(metadataResult, "Item");
            return item as BaseItem;
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

        private static List<string> GetTvdbFallbackLanguages()
        {
            var options = GetTmdbOptions();
            var configured = options.TvdbFallbackLanguages;

            if (string.IsNullOrWhiteSpace(configured))
            {
                return new List<string> { "zhtw", "yue" };
            }

            var selected = configured
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim())
                .Where(v => v.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var ordered = SupportedTvdbFallbackLanguages
                .Where(v => selected.Contains(v))
                .ToList();

            if (ordered.Count == 0)
            {
                ordered.Add("zhtw");
                ordered.Add("yue");
            }

            return ordered;
        }

        private static bool HasTvdbJapaneseFallback()
        {
            return GetTvdbFallbackLanguages().Any(v => string.Equals(v, "jpn", StringComparison.OrdinalIgnoreCase));
        }

        private static bool BlockTvdbNonFallbackLanguage(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var options = GetTmdbOptions();
            return options.BlockNonFallbackLanguage &&
                   (!HasTvdbJapaneseFallback() || !IsJapanese(input));
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

        private static int ToInt32(object value)
        {
            if (value == null)
            {
                return 0;
            }

            if (value is int i)
            {
                return i;
            }

            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        private static bool ToBool(object value)
        {
            if (value is bool b)
            {
                return b;
            }

            return value != null &&
                   bool.TryParse(value.ToString(), out var parsed) &&
                   parsed;
        }

        private static object GetTaskResult(Task task)
        {
            if (task == null)
            {
                return null;
            }

            var taskType = task.GetType();
            if (!taskType.IsGenericType || taskType.GetGenericTypeDefinition() != typeof(Task<>))
            {
                return null;
            }

            var resultProperty = taskType.GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            return resultProperty?.GetValue(task);
        }

        private static object GetPropertyValue(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var property = instance.GetType().GetProperty(name, flags);
            if (property != null)
            {
                try
                {
                    return property.GetValue(instance);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static string GetPropertyString(object instance, string name)
        {
            return GetPropertyValue(instance, name) as string;
        }

        private static bool GetPropertyBool(object instance, string name)
        {
            var value = GetPropertyValue(instance, name);
            if (value is bool b)
            {
                return b;
            }

            return value != null && bool.TryParse(value.ToString(), out var parsed) && parsed;
        }

        private static void SetPropertyValue(object instance, string name, string value)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var property = instance.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);

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
    }
}
