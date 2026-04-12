using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MediaInfoKeeper.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;

namespace MediaInfoKeeper.Provider
{
    /// <summary>
    /// 在“缺失剧集”场景下对 TMDB 返回结果补入剧集组映射。
    /// </summary>
    public static class MovieDbSeriesProvider
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly JsonSerializerOptions WriteIndentedJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private sealed class EpisodeGroupResponse
        {
            public string id { get; set; }

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
            public int id { get; set; }

            public string name { get; set; }

            public string overview { get; set; }

            public DateTime air_date { get; set; }

            public int episode_number { get; set; }

            public int season_number { get; set; }

            public int order { get; set; }
        }

        [ThreadStatic]
        private static string currentSeriesContainingFolderPath;

        public static string CurrentSeriesContainingFolderPath
        {
            get => currentSeriesContainingFolderPath;
            set => currentSeriesContainingFolderPath = value;
        }

        public static async Task<RemoteSearchResult[]> RewriteMissingEpisodesAsync(
            SeriesInfo searchInfo,
            Task<RemoteSearchResult[]> fallbackTask,
            ILogger logger,
            HashSet<(int SeasonNumber, int EpisodeNumber)> existingEpisodeNumbers)
        {
            if (fallbackTask == null)
            {
                return Array.Empty<RemoteSearchResult>();
            }

            var fallbackResults = await fallbackTask.ConfigureAwait(false) ?? Array.Empty<RemoteSearchResult>();
            var options = Plugin.Instance?.Options?.MetaData;
            if (options?.EnableMissingEpisodesEnhance != true)
            {
                return fallbackResults;
            }

            var tmdbId = searchInfo?.GetProviderId(MetadataProviders.Tmdb);
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                return fallbackResults;
            }

            string localEpisodeGroupPath = null;
            if (options.EnableLocalEpisodeGroup && !string.IsNullOrWhiteSpace(CurrentSeriesContainingFolderPath))
            {
                localEpisodeGroupPath = Path.Combine(
                    CurrentSeriesContainingFolderPath,
                    Patch.MovieDbEpisodeGroup.LocalEpisodeGroupFileName);
            }
            CurrentSeriesContainingFolderPath = null;

