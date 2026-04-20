using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaInfoKeeper.Services;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Services;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 对远端 http(s) strm 视频在原始静态取流阶段按条件改为 302，让客户端直接拉取。
    /// </summary>
    internal static class StrmVideoDirectRedirect
    {
        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;
        private static MethodInfo processRequestMethod;
        private static MethodInfo getStateMethod;
        private static MethodInfo disposeStateMethod;
        private static Type streamRequestType;
        private static Type streamStateType;
        private static Type videoStreamRequestType;
        private static readonly ConcurrentDictionary<string, RedirectUrlCacheEntry> RedirectUrlCache =
            new ConcurrentDictionary<string, RedirectUrlCacheEntry>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> RedirectPrefetchJobs =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTimeOffset> RedirectPrefetchSessions =
            new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        private static readonly object RedirectUrlCacheTrimLock = new object();
        private const int RedirectUrlCacheCapacity = 128;
        private const int RedirectPrefetchSessionCapacity = 16;
        private static readonly TimeSpan RedirectPrefetchSessionRetention = TimeSpan.FromMinutes(10);
        private static bool followRedirect302 = true;
        private static int redirectUrlCacheDurationSeconds = 3600;
        private static int redirectUrlCacheReuseLimit = 5;
        private static int redirectUrlPrecacheCount = 1;

        public static bool IsReady => harmony != null
            && processRequestMethod != null
            && getStateMethod != null
            && disposeStateMethod != null
            && (!isEnabled || isPatched);

        public static void Initialize(
            ILogger pluginLogger,
            bool enabled,
            bool follow302,
            int cacheDurationSeconds,
            int reuseLimit,
            int precacheCount)
        {
            if (harmony != null)
            {
                Configure(enabled, follow302, cacheDurationSeconds, reuseLimit, precacheCount);
                return;
            }

            logger = pluginLogger;
            isEnabled = enabled;
            ApplySettings(follow302, cacheDurationSeconds, reuseLimit, precacheCount);

            try
            {
                var mediaEncoding = Assembly.Load("Emby.Server.MediaEncoding");
                var mediaEncodingVersion = mediaEncoding?.GetName().Version;
                var baseProgressiveStreamingServiceType =
                    mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.Progressive.BaseProgressiveStreamingService", false);
                var baseStreamingServiceType =
                    mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.BaseStreamingService", false);
                streamRequestType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.StreamRequest", false);
                streamStateType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.StreamState", false);
                videoStreamRequestType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.VideoStreamRequest", false);

                if (baseProgressiveStreamingServiceType == null ||
                    baseStreamingServiceType == null ||
                    streamRequestType == null ||
                    streamStateType == null ||
                    videoStreamRequestType == null)
                {
                    PatchLog.InitFailed(logger, nameof(StrmVideoDirectRedirect), "未找到播放流相关类型");
                    return;
                }

                processRequestMethod = PatchMethodResolver.Resolve(
                    baseProgressiveStreamingServiceType,
                    mediaEncodingVersion,
                    new MethodSignatureProfile
                    {
                        Name = "baseprogressivestreamingservice-processrequest-exact",
                        MethodName = "ProcessRequest",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            streamRequestType,
                            typeof(bool)
                        },
                        ReturnType = typeof(Task<object>)
                    },
                    logger,
                    "StrmVideoDirectRedirect.BaseProgressiveStreamingService.ProcessRequest");

                getStateMethod = PatchMethodResolver.Resolve(
                    baseStreamingServiceType,
                    mediaEncodingVersion,
                    new MethodSignatureProfile
                    {
                        Name = "basestreamingservice-getstate-exact",
                        MethodName = "GetState",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            streamRequestType,
                            typeof(bool),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<>).MakeGenericType(streamStateType)
                    },
                    logger,
                    "StrmVideoDirectRedirect.BaseStreamingService.GetState");

                disposeStateMethod = streamStateType.GetMethod(
                    "Dispose",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(bool),
                        typeof(bool)
                    },
                    null);

                if (processRequestMethod == null || getStateMethod == null || disposeStateMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(StrmVideoDirectRedirect), "未命中 ProcessRequest/GetState/StreamState.Dispose");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.strm-direct-redirect");
                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(StrmVideoDirectRedirect), ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                processRequestMethod = null;
                getStateMethod = null;
                disposeStateMethod = null;
                streamRequestType = null;
                streamStateType = null;
                videoStreamRequestType = null;
                isPatched = false;
            }
        }

        public static void Configure(bool enabled, bool follow302, int cacheDurationSeconds, int reuseLimit, int precacheCount)
        {
            isEnabled = enabled;
            ApplySettings(follow302, cacheDurationSeconds, reuseLimit, precacheCount);
            if (harmony == null)
            {
                return;
            }

            if (isEnabled)
            {
                Patch();
            }
            else
            {
                Unpatch();
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || processRequestMethod == null)
            {
                return;
            }

            harmony.Patch(
                processRequestMethod,
                prefix: new HarmonyMethod(typeof(StrmVideoDirectRedirect), nameof(ProcessRequestPrefix)));

            PatchLog.Patched(logger, nameof(StrmVideoDirectRedirect), processRequestMethod);
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || processRequestMethod == null)
            {
                return;
            }

            harmony.Unpatch(processRequestMethod, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        private static bool ProcessRequestPrefix(
            object __instance,
            object __0,
            ref Task<object> __result)
        {
            var itemId = GetPropertyValue<string>(__0, "Id");

            if (!isEnabled || __instance == null || __0 == null || videoStreamRequestType == null)
            {
                return true;
            }

            if (!videoStreamRequestType.IsInstanceOfType(__0))
            {
                return true;
            }

            if (!ResolveStrmPath(__0, out var strmPath))
            {
                return true;
            }

            try
            {
                NormalizeOriginalRequest(__0);
                if (!GetPropertyValue<bool>(__0, "Static"))
                {
                    return true;
                }

                var state = GetState(__instance, __0);
                if (!CanRedirect(state))
                {
                    DisposeState(state);
                    return true;
                }

                var resultFactory = GetPropertyValue<IHttpResultFactory>(__instance, "ResultFactory");
                var requestContext = GetPropertyValue<IRequest>(__instance, "Request");
                var mediaSource = GetPropertyValue<MediaSourceInfo>(state, "MediaSource");
                if (resultFactory == null || mediaSource == null)
                {
                    DisposeState(state);
                    return true;
                }

                var originalUrl = mediaSource.Path;
                var cacheKey = BuildRedirectCacheKey(itemId, requestContext?.UserAgent);
                var playSessionId = GetPlaySessionId(requestContext);
                var redirectUrl = ResolveRedirectUrl(cacheKey, playSessionId, originalUrl, requestContext?.UserAgent);
                var decodedRedirectUrl = redirectUrl;
                if (!string.IsNullOrWhiteSpace(decodedRedirectUrl))
                {
                    try
                    {
                        decodedRedirectUrl = Uri.UnescapeDataString(decodedRedirectUrl);
                    }
                    catch
                    {
                    }
                }

                __result = Task.FromResult(resultFactory.GetRedirectResult(redirectUrl));
                logger?.Info(
                    "StrmVideoDirectRedirect : itemId={0}, cacheKey={1}, finalUrl={2}",
                    itemId,
                    cacheKey,
                    decodedRedirectUrl);
                QueueUpcomingEpisodeRedirectPrefetch(itemId, requestContext?.UserAgent, playSessionId);
                DisposeState(state);
                return false;
            }
            catch (Exception ex)
            {
                logger?.Warn(
                    "StrmVideoDirectRedirect 预判失败，回退 Emby 中转: itemId={0}, error={1}",
                    itemId,
                    ex.Message);
                return true;
            }
        }

        /// <summary>调用 Emby 原始流服务获取当前请求对应的 StreamState。</summary>
        private static object GetState(object service, object request)
        {
            var requestContext = GetPropertyValue<IRequest>(service, "Request");
            if (requestContext == null)
            {
                throw new InvalidOperationException("当前请求上下文为空");
            }

            var task = getStateMethod?.Invoke(service, new[] { request, (object)true, requestContext.CancellationToken }) as Task;
            if (task == null)
            {
                throw new InvalidOperationException("GetState 返回为空");
            }

            task.GetAwaiter().GetResult();
            var state = task.GetType()
                .GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(task);
            if (state == null)
            {
                throw new InvalidOperationException("GetState 未返回有效 StreamState");
            }

            return state;
        }

        /// <summary>从播放请求中解析出当前条目的 .strm 路径。</summary>
        private static bool ResolveStrmPath(object request, out string strmPath)
        {
            strmPath = null;

            var itemId = GetPropertyValue<string>(request, "Id");
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return false;
            }

            var item = Plugin.LibraryManager?.GetItemById(itemId);
            if (item == null)
            {
                return false;
            }

            strmPath = item.Path ?? item.FileName;
            if (!LibraryService.IsFileShortcut(strmPath))
            {
                return false;
            }

            return true;
        }

        /// <summary>把 original 静态取流请求规范化，补齐 Static/Container 以便命中直连分支。</summary>
        private static void NormalizeOriginalRequest(object request)
        {
            var streamFileName = GetPropertyValue<string>(request, "StreamFileName");
            if (string.IsNullOrEmpty(streamFileName))
            {
                return;
            }

            if (string.Equals(Path.GetFileNameWithoutExtension(streamFileName), "original", StringComparison.OrdinalIgnoreCase))
            {
                SetPropertyValue(request, "Static", true);
            }

            var container = GetPropertyValue<string>(request, "Container");
            if (!string.IsNullOrEmpty(container))
            {
                return;
            }

            var extension = Path.GetExtension(streamFileName);
            if (string.IsNullOrEmpty(extension))
            {
                return;
            }

            container = extension.TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(container))
            {
                container = null;
            }

            SetPropertyValue(request, "Container", container);
        }

        /// <summary>判断当前 StreamState 是否满足直接返回 302 直链的条件。</summary>
        private static bool CanRedirect(object state)
        {
            var mediaSource = GetPropertyValue<MediaSourceInfo>(state, "MediaSource");
            if (state == null ||
                !GetPropertyValue<bool>(state, "IsVideoRequest") ||
                GetPropertyValue<object>(state, "LiveStream") != null ||
                mediaSource == null)
            {
                return false;
            }

            if (!mediaSource.IsRemote ||
                mediaSource.Protocol != MediaProtocol.Http ||
                !mediaSource.SupportsDirectPlay ||
                mediaSource.IsInfiniteStream ||
                mediaSource.RequiresOpening ||
                mediaSource.RequiresClosing)
            {
                return false;
            }

            if (mediaSource.RequiredHttpHeaders != null && mediaSource.RequiredHttpHeaders.Count > 0)
            {
                return false;
            }

            if (!Uri.TryCreate(mediaSource.Path, UriKind.Absolute, out var uri) ||
                (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return true;
        }

        /// <summary>释放 GetState 创建的 StreamState，避免占用 Emby 内部资源。</summary>
        private static void DisposeState(object state)
        {
            disposeStateMethod?.Invoke(state, new object[] { true, true });
        }

        /// <summary>应用运行时配置，并在必要时清理缓存与预加载去重状态。</summary>
        private static void ApplySettings(bool follow302, int cacheDurationSeconds, int reuseLimit, int precacheCount)
        {
            followRedirect302 = follow302;
            redirectUrlCacheDurationSeconds = cacheDurationSeconds;
            redirectUrlCacheReuseLimit = reuseLimit;
            redirectUrlPrecacheCount = precacheCount;

            if (!followRedirect302 || redirectUrlCacheDurationSeconds == 0)
            {
                RedirectUrlCache.Clear();
                RedirectPrefetchSessions.Clear();
                return;
            }

            TrimRedirectUrlCacheIfNeeded();
            TrimPrefetchSessionsIfNeeded();
        }

        /// <summary>解析用于 302 返回的直链地址；开启跟踪时优先命中缓存，否则主动探测最终地址。</summary>
        private static string ResolveRedirectUrl(string cacheKey, string playSessionId, string url, string userAgent)
        {
            var normalizedUrl = NormalizeRedirectUrl(url);
            if (!followRedirect302)
            {
                return normalizedUrl;
            }

            if (GetCachedRedirectUrl(cacheKey, playSessionId, out var cachedRedirectUrl))
            {
                return cachedRedirectUrl;
            }

            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null || string.IsNullOrWhiteSpace(normalizedUrl))
            {
                return normalizedUrl;
            }

            string resolvedUrl = null;
            foreach (var method in new[] { "GET", "HEAD" })
            {
                try
                {
                    using var response = httpClient.SendAsync(
                        new HttpRequestOptions
                        {
                            Url = normalizedUrl,
                            UserAgent = userAgent ?? string.Empty,
                            TimeoutMs = 3000,
                            BufferContent = false,
                            LogErrors = false,
                            LogRequest = false,
                            LogResponse = false,
                            EnableHttpCompression = false,
                            EnableKeepAlive = false,
                            EnableDefaultUserAgent = false,
                            ThrowOnErrorResponse = false
                        },
                        method).GetAwaiter().GetResult();

                    if (!string.IsNullOrWhiteSpace(response?.ResponseUrl))
                    {
                        resolvedUrl = NormalizeRedirectUrl(response.ResponseUrl);
                        break;
                    }
                }
                catch
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(resolvedUrl))
            {
                CacheRedirectUrl(cacheKey, resolvedUrl);
                return resolvedUrl;
            }

            CacheRedirectUrl(cacheKey, normalizedUrl);
            return normalizedUrl;
        }

        /// <summary>尝试从内存直链缓存中复用已解析地址，并更新访问状态供 LRU 淘汰使用。</summary>
        private static bool GetCachedRedirectUrl(string cacheKey, string playSessionId, out string redirectUrl)
        {
            redirectUrl = null;
            if (redirectUrlCacheDurationSeconds == 0)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return false;
            }

            if (!RedirectUrlCache.TryGetValue(cacheKey, out var entry))
            {
                return false;
            }

            if (!IsExpired(entry.CreatedAt) &&
                !string.IsNullOrWhiteSpace(entry.RedirectUrl) &&
                entry.Reuse(redirectUrlCacheReuseLimit, playSessionId, out redirectUrl))
            {
                return true;
            }

            RedirectUrlCache.TryRemove(cacheKey, out _);
            return false;
        }

        /// <summary>写入直链缓存，并按容量上限执行 LRU 裁剪。</summary>
        private static void CacheRedirectUrl(string cacheKey, string redirectUrl)
        {
            if (redirectUrlCacheDurationSeconds == 0 ||
                string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(redirectUrl))
            {
                return;
            }

            RedirectUrlCache[cacheKey] = new RedirectUrlCacheEntry(DateTimeOffset.UtcNow, redirectUrl);
            TrimRedirectUrlCacheIfNeeded();
        }

        /// <summary>判断缓存项是否已超过配置的生存时间。</summary>
        private static bool IsExpired(DateTimeOffset createdAt)
        {
            return redirectUrlCacheDurationSeconds > 0 &&
                DateTimeOffset.UtcNow - createdAt > TimeSpan.FromSeconds(redirectUrlCacheDurationSeconds);
        }

        /// <summary>按最近最少使用原则裁剪直链缓存，只保留最近访问过的热点项。</summary>
        private static void TrimRedirectUrlCacheIfNeeded()
        {
            if (RedirectUrlCache.Count <= RedirectUrlCacheCapacity)
            {
                return;
            }

            lock (RedirectUrlCacheTrimLock)
            {
                while (RedirectUrlCache.Count > RedirectUrlCacheCapacity)
                {
                    string leastRecentlyUsedKey = null;
                    RedirectUrlCacheEntry leastRecentlyUsedEntry = null;

                    foreach (var pair in RedirectUrlCache)
                    {
                        if (leastRecentlyUsedEntry == null || pair.Value.LastAccessedAt < leastRecentlyUsedEntry.LastAccessedAt)
                        {
                            leastRecentlyUsedKey = pair.Key;
                            leastRecentlyUsedEntry = pair.Value;
                        }
                    }

                    if (leastRecentlyUsedKey == null)
                    {
                        break;
                    }

                    RedirectUrlCache.TryRemove(leastRecentlyUsedKey, out _);
                }
            }
        }

        /// <summary>基于条目与客户端 UA 构建直链缓存键，避免不同客户端误复用。</summary>
        private static string BuildRedirectCacheKey(string itemId, string userAgent)
        {
            return string.Concat(
                itemId?.Trim() ?? string.Empty,
                "|",
                userAgent?.Trim() ?? string.Empty);
        }

        /// <summary>从原始请求 URL 中提取 PlaySessionId，用于直链复用与预加载去重。</summary>
        private static string GetPlaySessionId(IRequest requestContext)
        {
            var rawUrl = requestContext?.RawUrl;
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                return null;
            }

            var playSessionIdPrefix = "PlaySessionId=";
            var playSessionIdIndex = rawUrl.IndexOf(playSessionIdPrefix, StringComparison.OrdinalIgnoreCase);
            if (playSessionIdIndex < 0)
            {
                return null;
            }

            var valueStartIndex = playSessionIdIndex + playSessionIdPrefix.Length;
            if (valueStartIndex >= rawUrl.Length)
            {
                return null;
            }

            var valueEndIndex = rawUrl.IndexOf('&', valueStartIndex);
            var encodedValue = valueEndIndex >= 0
                ? rawUrl.Substring(valueStartIndex, valueEndIndex - valueStartIndex)
                : rawUrl.Substring(valueStartIndex);

            if (string.IsNullOrWhiteSpace(encodedValue))
            {
                return null;
            }

            return Uri.UnescapeDataString(encodedValue).Trim();
        }

        /// <summary>规范化 URL 与查询串编码，减少等价地址造成的缓存碎片。</summary>
        private static string NormalizeRedirectUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            var questionMarkIndex = url.IndexOf('?');
            if (questionMarkIndex < 0)
            {
                return Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri)
                    ? absoluteUri.AbsoluteUri
                    : url;
            }

            var baseUrl = url.Substring(0, questionMarkIndex);
            var rawQuery = questionMarkIndex + 1 < url.Length
                ? url.Substring(questionMarkIndex + 1)
                : string.Empty;

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                return url;
            }

            var builder = new UriBuilder(baseUri)
            {
                Query = string.Join("&", Array.ConvertAll(
                    rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries),
                    pair =>
                    {
                        var separatorIndex = pair.IndexOf('=');
                        if (separatorIndex < 0)
                        {
                            return Uri.EscapeDataString(Uri.UnescapeDataString(pair));
                        }

                        var key = pair.Substring(0, separatorIndex);
                        var value = separatorIndex + 1 < pair.Length
                            ? pair.Substring(separatorIndex + 1)
                            : string.Empty;
                        return Uri.EscapeDataString(Uri.UnescapeDataString(key)) +
                               "=" +
                               Uri.EscapeDataString(Uri.UnescapeDataString(value));
                    }))
            };

            return builder.Uri.AbsoluteUri;
        }

        /// <summary>当前集命中 302 后，后台预热后续几集的直链解析结果。</summary>
        private static void QueueUpcomingEpisodeRedirectPrefetch(string currentItemId, string userAgent, string playSessionId)
        {
            if (!followRedirect302 || redirectUrlPrecacheCount <= 0 || string.IsNullOrWhiteSpace(currentItemId))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(playSessionId))
            {
                return;
            }

            var currentEpisode = Plugin.LibraryManager?.GetItemById(currentItemId) as Episode;
            var nextEpisodeIds = Plugin.LibraryService?.NextEpisodesId(currentEpisode, redirectUrlPrecacheCount);
            if (nextEpisodeIds == null || nextEpisodeIds.Count == 0)
            {
                return;
            }

            var normalizedPlaySessionId = playSessionId.Trim();
            if (!MarkPrefetchSession(normalizedPlaySessionId))
            {
                return;
            }

            var jobKey = string.Concat(
                currentItemId.Trim(),
                "|",
                normalizedPlaySessionId,
                "|",
                userAgent?.Trim() ?? string.Empty);
            if (!RedirectPrefetchJobs.TryAdd(jobKey, 0))
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var nextEpisodeId in nextEpisodeIds)
                    {
                        var nextEpisode = Plugin.LibraryManager?.GetItemById(nextEpisodeId) as Episode;
                        if (!ReadEpisodeOriginalUrl(nextEpisode, out var originalUrl))
                        {
                            continue;
                        }

                        var nextItemId = nextEpisode.GetClientId();
                        var cacheKey = BuildRedirectCacheKey(nextItemId, userAgent);
                        var redirectUrl = ResolveRedirectUrl(cacheKey, normalizedPlaySessionId, originalUrl, userAgent);
                        logger?.Info(
                            "StrmVideoDirectRedirect 预缓存: itemId={0}, episode={1}, finalUrl={2}",
                            nextItemId,
                            nextEpisode.FileName ?? nextEpisode.Name,
                            redirectUrl);
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn("StrmVideoDirectRedirect 预缓存失败: itemId={0}, error={1}", currentItemId, ex.Message);
                }

                RedirectPrefetchJobs.TryRemove(jobKey, out _);
            });
        }

        /// <summary>登记已执行过预加载的播放会话，避免同一会话重复触发。</summary>
        private static bool MarkPrefetchSession(string playSessionId)
        {
            TrimPrefetchSessionsIfNeeded();

            var now = DateTimeOffset.UtcNow;
            if (RedirectPrefetchSessions.TryGetValue(playSessionId, out var markedAt) &&
                now - markedAt <= RedirectPrefetchSessionRetention)
            {
                return false;
            }

            RedirectPrefetchSessions[playSessionId] = now;
            return true;
        }

        /// <summary>按过期时间与 FIFO 顺序裁剪会话去重缓存，避免状态无限增长。</summary>
        private static void TrimPrefetchSessionsIfNeeded()
        {
            if (RedirectPrefetchSessions.Count == 0)
            {
                return;
            }

            var expireBefore = DateTimeOffset.UtcNow - RedirectPrefetchSessionRetention;
            foreach (var pair in RedirectPrefetchSessions)
            {
                if (pair.Value < expireBefore)
                {
                    RedirectPrefetchSessions.TryRemove(pair.Key, out _);
                }
            }

            if (RedirectPrefetchSessions.Count <= RedirectPrefetchSessionCapacity)
            {
                return;
            }

            foreach (var pair in RedirectPrefetchSessions.OrderBy(pair => pair.Value).Take(RedirectPrefetchSessions.Count - RedirectPrefetchSessionCapacity))
            {
                RedirectPrefetchSessions.TryRemove(pair.Key, out _);
            }
        }

        /// <summary>读取剧集 .strm 的首个有效 http(s) 行，作为预加载时的原始地址。</summary>
        private static bool ReadEpisodeOriginalUrl(Episode episode, out string originalUrl)
        {
            originalUrl = null;
            var strmPath = episode?.Path;
            if (!LibraryService.IsFileShortcut(strmPath) || !File.Exists(strmPath))
            {
                return false;
            }

            try
            {
                var line = File.ReadLines(strmPath)
                    .Select(l => l?.Trim())
                    .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#", StringComparison.Ordinal));
                if (string.IsNullOrWhiteSpace(line))
                {
                    return false;
                }

                if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) ||
                    (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                originalUrl = NormalizeRedirectUrl(uri.AbsoluteUri);
                return !string.IsNullOrWhiteSpace(originalUrl);
            }
            catch
            {
                return false;
            }
        }

        private static T GetPropertyValue<T>(object instance, string propertyName)
        {
            var value = instance?.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance);

            if (value == null)
            {
                return default(T);
            }

            return value is T typedValue ? typedValue : default(T);
        }

        private static void SetPropertyValue(object instance, string propertyName, object value)
        {
            instance?.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.SetValue(instance, value);
        }

        private sealed class RedirectUrlCacheEntry
        {
            private int reuseCount;
            private string lastPlaySessionId;
            private long lastAccessedTicks;

            public RedirectUrlCacheEntry(DateTimeOffset createdAt, string redirectUrl)
            {
                CreatedAt = createdAt;
                RedirectUrl = redirectUrl;
                lastAccessedTicks = createdAt.UtcTicks;
            }

            public DateTimeOffset CreatedAt { get; }

            public string RedirectUrl { get; }

            public DateTimeOffset LastAccessedAt => new DateTimeOffset(Interlocked.Read(ref lastAccessedTicks), TimeSpan.Zero);

            public bool Reuse(int reuseLimit, string playSessionId, out string redirectUrl)
            {
                redirectUrl = null;
                if (reuseLimit <= 0)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(playSessionId) &&
                    string.Equals(lastPlaySessionId, playSessionId, StringComparison.Ordinal))
                {
                    Touch();
                    redirectUrl = RedirectUrl;
                    return true;
                }

                var currentReuseCount = Interlocked.Increment(ref reuseCount);
                if (currentReuseCount > reuseLimit)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(playSessionId))
                {
                    lastPlaySessionId = playSessionId;
                }

                Touch();
                redirectUrl = RedirectUrl;
                return true;
            }

            private void Touch()
            {
                Interlocked.Exchange(ref lastAccessedTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
        }

    }
}
