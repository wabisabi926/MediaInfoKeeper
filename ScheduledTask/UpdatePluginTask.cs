using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MediaInfoKeeper.ScheduledTask
{


    public class UpdatePluginTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly IApplicationHost applicationHost;
        private readonly IApplicationPaths applicationPaths;
        private readonly IHttpClient httpClient;
        private readonly IJsonSerializer jsonSerializer;
        private readonly IActivityManager activityManager;
        private readonly IServerApplicationHost serverApplicationHost;
        private static string PluginAssemblyFilename => Assembly.GetExecutingAssembly().GetName().Name + ".dll";
        private static string RepoReleaseUrl => "https://api.github.com/repos/honue/MediaInfoKeeper/releases/latest";
        private static string RepoVersionUrl => "https://raw.githubusercontent.com/honue/MediaInfoKeeper/master/Version.json";

        public string Key => "UpdatePluginTask";

        public string Name => "6.更新插件";

        public string Description => "更新插件至最新版本";

        public string Category => Plugin.TaskCategoryName;

        public UpdatePluginTask(
            IApplicationHost applicationHost,
            IApplicationPaths applicationPaths,
            IHttpClient httpClient,
            IJsonSerializer jsonSerializer,
            IActivityManager activityManager,
            IServerApplicationHost serverApplicationHost)
        {
            this.logger = Plugin.Instance.Logger;
            this.applicationHost = applicationHost;
            this.applicationPaths = applicationPaths;
            this.httpClient = httpClient;
            this.jsonSerializer = jsonSerializer;
            this.activityManager = activityManager;
            this.serverApplicationHost = serverApplicationHost;
        }


        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromDays(3).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Task.Yield();
            progress.Report(0);

            try
            {
                var githubOptions = Plugin.Instance.Options.GitHub;
                var githubToken = githubOptions?.GitHubToken;
                var authHeader = string.IsNullOrWhiteSpace(githubToken) ? null : $"token {githubToken}";
                var currentVersion = ParseVersion(GetCurrentVersion());
                var embyVersion = Plugin.Instance?.AppHost?.ApplicationVersion ?? new Version(0, 0, 0, 0);

                logger.Info("开始检查插件更新：当前插件版本={0}，当前Emby版本={1}", currentVersion, embyVersion);

                using var response = await httpClient.SendAsync(new HttpRequestOptions
                {
                    Url = RepoReleaseUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    RequestHeaders =
                    {
                        ["Authorization"] = authHeader
                    }
                }, "GET").ConfigureAwait(false);

                await using var contentStream = response.Content;
                var apiResult = jsonSerializer.DeserializeFromStream<ApiResponseInfo>(contentStream);

                var remoteVersion = ParseVersion(apiResult?.tag_name);
                var compatibility = await FetchCompatibilityManifest(cancellationToken, authHeader).ConfigureAwait(false);
                var (minVersion, maxVersion) = GetEmbyVersionRange(compatibility);
                logger.Info(
                    "版本信息：最新插件={0}，当前插件={1}，当前Emby={2}，兼容Emby版本区间=[{3},{4}]",
                    remoteVersion,
                    currentVersion,
                    embyVersion,
                    minVersion?.ToString() ?? "*",
                    maxVersion?.ToString() ?? "*");

                if (!IsEmbyVersionCompatible(compatibility, embyVersion, out var incompatibleReason))
                {
                    logger.Warn("跳过插件更新：{0}", incompatibleReason);
                    activityManager.Create(new ActivityLogEntry
                    {
                        Name = Plugin.Instance.Name + " update skipped on " + serverApplicationHost.FriendlyName,
                        Type = "PluginUpdateSkipped",
                        Overview = incompatibleReason,
                        Severity = LogSeverity.Info
                    });

                    progress.Report(100);
                    return;
                }

                logger.Info("版本校验通过：允许检查并更新插件。");

                if (currentVersion.CompareTo(remoteVersion) < 0)
                {
                    var url = (apiResult?.assets ?? new List<ApiAssetInfo>())
                        .FirstOrDefault(asset => asset.name == PluginAssemblyFilename)
                        ?.browser_download_url;
                    if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                    {
                        throw new Exception("下载地址无效");
                    }

                    logger.Info("开始下载插件：版本={0}", remoteVersion);

                    await using (var responseStream = await httpClient.Get(new HttpRequestOptions
                                     {
                                         Url = url,
                                         CancellationToken = cancellationToken,
                                         UserAgent = "MediaInfoKeeper",
                                         EnableDefaultUserAgent = false,
                                         Progress = progress,
                                         RequestHeaders =
                                         {
                                             ["Authorization"] = authHeader
                                         }
                                     })
                                     .ConfigureAwait(false))
                    {
                        using (var memoryStream = new MemoryStream())
                        {
                            await responseStream.CopyToAsync(memoryStream, 81920, cancellationToken)
                                .ConfigureAwait(false);

                            memoryStream.Seek(0, SeekOrigin.Begin);
                            var dllFilePath = Path.Combine(applicationPaths.PluginsPath, PluginAssemblyFilename);

                            await using (var fileStream =
                                         new FileStream(dllFilePath, FileMode.Create, FileAccess.Write))
                            {
                                await memoryStream.CopyToAsync(fileStream, 81920, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                    }

                    logger.Info(
                        "插件更新完成：版本={0}，重启后生效", remoteVersion);

                    activityManager.Create(new ActivityLogEntry
                    {
                        Name = Plugin.Instance.Name + " Updated to " + remoteVersion + " on " +
                               serverApplicationHost.FriendlyName,
                        Type = "PluginUpdateInstalled",
                        Severity = LogSeverity.Info
                    });

                    applicationHost.NotifyPendingRestart();
                }
                else
                {
                    logger.Info("无需更新：最新版本={0}，当前版本={1}", remoteVersion, currentVersion);
                }
            }
            catch (Exception ex)
            {
                activityManager.Create(new ActivityLogEntry
                {
                    Name = Plugin.Instance.Name + " update failed on " + serverApplicationHost.FriendlyName,
                    Type = "PluginUpdateFailed",
                    Overview = ex.Message,
                    Severity = LogSeverity.Error
                });

                logger.Error("插件更新失败：{0}", ex.Message);
                logger.Debug(ex.StackTrace);
            }

            progress.Report(100);
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new Version(0, 0, 0, 0);
            }

            var normalized = value.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(1)
                : value;
            return new Version(normalized);
        }

        private async Task<PluginCompatibilityInfo> FetchCompatibilityManifest(
            CancellationToken cancellationToken,
            string authHeader)
        {
            try
            {
                using var response = await httpClient.SendAsync(new HttpRequestOptions
                {
                    Url = RepoVersionUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    RequestHeaders =
                    {
                        ["Authorization"] = authHeader
                    }
                }, "GET").ConfigureAwait(false);

                await using var stream = response.Content;
                var manifest = jsonSerializer.DeserializeFromStream<PluginManifestInfo>(stream);
                var compatibility = manifest?.latest;
                if (compatibility != null)
                {
                    return compatibility;
                }
            }
            catch (Exception ex)
            {
                logger.Debug("加载 Version.json 失败：url={0}, error={1}", RepoVersionUrl, ex.Message);
            }

            logger.Info("未获取到 Version.json 兼容信息，默认允许更新。");
            return null;
        }

        private static (Version minVersion, Version maxVersion) GetEmbyVersionRange(PluginCompatibilityInfo compatibility)
        {
            if (compatibility == null)
            {
                return (null, null);
            }

            var minVersion = ParseOptionalVersion(
                compatibility.minEmbyVersion ??
                compatibility.embyMinVersion ??
                compatibility.min_version);
            var maxVersion = ParseOptionalVersion(
                compatibility.maxEmbyVersion ??
                compatibility.embyMaxVersion ??
                compatibility.max_version);
            return (minVersion, maxVersion);
        }

        private static bool IsEmbyVersionCompatible(
            PluginCompatibilityInfo compatibility,
            Version currentEmbyVersion,
            out string reason)
        {
            reason = null;
            if (currentEmbyVersion == null)
            {
                currentEmbyVersion = new Version(0, 0, 0, 0);
            }

            if (compatibility == null)
            {
                return true;
            }

            var (minVersion, maxVersion) = GetEmbyVersionRange(compatibility);

            if (minVersion != null && currentEmbyVersion < minVersion)
            {
                reason = string.Format(
                    "当前 Emby 版本 {0} 低于插件要求的最小版本 {1}",
                    currentEmbyVersion,
                    minVersion);
                return false;
            }

            if (maxVersion != null && currentEmbyVersion > maxVersion)
            {
                reason = string.Format(
                    "当前 Emby 版本 {0} 高于插件支持的最大版本 {1}",
                    currentEmbyVersion,
                    maxVersion);
                return false;
            }

            return true;
        }

        private static Version ParseOptionalVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(1)
                : value;
            return Version.TryParse(normalized, out var version) ? version : null;
        }

        private static string GetCurrentVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0.0" : $"v{version.ToString(4)}";
        }

        internal class ApiResponseInfo
        {
            public string tag_name { get; set; }

            public List<ApiAssetInfo> assets { get; set; }
        }

        internal class PluginManifestInfo
        {
            public PluginCompatibilityInfo latest { get; set; }
        }

        internal class PluginCompatibilityInfo
        {
            public string minEmbyVersion { get; set; }

            public string maxEmbyVersion { get; set; }

            public string embyMinVersion { get; set; }

            public string embyMaxVersion { get; set; }

            public string min_version { get; set; }

            public string max_version { get; set; }
        }

        internal class ApiAssetInfo
        {
            public string name { get; set; }

            public string browser_download_url { get; set; }
        }
    }
}
