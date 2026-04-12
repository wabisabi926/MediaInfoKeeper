using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Options;

namespace MediaInfoKeeper.Services
{
    public class DanmuService
    {
        private sealed class QueuedDanmuItem
        {
            public long InternalId { get; set; }

            public bool OverwriteExisting { get; set; }

            public TaskCompletionSource<bool> CompletionSource { get; set; }
        }

        private static readonly TimeSpan QueueIntervalDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan TooManyRequestsDelay = TimeSpan.FromMinutes(1);
        private const string TooManyRequestsMessage = "TooManyRequests";

        private readonly ILogger logger;
        private readonly IHttpClient httpClient;
        private readonly ConcurrentQueue<QueuedDanmuItem> itemAddedQueue = new ConcurrentQueue<QueuedDanmuItem>();
        private readonly SemaphoreSlim queueSignal = new SemaphoreSlim(0);
        private int queueWorkerStarted;

        public DanmuService(ILogManager logManager, IHttpClient httpClient)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.httpClient = httpClient;
        }

        public bool IsEnabled =>
            Plugin.Instance?.Options?.MetaData?.EnableDanmuApi == true &&
            !string.IsNullOrWhiteSpace(Plugin.Instance?.Options?.MetaData?.DanmuApiBaseUrl);

        public bool IsSupportedItem(BaseItem item)
        {
            return item is Episode || item is Movie;
        }

