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
using System.Text.RegularExpressions;
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
        private const int ReleasePageSize = 100;
        private const int MaxReleasePages = 10;
        private static string PluginAssemblyFilename => Assembly.GetExecutingAssembly().GetName().Name + ".dll";
        private static string RepoReleaseUrlTemplate => $"https://api.github.com/repos/honue/MediaInfoKeeper/releases?per_page={ReleasePageSize}&page={{0}}";
        private static string RepoVersionUrl => "https://raw.githubusercontent.com/honue/MediaInfoKeeper/master/Version.json";

        public string Key => "UpdatePluginTask";

        public string Name => "1.更新插件";

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
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = DayOfWeek.Monday,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            };

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = DayOfWeek.Thursday,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Task.Yield();
            progress.Report(0);

            try
            {
                var githubToken = Plugin.Instance.Options.GitHub?.GitHubToken;
                var updateChannel = string.IsNullOrWhiteSpace(Plugin.Instance.Options.GitHub?.UpdateChannel)
                    ? Options.GitHubOptions.UpdateChannelOption.Stable.ToString()
                    : Plugin.Instance.Options.GitHub.UpdateChannel;
                var currentVersion = ParseVersion(GetCurrentVersion());
                var embyVersion = Plugin.Instance?.AppHost?.ApplicationVersion ?? new Version(0, 0, 0, 0);

                logger.Info(
                    "开始检查插件更新：当前插件版本={0}，当前Emby版本={1}，更新频道={2}",
                    currentVersion,
                    embyVersion,
                    updateChannel);

                var apiResult = await FetchReleaseForChannel(cancellationToken, updateChannel, githubToken).ConfigureAwait(false);
                if (apiResult == null)
                {
                    throw new Exception("未找到匹配当前更新频道的 Release");
                }

                var remoteVersion = ParseVersion(apiResult?.tag_name);
                var compatibility = await FetchCompatibilityManifest(cancellationToken, updateChannel).ConfigureAwait(false);
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

                    var downloadRequestOptions = new HttpRequestOptions
                    {
                        Url = url,
                        CancellationToken = cancellationToken,
                        UserAgent = "MediaInfoKeeper",
                        EnableDefaultUserAgent = false,
                        LogRequest = true,
                        LogResponse = true,
                        Progress = progress
                    };
                    if (!string.IsNullOrWhiteSpace(githubToken))
                    {
                        downloadRequestOptions.RequestHeaders["Authorization"] = $"token {githubToken}";
                    }

                    using (var downloadResponse = await httpClient.GetResponse(downloadRequestOptions)
                               .ConfigureAwait(false))
                    {
                        if ((int)downloadResponse.StatusCode < 200 || (int)downloadResponse.StatusCode >= 300)
                        {
                            using var reader = new StreamReader(downloadResponse.Content);
                            var responseBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                            logger.Error("下载插件失败：status={0}, body={1}", (int)downloadResponse.StatusCode, responseBody);
                            throw new Exception($"下载插件失败: {(int)downloadResponse.StatusCode}");
                        }

                        using (var memoryStream = new MemoryStream())
                        {
                            await downloadResponse.Content.CopyToAsync(memoryStream, 81920, cancellationToken)
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
            if (Version.TryParse(normalized, out var version))
            {
                return NormalizeVersion(version);
            }

            var match = Regex.Match(normalized, @"^(?<core>\d+(?:\.\d+){0,3})(?:[-+][A-Za-z.-]*?(?<suffix>\d+))?", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return new Version(0, 0, 0, 0);
            }

            var coreText = match.Groups["core"].Value;
            if (!Version.TryParse(coreText, out var coreVersion))
            {
                return new Version(0, 0, 0, 0);
            }

            coreVersion = NormalizeVersion(coreVersion);
            var suffixGroup = match.Groups["suffix"];
            if (!suffixGroup.Success || !int.TryParse(suffixGroup.Value, out var suffixNumber))
            {
                return coreVersion;
            }

            return new Version(coreVersion.Major, coreVersion.Minor, coreVersion.Build, suffixNumber);
        }

        private async Task<PluginCompatibilityInfo> FetchCompatibilityManifest(
            CancellationToken cancellationToken,
            string updateChannel)
        {
            try
            {
                var githubToken = Plugin.Instance.Options.GitHub?.GitHubToken;
                var manifestRequestOptions = new HttpRequestOptions
                {
                    Url = RepoVersionUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    LogRequest = true,
                    LogResponse = true
                };
                if (!string.IsNullOrWhiteSpace(githubToken))
                {
                    manifestRequestOptions.RequestHeaders["Authorization"] = $"token {githubToken}";
                }

                using var response = await httpClient.SendAsync(manifestRequestOptions, "GET").ConfigureAwait(false);
                string manifestResponseBody;
                await using (var stream = response.Content)
                using (var reader = new StreamReader(stream))
                {
                    manifestResponseBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    logger.Error("加载 Version.json 失败：status={0}, body={1}", (int)response.StatusCode, manifestResponseBody);
                    throw new Exception($"加载 Version.json 失败: {(int)response.StatusCode}");
                }

                var manifest = jsonSerializer.DeserializeFromString<PluginManifestInfo>(manifestResponseBody);
                var compatibility = SelectCompatibilityInfo(manifest, updateChannel);
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

        private async Task<ApiResponseInfo> FetchReleaseForChannel(
            CancellationToken cancellationToken,
            string updateChannel,
            string githubToken)
        {
            for (var page = 1; page <= MaxReleasePages; page++)
            {
                var releaseRequestOptions = new HttpRequestOptions
                {
                    Url = string.Format(RepoReleaseUrlTemplate, page),
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    LogRequest = true,
                    LogResponse = true
                };
                if (!string.IsNullOrWhiteSpace(githubToken))
                {
                    releaseRequestOptions.RequestHeaders["Authorization"] = $"token {githubToken}";
                }

                using var response = await httpClient.SendAsync(releaseRequestOptions, "GET").ConfigureAwait(false);
                string releaseResponseBody;
                await using (var contentStream = response.Content)
                using (var reader = new StreamReader(contentStream))
                {
                    releaseResponseBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    logger.Error("获取 Release 失败：page={0}, status={1}, body={2}", page, (int)response.StatusCode, releaseResponseBody);
                    throw new Exception($"获取 Release 失败: {(int)response.StatusCode}");
                }

                var releaseResults = jsonSerializer.DeserializeFromString<List<ApiResponseInfo>>(releaseResponseBody) ??
                                     new List<ApiResponseInfo>();
                var selected = SelectReleaseForChannel(releaseResults, updateChannel);
                if (selected != null)
                {
                    return selected;
                }

                if (releaseResults.Count < ReleasePageSize)
                {
                    break;
                }
            }

            return null;
        }

        private static ApiResponseInfo SelectReleaseForChannel(
            IEnumerable<ApiResponseInfo> releases,
            string updateChannel)
        {
            var preferBeta = string.Equals(
                updateChannel,
                Options.GitHubOptions.UpdateChannelOption.Beta.ToString(),
                StringComparison.OrdinalIgnoreCase);

            var candidates = releases?
                .Where(r => r != null && !r.draft)
                .ToList() ?? new List<ApiResponseInfo>();
            if (preferBeta)
            {
                return candidates.FirstOrDefault();
            }

            return candidates.FirstOrDefault(r => !r.prerelease);
        }

        private static Version NormalizeVersion(Version version)
        {
            if (version == null)
            {
                return new Version(0, 0, 0, 0);
            }

            var build = version.Build >= 0 ? version.Build : 0;
            var revision = version.Revision >= 0 ? version.Revision : 0;
            return new Version(version.Major, version.Minor, build, revision);
        }

        private static PluginCompatibilityInfo SelectCompatibilityInfo(
            PluginManifestInfo manifest,
            string updateChannel)
        {
            if (manifest == null)
            {
                return null;
            }

            if (string.Equals(
                    updateChannel,
                    Options.GitHubOptions.UpdateChannelOption.Beta.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return manifest.beta ?? manifest.latest;
            }

            return manifest.latest;
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

        internal class PluginManifestInfo
        {
            public PluginCompatibilityInfo latest { get; set; }

            public PluginCompatibilityInfo beta { get; set; }
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

        internal class ApiResponseInfo
        {
            public string tag_name { get; set; }

            public bool prerelease { get; set; }

            public bool draft { get; set; }

            public List<ApiAssetInfo> assets { get; set; }
        }
    }
}
