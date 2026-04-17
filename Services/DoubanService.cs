using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.External;

namespace MediaInfoKeeper.Services
{
    internal static class DoubanService
    {
        private sealed class DoubanSearchResponse
        {
            public List<DoubanSearchItem> items { get; set; }
        }

        private sealed class DoubanSearchItem
        {
            public string type_name { get; set; }

            public string layout { get; set; }

            public DoubanSearchTarget target { get; set; }
        }

        private sealed class DoubanSearchTarget
        {
            public string id { get; set; }

            public string year { get; set; }
        }

        private sealed class DoubanImdbLookupResponse
        {
            public string id { get; set; }
        }

        private sealed class DoubanCelebrityResponse
        {
            public List<DoubanCelebrity> actors { get; set; }
        }

        private sealed class DoubanCelebrity
        {
            public string name { get; set; }

            public string latin_name { get; set; }

            public string character { get; set; }
        }

        private sealed class CacheEntry<T>
        {
            public DateTimeOffset At { get; set; }

            public T Value { get; set; }
        }

        private sealed class MediaLookupContext
        {
            public string DoubanSubjectId { get; set; }

            public string DoubanSubjectType { get; set; }

            public string CacheKey { get; set; }
        }

        private static readonly Regex MixedChineseEnglishRoleRegex = new Regex(@"^(.+?[\u4E00-\u9FFF][^A-Za-z]*?)\s+[A-Za-z0-9].*$", RegexOptions.Compiled);
        private static readonly Regex DoubanSubjectIdRegex = new Regex(@"(?<!\d)(\d{5,})(?!\d)", RegexOptions.Compiled);
        private static readonly TimeSpan DoubanCelebrityCacheDuration = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan DoubanFailureCacheDuration = TimeSpan.FromMinutes(3);
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly ConcurrentDictionary<string, CacheEntry<List<DoubanCelebrity>>> DoubanCelebrityCache =
            new ConcurrentDictionary<string, CacheEntry<List<DoubanCelebrity>>>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, CacheEntry<string>> DoubanImdbSubjectCache =
            new ConcurrentDictionary<string, CacheEntry<string>>(StringComparer.OrdinalIgnoreCase);

        public static void EnhancePeopleRole(BaseItem item, List<PersonInfo> people)
        {
            if (item == null || people == null || people.Count == 0)
            {
                return;
            }

            if (Plugin.Instance?.Options?.MetaData?.EnablePersonRoleDoubanFallback != true)
            {
                return;
            }

            var doubanCelebrities = GetDoubanCelebrities(item);
            if (doubanCelebrities == null || doubanCelebrities.Count == 0)
            {
                return;
            }

            foreach (var person in people)
            {
                if (!ShouldHandlePerson(person))
                {
                    continue;
                }

                if (LanguageUtility.IsChinese(person.Role))
                {
                    continue;
                }

                var doubanCelebrity = MatchDoubanCelebrity(doubanCelebrities, person.Name);
                var chineseRole = CleanDoubanCharacter(doubanCelebrity?.character);
                if (!LanguageUtility.IsChinese(chineseRole))
                {
                    continue;
                }

                person.Role = chineseRole;
            }
        }

        private static bool ShouldHandlePerson(PersonInfo person)
        {
            return person != null &&
                   (person.Type == PersonType.Actor || person.Type == PersonType.GuestStar);
        }

        private static List<DoubanCelebrity> GetDoubanCelebrities(BaseItem item)
        {
            var context = BuildMediaLookupContext(item);
            if (context == null || string.IsNullOrWhiteSpace(context.DoubanSubjectId))
            {
                return null;
            }

            if (DoubanCelebrityCache.TryGetValue(context.CacheKey, out var cached) &&
                ConfiguredDateTime.NowOffset - cached.At < (cached.Value == null ? DoubanFailureCacheDuration : DoubanCelebrityCacheDuration))
            {
                return cached.Value;
            }

            var body = DoubanApiClient.GetJson(DoubanApiClient.BuildCelebritiesUrl(context.DoubanSubjectType, context.DoubanSubjectId));
            if (string.IsNullOrWhiteSpace(body))
            {
                DoubanCelebrityCache[context.CacheKey] = new CacheEntry<List<DoubanCelebrity>>
                {
                    At = ConfiguredDateTime.NowOffset,
                    Value = null
                };
                return null;
            }

            try
            {
                var actors = JsonSerializer.Deserialize<DoubanCelebrityResponse>(body, JsonOptions)?.actors ?? new List<DoubanCelebrity>();
                DoubanCelebrityCache[context.CacheKey] = new CacheEntry<List<DoubanCelebrity>>
                {
                    At = ConfiguredDateTime.NowOffset,
                    Value = actors
                };
                return actors;
            }
            catch (Exception)
            {
                DoubanCelebrityCache[context.CacheKey] = new CacheEntry<List<DoubanCelebrity>>
                {
                    At = ConfiguredDateTime.NowOffset,
                    Value = null
                };
                return null;
            }
        }