            var episodeGroupId = searchInfo.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName)?.Trim();
            EpisodeGroupResponse episodeGroup = null;
            if (options.EnableLocalEpisodeGroup &&
                !string.IsNullOrWhiteSpace(localEpisodeGroupPath) &&
                File.Exists(localEpisodeGroupPath))
            {
                try
                {
                    var raw = File.ReadAllText(localEpisodeGroupPath);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        episodeGroup = JsonSerializer.Deserialize<EpisodeGroupResponse>(raw, JsonOptions);
                    }
                }
                catch (Exception ex)
                {
                    logger?.Debug("缺失剧集增强读取本地剧集组失败: {0}", ex.Message);
                }
            }

            if (episodeGroup == null && !string.IsNullOrWhiteSpace(episodeGroupId))
            {
                episodeGroup = FetchOnlineEpisodeGroup(
                    tmdbId,
                    episodeGroupId,
                    searchInfo.MetadataLanguage,
                    localEpisodeGroupPath,
                    logger);
            }

            if (episodeGroup?.groups == null || episodeGroup.groups.Count == 0)
            {
                var normalizedFallbackResults = fallbackResults;
                if (existingEpisodeNumbers != null && fallbackResults.Length > 0)
                {
                    var today = new DateTimeOffset(ConfiguredDateTime.Today, ConfiguredDateTime.Offset);
                    var missingDateCount = 0;
                    normalizedFallbackResults = fallbackResults.Select(result =>
                    {
                        if (result?.ParentIndexNumber == null || result.IndexNumber == null)
                        {
                            return result;
                        }

                        if (existingEpisodeNumbers.Contains((result.ParentIndexNumber.Value, result.IndexNumber.Value)) ||
                            result.PremiereDate.HasValue)
                        {
                            return result;
                        }

                        missingDateCount++;
                        return new RemoteSearchResult
                        {
                            SearchProviderName = result.SearchProviderName,
                            ParentIndexNumber = result.ParentIndexNumber,
                            IndexNumber = result.IndexNumber,
                            IndexNumberEnd = result.IndexNumberEnd,
                            Name = result.Name,
                            OriginalTitle = result.OriginalTitle,
                            Overview = result.Overview,
                            PremiereDate = today,
                            ProductionYear = today.Year,
                            ProviderIds = result.ProviderIds
                        };
                    }).ToArray();

                    if (missingDateCount > 0)
                    {
                        logger?.Debug(
                            "缺失剧集增强补全原始结果首播日期: tmdbId={0}, count={1}",
                            tmdbId,
                            missingDateCount);
                    }
                }

                logger?.Debug("缺失剧集增强未改写: 使用原始结果 fallbackCount={0}", normalizedFallbackResults.Length);
                return normalizedFallbackResults;
            }

            var fallbackLookup = fallbackResults
                .Where(result => result?.ParentIndexNumber != null && result.IndexNumber != null)
                .GroupBy(result => (Season: result.ParentIndexNumber.Value, Episode: result.IndexNumber.Value))
                .ToDictionary(group => group.Key, group => group.First());

            var results = new List<RemoteSearchResult>();
            foreach (var group in episodeGroup.groups.OrderBy(g => g.order))
            {
                foreach (var episode in (group.episodes ?? Enumerable.Empty<GroupEpisode>()).OrderBy(e => e.order))
                {
                    var rewrittenSeasonNumber = group.order;
                    var rewrittenEpisodeNumber = episode.order + 1;
                    if (existingEpisodeNumbers != null &&
                        existingEpisodeNumbers.Contains((rewrittenSeasonNumber, rewrittenEpisodeNumber)))
                    {
                        continue;
                    }

                    fallbackLookup.TryGetValue((episode.season_number, episode.episode_number), out var mappedEpisode);
                    var providerIds = new ProviderIdDictionary();
                    if (mappedEpisode?.ProviderIds != null)
                    {
                        foreach (var pair in mappedEpisode.ProviderIds)
                        {
                            providerIds[pair.Key] = pair.Value;
                        }
                    }

                    if (episode.id > 0)
                    {
                        providerIds[MetadataProviders.Tmdb.ToString()] = episode.id.ToString();
                    }

                    var premiereDate = episode.air_date != default ? episode.air_date : mappedEpisode?.PremiereDate;
                    results.Add(new RemoteSearchResult
                    {
                        SearchProviderName = mappedEpisode?.SearchProviderName ?? "TheMovieDb",
                        ParentIndexNumber = rewrittenSeasonNumber,
                        IndexNumber = rewrittenEpisodeNumber,
                        IndexNumberEnd = mappedEpisode?.IndexNumberEnd,
                        Name = !string.IsNullOrWhiteSpace(episode.name) ? episode.name : mappedEpisode?.Name,
                        OriginalTitle = mappedEpisode?.OriginalTitle,
                        Overview = !string.IsNullOrWhiteSpace(episode.overview) ? episode.overview : mappedEpisode?.Overview,
                        PremiereDate = premiereDate,
                        ProductionYear = premiereDate?.Year,
                        ProviderIds = providerIds
                    });
                }
            }

            logger?.Debug(
                "缺失剧集增强完成: tmdbId={0}, episodeGroupId={1}, rewrittenCount={2}, fallbackCount={3}",
                tmdbId,
                string.IsNullOrWhiteSpace(episodeGroup.id) ? (string.IsNullOrWhiteSpace(episodeGroupId) ? "<empty>" : episodeGroupId) : episodeGroup.id,
                results.Count,
                fallbackResults.Length);
            return results.ToArray();
        }

        private static EpisodeGroupResponse FetchOnlineEpisodeGroup(
            string seriesTmdbId,
            string episodeGroupId,
            string language,
            string localEpisodeGroupPath,
            ILogger logger)
        {
            var url = BuildEpisodeGroupUrl(episodeGroupId, language);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            try
            {
                IHttpClient httpClient = Plugin.SharedHttpClient;
                if (httpClient == null)
                {
                    return null;
                }

                using var response = httpClient.GetResponse(new HttpRequestOptions
                {
                    Url = url,
                    TimeoutMs = 8000
                }).GetAwaiter().GetResult();
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    logger?.Debug("缺失剧集增强获取在线剧集组失败: status={0}, tmdb={1}, id={2}",
                        (int)response.StatusCode,
                        seriesTmdbId ?? string.Empty,
                        episodeGroupId ?? string.Empty);
                    return null;
                }

                using var reader = new StreamReader(response.Content);
                var raw = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                var data = JsonSerializer.Deserialize<EpisodeGroupResponse>(raw, JsonOptions);
                if (data == null)
                {
                    return null;
                }

                if (IsValidHttpUrl(episodeGroupId) && string.IsNullOrWhiteSpace(data.id))
                {
                    data.id = episodeGroupId;
                }

                logger?.Debug(
                    "缺失剧集增强命中在线剧集组: tmdbId={0}, episodeGroupId={1}, groups={2}",
                    seriesTmdbId ?? string.Empty,
                    data.id ?? episodeGroupId ?? string.Empty,
                    data.groups?.Count ?? 0);

                if (Plugin.Instance?.Options?.MetaData?.EnableLocalEpisodeGroup == true &&
                    !string.IsNullOrWhiteSpace(localEpisodeGroupPath))
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(localEpisodeGroupPath);
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        File.WriteAllText(localEpisodeGroupPath, JsonSerializer.Serialize(data, WriteIndentedJsonOptions));
                    }
                    catch (Exception ex)
                    {
                        logger?.Debug("缺失剧集增强写入本地剧集组失败: {0}", ex.Message);
                    }
                }

                return data;
            }
            catch (Exception ex)
            {
                logger?.Debug("缺失剧集增强获取在线剧集组失败: {0}", ex.Message);
                return null;
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

            var networkOptions = Plugin.Instance?.Options?.GetNetWorkOptions();
            var apiKey = networkOptions?.AlternativeTmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return null;
            }

            var baseUrl = networkOptions.AlternativeTmdbApiUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "https://api.themoviedb.org";
            }
            else if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = "https://" + baseUrl.Trim();
            }

            baseUrl = baseUrl.TrimEnd('/');
            var url = $"{baseUrl}/3/tv/episode_group/{Uri.EscapeDataString(episodeGroupId)}?api_key={Uri.EscapeDataString(apiKey)}";
            if (!string.IsNullOrWhiteSpace(language))
            {
                url += $"&language={Uri.EscapeDataString(language)}";
            }

            return Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;
        }

        private static bool IsValidHttpUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }
}