        public Task<bool> QueueDownloadAsync(long internalId, bool overwriteExisting, CancellationToken cancellationToken)
        {
            var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => completionSource.TrySetCanceled(cancellationToken));
            }

            var item = Plugin.LibraryManager?.GetItemById(internalId);
            if (!overwriteExisting && ShouldSkipAutoDownload(item))
            {
                this.logger.Info($"弹幕下载: 跳过 {item?.FileName} 文件已存在");
                completionSource.TrySetResult(false);
                return completionSource.Task;
            }

            Enqueue(internalId, overwriteExisting, completionSource);
            return completionSource.Task;
        }

        private void Enqueue(long internalId, bool overwriteExisting, TaskCompletionSource<bool> completionSource)
        {
            itemAddedQueue.Enqueue(new QueuedDanmuItem
            {
                InternalId = internalId,
                OverwriteExisting = overwriteExisting,
                CompletionSource = completionSource
            });
            queueSignal.Release();

            if (Interlocked.CompareExchange(ref queueWorkerStarted, 1, 0) == 0)
            {
                _ = Task.Run(ProcessItemAddedQueueAsync);
            }
        }

        private async Task ProcessItemAddedQueueAsync()
        {
            while (true)
            {
                try
                {
                    await queueSignal.WaitAsync().ConfigureAwait(false);
                    if (!itemAddedQueue.TryDequeue(out var queuedItem) || queuedItem == null)
                    {
                        continue;
                    }

                    try
                    {
                        var result = await ProcessQueuedItemAsync(queuedItem).ConfigureAwait(false);
                        queuedItem.CompletionSource?.TrySetResult(result);
                    }
                    catch (Exception ex) when (string.Equals(ex.Message, TooManyRequestsMessage, StringComparison.Ordinal))
                    {
                        queuedItem.CompletionSource?.TrySetResult(false);
                        this.logger.Info($"弹幕下载: 失败 {queuedItem.InternalId} {ex.Message}，队列休息 60 秒");
                        await Task.Delay(TooManyRequestsDelay).ConfigureAwait(false);
                    }
                    await Task.Delay(QueueIntervalDelay).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.Info($"弹幕下载: 失败 queue {ex.Message}");
                    this.logger.Debug(ex.StackTrace);
                }
            }
        }

        private async Task<bool> ProcessQueuedItemAsync(QueuedDanmuItem queuedItem)
        {
            if (queuedItem == null)
            {
                return false;
            }

            var internalId = queuedItem.InternalId;
            var currentItem = Plugin.LibraryManager?.GetItemById(internalId);
            if (currentItem == null || Plugin.Instance == null || Plugin.Instance.Options.MainPage?.PlugginEnabled != true || !IsSupportedItem(currentItem))
            {
                return false;
            }

            if (Plugin.LibraryService?.IsItemInScope(currentItem) != true)
            {
                return false;
            }

            if (!IsEnabled)
            {
                return false;
            }

            return await TryDownloadDanmuXmlAsync(currentItem, CancellationToken.None, queuedItem.OverwriteExisting).ConfigureAwait(false);
        }

        public bool ShouldSkipAutoDownload(BaseItem item)
        {
            if (item == null)
            {
                return true;
            }

            if (item is not Episode && item is not Movie)
            {
                return true;
            }

            var networkFirst = string.Equals(
                Plugin.Instance?.Options?.MetaData?.DanmuFetchMode,
                MetaDataOptions.DanmuFetchModeOption.NetworkFirst.ToString(),
                StringComparison.Ordinal);
            if (networkFirst)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.ContainingFolderPath) || string.IsNullOrWhiteSpace(item.FileNameWithoutExtension))
            {
                return false;
            }

            var targetPath = Path.Combine(item.ContainingFolderPath, item.FileNameWithoutExtension + ".xml");
            return File.Exists(targetPath);
        }

        public string GetDanmuXmlPath(BaseItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ContainingFolderPath) || string.IsNullOrWhiteSpace(item.FileNameWithoutExtension))
            {
                return null;
            }

            return Path.Combine(item.ContainingFolderPath, item.FileNameWithoutExtension + ".xml");
        }

        public async Task<bool> TryDownloadDanmuXmlAsync(BaseItem item, CancellationToken cancellationToken, bool overwriteExisting = false)
        {
            if (!IsEnabled || item == null)
            {
                return false;
            }

            if (item is not Episode && item is not Movie)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.ContainingFolderPath) || string.IsNullOrWhiteSpace(item.FileNameWithoutExtension))
            {
                this.logger.Info($"弹幕下载: 跳过 {item.FileName} 路径信息不完整");
                return false;
            }

            var networkFirst = string.Equals(
                Plugin.Instance?.Options?.MetaData?.DanmuFetchMode,
                MetaDataOptions.DanmuFetchModeOption.NetworkFirst.ToString(),
                StringComparison.Ordinal);
            var overwrite = overwriteExisting || networkFirst;
            var targetPath = GetDanmuXmlPath(item);
            if (!overwrite && File.Exists(targetPath))
            {
                this.logger.Info($"弹幕下载: 跳过 {item.FileName} 文件已存在");
                return false;
            }

            var xmlBytes = await FetchDanmuXmlBytesAsync(item, cancellationToken).ConfigureAwait(false);
            if (xmlBytes == null || xmlBytes.Length == 0)
            {
                this.logger.Info($"弹幕下载: 跳过 {item.FileName} 未获取到内容");
                return false;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(targetPath, xmlBytes, cancellationToken).ConfigureAwait(false);
            this.logger.Info($"弹幕下载: 成功 {item.FileName}");
            return true;
        }

        public async Task<byte[]> FetchDanmuXmlBytesAsync(BaseItem item, CancellationToken cancellationToken)
        {
            if (!IsEnabled || item == null)
            {
                return null;
            }

            if (item is not Episode && item is not Movie)
            {
                return null;
            }

            if (!TryBuildSearchRequest(item, out var animeTitle, out var episodeNumber))
            {
                this.logger.Info($"弹幕下载: 跳过 {item.FileName} 无法解析标题或集数");
                return null;
            }

            var baseUrl = Plugin.Instance?.Options?.MetaData?.DanmuApiBaseUrl?.Trim();
            var episodeId = await SearchEpisodeIdAsync(baseUrl, animeTitle, episodeNumber, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(episodeId))
            {
                this.logger.Info($"弹幕下载: 跳过 {item.FileName} 未匹配到条目");
                return null;
            }

            var xmlBytes = await DownloadXmlAsync(baseUrl, episodeId, cancellationToken).ConfigureAwait(false);
            if (xmlBytes == null || xmlBytes.Length == 0)
            {
                this.logger.Info($"弹幕下载: 跳过 {item.FileName} 未获取到内容 episodeId={episodeId}");
                return null;
            }

            return xmlBytes;
        }

        private async Task<string> SearchEpisodeIdAsync(string baseUrl, string animeTitle, int episodeNumber, CancellationToken cancellationToken)
        {
            var requestUrl = BuildApiUrl(
                baseUrl,
                $"search/episodes?anime={Uri.EscapeDataString(animeTitle)}&episode={episodeNumber}");

            var requestOptions = new HttpRequestOptions
            {
                Url = requestUrl,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                UserAgent = "MediaInfoKeeper",
                EnableDefaultUserAgent = false
            };

            using var response = await httpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);
            var body = await ReadResponseBodyAsync(response).ConfigureAwait(false);

            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                if ((int)response.StatusCode == 429)
                {
                    throw new InvalidOperationException(TooManyRequestsMessage);
                }

                this.logger.Info($"弹幕下载: 失败 {animeTitle} status={(int)response.StatusCode} url={requestUrl} body={body}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("animes", out var animesElement) ||
                    animesElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var animeElement in animesElement.EnumerateArray())
                {
                    if (!animeElement.TryGetProperty("episodes", out var episodesElement) ||
                        episodesElement.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var episodeElement in episodesElement.EnumerateArray())
                    {
                        if (!episodeElement.TryGetProperty("episodeId", out var episodeIdElement))
                        {
                            continue;
                        }

                        var episodeId = episodeIdElement.ValueKind == JsonValueKind.Number
                            ? episodeIdElement.GetInt64().ToString()
                            : episodeIdElement.GetString();
                        if (!string.IsNullOrWhiteSpace(episodeId))
                        {
                            return episodeId;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.Info($"弹幕下载: 失败 {animeTitle} 结果解析异常 url={requestUrl}");
                this.logger.Debug(ex.Message);
            }

            return null;
        }

        private async Task<byte[]> DownloadXmlAsync(string baseUrl, string episodeId, CancellationToken cancellationToken)
        {
            var requestUrl = BuildApiUrl(baseUrl, $"comment/{Uri.EscapeDataString(episodeId)}?format=xml");
            var requestOptions = new HttpRequestOptions
            {
                Url = requestUrl,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/xml,text/xml;q=0.9,*/*;q=0.8",
                UserAgent = "MediaInfoKeeper",
                EnableDefaultUserAgent = false
            };

            using var response = await httpClient.GetResponse(requestOptions).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                if ((int)response.StatusCode == 429)
                {
                    throw new InvalidOperationException(TooManyRequestsMessage);
                }

                var body = await ReadStreamToStringAsync(response.Content).ConfigureAwait(false);
                this.logger.Info($"弹幕下载: 失败 {episodeId} status={(int)response.StatusCode} url={requestUrl} body={body}");
                return null;
            }

            using var memoryStream = new MemoryStream();
            await response.Content.CopyToAsync(memoryStream, 81920, cancellationToken).ConfigureAwait(false);
            return memoryStream.ToArray();
        }

        private static bool TryBuildSearchRequest(BaseItem item, out string animeTitle, out int episodeNumber)
        {
            animeTitle = null;
            episodeNumber = 1;

            if (item is Episode episode)
            {
                animeTitle = ResolveEpisodeTitle(episode);
                if (!episode.IndexNumber.HasValue || episode.IndexNumber.Value <= 0)
                {
                    return false;
                }

                episodeNumber = episode.IndexNumber.Value;
                return !string.IsNullOrWhiteSpace(animeTitle);
            }

            if (item is Movie movie)
            {
                animeTitle = movie.Name?.Trim();
                return !string.IsNullOrWhiteSpace(animeTitle);
            }

            return false;
        }

        private static string ResolveEpisodeTitle(Episode item)
        {
            if (!string.IsNullOrWhiteSpace(item.SeriesName))
            {
                return item.SeriesName.Trim();
            }

            var series = item.Series;
            if (!string.IsNullOrWhiteSpace(series?.Name))
            {
                return series.Name.Trim();
            }

            return item.Name?.Trim();
        }

        private static string BuildApiUrl(string baseUrl, string relativePath)
        {
            var normalizedBaseUrl = (baseUrl ?? string.Empty).Trim();
            if (!normalizedBaseUrl.EndsWith("/", StringComparison.Ordinal))
            {
                normalizedBaseUrl += "/";
            }

            var combined = normalizedBaseUrl + "api/v2/" + relativePath.TrimStart('/');
            return Regex.Replace(combined, "(?<!:)/{2,}", "/");
        }

        private static async Task<string> ReadResponseBodyAsync(HttpResponseInfo response)
        {
            return await ReadStreamToStringAsync(response?.Content).ConfigureAwait(false);
        }

        private static async Task<string> ReadStreamToStringAsync(Stream stream)
        {
            if (stream == null)
            {
                return string.Empty;
            }

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
    }
}