        private static MediaLookupContext BuildMediaLookupContext(BaseItem item)
        {
            if (item == null)
            {
                return null;
            }

            var lookupItem = item;
            string subjectType;
            if (item is Movie)
            {
                subjectType = "movie";
            }
            else if (item is Series)
            {
                subjectType = "tv";
            }
            else if (item is Season season)
            {
                lookupItem = season.Series;
                subjectType = "tv";
            }
            else if (item is Episode episode)
            {
                lookupItem = episode.Series;
                subjectType = "tv";
            }
            else
            {
                return null;
            }

            var subjectId = ResolveDoubanSubjectId(lookupItem, subjectType);
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return null;
            }

            return new MediaLookupContext
            {
                DoubanSubjectId = subjectId,
                DoubanSubjectType = subjectType,
                CacheKey = subjectType + ":" + subjectId
            };
        }

        private static string ResolveDoubanSubjectId(BaseItem item, string subjectType)
        {
            var providerId = GetProviderId(item, "Douban", "douban", "DoubanId", "doubanid");
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                return NormalizeDoubanSubjectId(providerId);
            }

            var imdbId = item.GetProviderId(MetadataProviders.Imdb);
            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                var imdbLookup = GetDoubanSubjectFromImdb(imdbId);
                if (!string.IsNullOrWhiteSpace(imdbLookup))
                {
                    PersistDoubanSubjectId(item, imdbLookup);
                    return imdbLookup;
                }
            }

            var searchResult = SearchDoubanSubject(item, subjectType);
            if (!string.IsNullOrWhiteSpace(searchResult))
            {
                PersistDoubanSubjectId(item, searchResult);
            }

            return searchResult;
        }

        private static string GetDoubanSubjectFromImdb(string imdbId)
        {
            if (DoubanImdbSubjectCache.TryGetValue(imdbId, out var cached) &&
                ConfiguredDateTime.NowOffset - cached.At < (string.IsNullOrWhiteSpace(cached.Value) ? DoubanFailureCacheDuration : DoubanCelebrityCacheDuration))
            {
                return cached.Value;
            }

            var body = DoubanApiClient.PostImdbLookup(imdbId);
            if (string.IsNullOrWhiteSpace(body))
            {
                DoubanImdbSubjectCache[imdbId] = new CacheEntry<string> { At = ConfiguredDateTime.NowOffset, Value = null };
                return null;
            }

            try
            {
                var rawSubjectId = JsonSerializer.Deserialize<DoubanImdbLookupResponse>(body, JsonOptions)?.id;
                var subjectId = NormalizeDoubanSubjectId(rawSubjectId);
                DoubanImdbSubjectCache[imdbId] = new CacheEntry<string> { At = ConfiguredDateTime.NowOffset, Value = subjectId };
                Plugin.SharedLogger?.Debug(
                    "DoubanService 豆瓣 IMDb 请求完成: imdb={0}, rawSubject={1}, subject={2}",
                    imdbId,
                    rawSubjectId ?? string.Empty,
                    subjectId ?? string.Empty);
                return subjectId;
            }
            catch (Exception)
            {
                DoubanImdbSubjectCache[imdbId] = new CacheEntry<string> { At = ConfiguredDateTime.NowOffset, Value = null };
                return null;
            }
        }

        private static string SearchDoubanSubject(BaseItem item, string subjectType)
        {
            var title = item?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var query = item.ProductionYear.HasValue ? title + " " + item.ProductionYear.Value : title;
            var body = DoubanApiClient.GetJson(DoubanApiClient.BuildSearchUrl(query));
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                var response = JsonSerializer.Deserialize<DoubanSearchResponse>(body, JsonOptions);
                foreach (var candidate in response?.items ?? Enumerable.Empty<DoubanSearchItem>())
                {
                    if (!string.Equals(candidate?.layout, "subject", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!IsMatchingTypeName(subjectType, candidate.type_name))
                    {
                        continue;
                    }

                    if (item.ProductionYear.HasValue &&
                        !string.IsNullOrWhiteSpace(candidate.target?.year) &&
                        !string.Equals(candidate.target.year, item.ProductionYear.Value.ToString(), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(candidate.target?.id))
                    {
                        return NormalizeDoubanSubjectId(candidate.target.id);
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private static bool IsMatchingTypeName(string subjectType, string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return false;
            }

            if (string.Equals(subjectType, "movie", StringComparison.OrdinalIgnoreCase))
            {
                return typeName.Contains("电影", StringComparison.Ordinal);
            }

            return typeName.Contains("剧", StringComparison.Ordinal) ||
                   typeName.Contains("电视剧", StringComparison.Ordinal) ||
                   typeName.Contains("真人秀", StringComparison.Ordinal) ||
                   typeName.Contains("综艺", StringComparison.Ordinal) ||
                   typeName.Contains("动画", StringComparison.Ordinal);
        }

        private static DoubanCelebrity MatchDoubanCelebrity(IEnumerable<DoubanCelebrity> celebrities, string currentName)
        {
            if (celebrities == null)
            {
                return null;
            }

            var normalizedCurrent = NormalizeName(currentName);
            return celebrities.FirstOrDefault(candidate =>
                       !string.IsNullOrWhiteSpace(candidate?.latin_name) &&
                       string.Equals(NormalizeName(candidate.latin_name), normalizedCurrent, StringComparison.OrdinalIgnoreCase))
                   ?? celebrities.FirstOrDefault(candidate =>
                       !string.IsNullOrWhiteSpace(candidate?.name) &&
                       string.Equals(NormalizeName(candidate.name), normalizedCurrent, StringComparison.OrdinalIgnoreCase));
        }

        private static string CleanDoubanCharacter(string character)
        {
            if (string.IsNullOrWhiteSpace(character))
            {
                return null;
            }

            var value = character.Trim();
            if (value.StartsWith("饰", StringComparison.Ordinal))
            {
                value = value.Substring(1).Trim();
            }

            if (string.Equals(value, "演员", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var mixedMatch = MixedChineseEnglishRoleRegex.Match(value);
            if (mixedMatch.Success)
            {
                value = mixedMatch.Groups[1].Value.Trim();
            }

            value = value.Replace("（演员）", string.Empty).Replace("(演员)", string.Empty).Trim();
            return LanguageUtility.IsChinese(value) ? value : null;
        }

        private static string NormalizeName(string name)
        {
            return string.IsNullOrWhiteSpace(name)
                ? null
                : name.Trim().Replace("·", string.Empty).Replace(" ", string.Empty);
        }

        private static string NormalizeDoubanSubjectId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.All(char.IsDigit))
            {
                return trimmed;
            }

            var match = DoubanSubjectIdRegex.Match(trimmed);
            return match.Success ? match.Groups[1].Value : trimmed;
        }

        private static void PersistDoubanSubjectId(BaseItem item, string subjectId)
        {
            var normalizedSubjectId = NormalizeDoubanSubjectId(subjectId);
            if (item == null || string.IsNullOrWhiteSpace(normalizedSubjectId))
            {
                return;
            }

            if (Plugin.Instance?.Options?.MetaData?.EnableDoubanLinkWriteback != true)
            {
                return;
            }

            var existing = GetProviderId(item, DoubanExternalId.StaticName, "douban", "DoubanId", "doubanid");
            if (string.Equals(NormalizeDoubanSubjectId(existing), normalizedSubjectId, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                item.SetProviderId(DoubanExternalId.StaticName, normalizedSubjectId);
                Plugin.Instance?.ItemRepository?.SaveItem(item, CancellationTokenUtility.None);
                Plugin.SharedLogger?.Info(
                    "DoubanService 豆瓣链接写入: {0} ({1}) doubanid={2}",
                    item.FileName ?? string.Empty,
                    item.ProductionYear,
                    normalizedSubjectId);
            }
            catch (Exception ex)
            {
                Plugin.SharedLogger?.Info(
                    "DoubanService 豆瓣链接写入失败: {0} ({1}) doubanid={2}, msg={3}",
                    item?.FileName ?? string.Empty,
                    item?.ProductionYear,
                    normalizedSubjectId,
                    ex.Message);
            }
        }

        private static string GetProviderId(IHasProviderIds item, params string[] keys)
        {
            if (item == null)
            {
                return null;
            }

            foreach (var key in keys)
            {
                var providerId = item.GetProviderId(key);
                if (!string.IsNullOrWhiteSpace(providerId))
                {
                    return providerId;
                }
            }

            return null;
        }

    }
}
