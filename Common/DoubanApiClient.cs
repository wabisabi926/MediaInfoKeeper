using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Net;

namespace MediaInfoKeeper.Common
{
    internal static class DoubanApiClient
    {
        private static readonly TimeSpan HttpCacheDuration = TimeSpan.FromHours(6);

        public static string BuildSearchUrl(string query)
        {
            var url = "https://frodo.douban.com/api/v2/search/weixin";
            var ts = DateTime.UtcNow.ToString("yyyyMMdd");
            var signedUrl = url + "?q=" + Uri.EscapeDataString(query);
            var sig = BuildSignature(signedUrl, ts, "GET");
            return url + "?q=" + Uri.EscapeDataString(query) + "&start=0&count=10&apiKey=0dad551ec0f84ed02907ff5c42e8ec70&os_rom=android&_ts=" + Uri.EscapeDataString(ts) + "&_sig=" + Uri.EscapeDataString(sig);
        }

        public static string BuildCelebritiesUrl(string subjectType, string subjectId)
        {
            var endpoint = string.Equals(subjectType, "movie", StringComparison.OrdinalIgnoreCase)
                ? "https://frodo.douban.com/api/v2/movie/" + Uri.EscapeDataString(subjectId) + "/celebrities"
                : "https://frodo.douban.com/api/v2/tv/" + Uri.EscapeDataString(subjectId) + "/celebrities";
            var ts = DateTime.UtcNow.ToString("yyyyMMdd");
            var sig = BuildSignature(endpoint, ts, "GET");
            return endpoint + "?apiKey=0dad551ec0f84ed02907ff5c42e8ec70&os_rom=android&_ts=" + Uri.EscapeDataString(ts) + "&_sig=" + Uri.EscapeDataString(sig);
        }

        public static string PostImdbLookup(string imdbId)
        {
            return Send(
                "https://api.douban.com/v2/movie/imdb/" + Uri.EscapeDataString(imdbId),
                "POST",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "apikey", "0ab215a8b1977939201640fa14c66bab" }
                },
                "application/json");
        }

        public static string GetJson(string url)
        {
            return Send(url, "GET", null, "application/json");
        }

        private static string BuildSignature(string url, string ts, string method)
        {
            var path = new Uri(url).AbsolutePath;
            var raw = string.Join("&", method.ToUpperInvariant(), Uri.EscapeDataString(path), ts);
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes("bf7dddc7c9cfe6f7"));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw)));
        }

        private static string Send(string url, string method, IDictionary<string, string> postData, string acceptHeader)
        {
            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null || string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            try
            {
                var requestOptions = new HttpRequestOptions
                {
                    Url = url,
                    AcceptHeader = acceptHeader,
                    UserAgent = "api-client/1 com.douban.frodo/7.22.0.beta9(231) Android/23 product/Mate 40 vendor/HUAWEI model/Mate 40 brand/HUAWEI rom/android network/wifi platform/AndroidPad",
                    EnableDefaultUserAgent = false,
                    TimeoutMs = 8000,
                    CacheMode = (CacheMode)1,
                    CacheLength = HttpCacheDuration,
                    LogRequest = true,
                    LogResponse = true,
                    LogRequestAsDebug = true
                };

                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && postData != null)
                {
                    requestOptions.SetPostData(postData);
                }

                if (url.IndexOf("api.douban.com", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    requestOptions.UserAgent = "MediaInfoKeeper";
                }

                using var response = httpClient.SendAsync(requestOptions, method).GetAwaiter().GetResult();
                using var reader = new StreamReader(response.Content);
                var body = reader.ReadToEnd();
                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    Plugin.Instance?.Logger?.Debug("DoubanApiClient 请求失败: status={0}, url={1}, body={2}", (int)response.StatusCode, url, body);
                    return null;
                }

                return body;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Debug("DoubanApiClient 请求异常: url={0}, msg={1}", url, ex.Message);
                return null;
            }
        }
    }
}
