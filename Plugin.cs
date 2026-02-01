using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using Emby.Web.GenericEdit.Common;
using MediaInfoKeeper.Configuration;
using MediaInfoKeeper.Options.Store;
using MediaInfoKeeper.Options.View;
using MediaInfoKeeper.Services;
using MediaInfoKeeper.Services.IntroSkip;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper
{
    /// <summary>
    /// The plugin.
    /// </summary>
    public class Plugin : BasePlugin, IHasThumbImage, IHasUIPages
    {
        public const string PluginName = "MediaInfoKeeper";
        public const string TaskCategoryName = "Auto-MediaInfoKeeper";

        public static Plugin Instance { get; private set; }
        public static MediaInfoService MediaInfoService { get; private set; }
        public static LibraryService LibraryService { get; private set; }
        public static IntroSkipChapterApi IntroSkipChapterApi { get; private set; }
        public static IntroSkipPlaySessionMonitor IntroSkipPlaySessionMonitor { get; private set; }
        public static IntroScanService IntroScanService { get; private set; }

        private readonly Guid id = new Guid("874D7056-072D-43A4-16DD-BC32665B9563");
        private readonly ILogger logger;
        private List<IPluginUIPageController> pages;

        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IItemRepository itemRepository;
        private readonly IFileSystem fileSystem;
        private readonly IUserManager userManager;
        private readonly ISessionManager sessionManager;
        private readonly IApplicationHost applicationHost;

        internal static IProviderManager ProviderManager { get; private set; }
        internal static IFileSystem FileSystem { get; private set; }
        internal static ILibraryManager LibraryManager { get; private set; }
        internal IApplicationHost AppHost => this.applicationHost;

        private bool currentPersistMediaInfo;
        internal readonly PluginOptionsStore OptionsStore;
        internal readonly MainPageOptionsStore MainPageOptionsStore;
        internal readonly GitHubOptionsStore GitHubOptionsStore;
        internal readonly IntroSkipOptionsStore IntroSkipOptionsStore;
        internal readonly ProxyOptionsStore ProxyOptionsStore;
        internal readonly EnhanceChineseSearchOptionsStore EnhanceChineseSearchOptionsStore;
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        private static string latestReleaseVersionCache;
        private static readonly object ReleaseHistoryLock = new object();
        private static DateTimeOffset releaseHistoryCheckedUtc = DateTimeOffset.MinValue;
        private static string releaseHistoryBodyCache;
        private static readonly TimeSpan LatestVersionCacheDuration = TimeSpan.FromMinutes(30);
        private const string GitHubReleaseHistoryUrl = "https://api.github.com/repos/honue/MediaInfoKeeper/releases?per_page=100&page=";

        /// <summary>初始化插件并注册库事件处理。</summary>
        public Plugin(
            IApplicationHost applicationHost,
            ILogManager logManager,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IItemRepository itemRepository,
            IUserManager userManager,
            ISessionManager sessionManager,
            IJsonSerializer jsonSerializer,
            IFileSystem fileSystem)
        {
            Instance = this;
            this.logger = logManager.GetLogger(this.Name);
            this.logger.Info($"插件 {this.Name} 正在加载");

            this.applicationHost = applicationHost;
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.itemRepository = itemRepository;
            this.fileSystem = fileSystem;
            this.userManager = userManager;
            this.sessionManager = sessionManager;
            ProviderManager = providerManager;
            FileSystem = fileSystem;
            LibraryManager = libraryManager;

            OptionsStore = new PluginOptionsStore(applicationHost, this.logger, this.Name,
                PrepareOptionsForUi, HandleOptionsSaving, HandleOptionsSaved);
            MainPageOptionsStore = new MainPageOptionsStore(OptionsStore);
            GitHubOptionsStore = new GitHubOptionsStore(OptionsStore);
            IntroSkipOptionsStore = new IntroSkipOptionsStore(OptionsStore);
            ProxyOptionsStore = new ProxyOptionsStore(OptionsStore);
            EnhanceChineseSearchOptionsStore = new EnhanceChineseSearchOptionsStore(OptionsStore);

            FfprobeGuard.Initialize(this.logger, this.Options.MainPage?.DisableSystemFfprobe ?? true);
            MetadataProvidersWatcher.Initialize(this.logger, this.Options.MainPage?.EnableMetadataProvidersWatcher ?? true);
            UnlockIntroSkip.Initialize(this.logger, this.Options.IntroSkip?.UnlockIntroSkip ?? false);
            UnlockIntroSkip.Configure(this.Options);
            IntroMarkerProtect.Initialize(this.logger, this.Options.IntroSkip?.ProtectIntroMarkers ?? true);
            ProxyServer.Initialize(this.logger, this.Options.Proxy?.EnableProxyServer ?? false);
            SearchScopeUtility.UpdateSearchScope(this.Options.EnhanceChineseSearch?.SearchScope);
            EnhanceChineseSearch.Initialize(this.logger, this.Options.EnhanceChineseSearch);

            this.currentPersistMediaInfo = this.Options.MainPage?.PersistMediaInfoEnabled ?? true;

            LibraryService = new LibraryService(libraryManager, providerManager, fileSystem);
            MediaInfoService = new MediaInfoService(libraryManager, fileSystem, itemRepository, jsonSerializer);
            IntroSkipChapterApi = new IntroSkipChapterApi(libraryManager, itemRepository, this.logger);
            IntroScanService = new IntroScanService(logManager, libraryManager);
            IntroSkipPlaySessionMonitor = new IntroSkipPlaySessionMonitor(
                libraryManager, userManager, sessionManager, this.logger);

            if (this.Options.IntroSkip?.EnableIntroSkip == true)
            {
                IntroSkipPlaySessionMonitor.Initialize();
                IntroSkipPlaySessionMonitor.UpdateLibraryPathsInScope(this.Options.IntroSkip.LibraryScope);
                IntroSkipPlaySessionMonitor.UpdateUsersInScope(this.Options.IntroSkip.UserScope);
            }

            this.libraryManager.ItemAdded += this.OnItemAdded;
            this.libraryManager.ItemRemoved += this.OnItemRemoved;
            this.logger.Info($"插件 {this.Name} 加载完成");
        }

        public override string Description => "Persist/restore MediaInfo to speed up first playback.";

        public override Guid Id => this.id;

        public sealed override string Name => PluginName;

        public PluginConfiguration Options
        {
            get
            {
                var options = this.OptionsStore.GetOptions();
                options.MainPage ??= new MainPageOptions();
                return options;
            }
        }

        public ILogger Logger => this.logger;

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            var type = this.GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Resources.ThumbImage.png");
        }

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (this.pages == null)
                {
                    this.pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(this.GetPluginInfo(), this.MainPageOptionsStore,
                            this.GitHubOptionsStore, this.IntroSkipOptionsStore, this.ProxyOptionsStore,
                            this.EnhanceChineseSearchOptionsStore)
                    };
                }

                return this.pages.AsReadOnly();
            }
        }

        internal void PrepareOptionsForUi(PluginConfiguration options)
        {
            if (options == null)
            {
                return;
            }

            options.MainPage ??= new MainPageOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.Proxy ??= new ProxyOptions();
            options.GitHub ??= new GitHubOptions();
            options.EnhanceChineseSearch ??= new EnhanceChineseSearchOptions();
            options.EnhanceChineseSearch.Initialize();

            var list = new List<EditorSelectOption>();
            foreach (var folder in this.libraryManager.GetVirtualFolders())
            {
                if (folder == null)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(folder.Name) ? folder.ItemId : folder.Name;
                list.Add(new EditorSelectOption
                {
                    Value = folder.ItemId,
                    Name = name,
                    IsEnabled = true
                });
            }

            options.MainPage.LibraryList = list;
            options.IntroSkip.LibraryList = list;
            options.GitHub.CurrentVersion = GetCurrentVersion();
            options.GitHub.LatestReleaseVersion = GetLatestReleaseVersion();
            options.GitHub.ReleaseHistoryBody = GetReleaseHistoryBody();
        }

        internal bool HandleOptionsSaving(PluginConfiguration options)
        {
            if (options?.MainPage == null)
            {
                return true;
            }

            options.MainPage.CatchupLibraries = NormalizeScopedLibraries(options.MainPage.CatchupLibraries);
            options.MainPage.ScheduledTaskLibraries = NormalizeScopedLibraries(options.MainPage.ScheduledTaskLibraries);
            if (options.IntroSkip != null)
            {
                options.IntroSkip.LibraryScope = NormalizeScopedLibraries(options.IntroSkip.LibraryScope);
                options.IntroSkip.MarkerEnabledLibraryScope = NormalizeScopedLibraries(options.IntroSkip.MarkerEnabledLibraryScope);
            }
            return true;
        }

        /// <summary>应用配置变更并更新缓存标记。</summary>
        internal void HandleOptionsSaved(PluginConfiguration options)
        {
            if (options == null)
            {
                return;
            }

            options.MainPage ??= new MainPageOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.EnhanceChineseSearch ??= new EnhanceChineseSearchOptions();

            this.currentPersistMediaInfo = options.MainPage.PersistMediaInfoEnabled;

            this.logger.Info($"{this.Name} 配置已更新。");
            this.logger.Info($"PersistMediaInfoEnabled 设置为 {options.MainPage.PersistMediaInfoEnabled}");
            this.logger.Info($"MediaInfoJsonRootFolder 设置为 {(string.IsNullOrEmpty(options.MainPage.MediaInfoJsonRootFolder) ? "EMPTY" : options.MainPage.MediaInfoJsonRootFolder)}");
            this.logger.Info($"DeleteMediaInfoJsonOnRemove 设置为 {options.MainPage.DeleteMediaInfoJsonOnRemove}");
            this.logger.Info($"CatchupLibraries 设置为 {(string.IsNullOrEmpty(options.MainPage.CatchupLibraries) ? "EMPTY" : options.MainPage.CatchupLibraries)}");
            this.logger.Info($"ScheduledTaskLibraries 设置为 {(string.IsNullOrEmpty(options.MainPage.ScheduledTaskLibraries) ? "EMPTY" : options.MainPage.ScheduledTaskLibraries)}");
            this.logger.Info($"EnableMetadataProvidersWatcher 设置为 {options.MainPage.EnableMetadataProvidersWatcher}");
            this.logger.Info($"MaxConcurrentCount 设置为 {options.MainPage.MaxConcurrentCount}");
            this.logger.Info($"EnableProxyServer 设置为 {options.Proxy.EnableProxyServer}");
            this.logger.Info($"ProxyServerUrl 设置为 {(string.IsNullOrEmpty(options.Proxy.ProxyServerUrl) ? "EMPTY" : options.Proxy.ProxyServerUrl)}");
            this.logger.Info($"IgnoreCertificateValidation 设置为 {options.Proxy.IgnoreCertificateValidation}");
            this.logger.Info($"WriteProxyEnvVars 设置为 {options.Proxy.WriteProxyEnvVars}");
            this.logger.Info($"EnhanceChineseSearch 设置为 {options.EnhanceChineseSearch.EnhanceChineseSearch}");
            this.logger.Info($"ExcludeOriginalTitleFromSearch 设置为 {options.EnhanceChineseSearch.ExcludeOriginalTitleFromSearch}");

            FfprobeGuard.Configure(options.MainPage.DisableSystemFfprobe);
            MetadataProvidersWatcher.Configure(options.MainPage.EnableMetadataProvidersWatcher);
            UnlockIntroSkip.Configure(options);
            ProxyServer.Configure(options.Proxy.EnableProxyServer);
            SearchScopeUtility.UpdateSearchScope(options.EnhanceChineseSearch.SearchScope);
            EnhanceChineseSearch.Configure(options.EnhanceChineseSearch);

            if (options.IntroSkip.EnableIntroSkip)
            {
                IntroSkipPlaySessionMonitor.Initialize();
                IntroSkipPlaySessionMonitor.UpdateLibraryPathsInScope(options.IntroSkip.LibraryScope);
                IntroSkipPlaySessionMonitor.UpdateUsersInScope(options.IntroSkip.UserScope);
            }
            else
            {
                IntroSkipPlaySessionMonitor.Dispose();
            }

            IntroMarkerProtect.Configure(options.IntroSkip.ProtectIntroMarkers);
        }

        private string NormalizeScopedLibraries(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in this.libraryManager.GetVirtualFolders())
            {
                if (folder == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(folder.ItemId))
                {
                    lookup[folder.ItemId] = folder.ItemId;
                }

                if (!string.IsNullOrWhiteSpace(folder.Name))
                {
                    lookup[folder.Name.Trim()] = folder.ItemId;
                }
            }

            var tokens = raw.Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var normalized = new List<string>();
            foreach (var token in tokens)
            {
                var value = token.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (lookup.TryGetValue(value, out var mapped))
                {
                    normalized.Add(mapped);
                }
                else
                {
                    normalized.Add(value);
                }
            }

            return string.Join(",", normalized);
        }

        /// <summary>处理新入库条目，按配置执行持久化或恢复。</summary>
        private async void OnItemAdded(object sender, ItemChangeEventArgs e)
        {

            try
            {
                this.logger.Info($"{e.Item.Path} 新增剧集事件");
                if (!this.currentPersistMediaInfo)
                {
                    // 未启用持久化，直接跳过。
                    return;
                }

                if (!(e.Item is Video))
                {
                    // 仅处理视频条目。
                    return;
                }

                if (!LibraryService.IsItemInScope(e.Item))
                {
                    // 条目不在选定媒体库范围内。
                    this.logger.Info("跳过处理: 不在选定媒体库范围");
                    return;
                }

                var directoryService = new DirectoryService(this.logger, this.fileSystem);
                // 判断当前条目是否已有 MediaInfo。
                var hasMediaInfo = LibraryService.HasMediaInfo(e.Item);

                if (!hasMediaInfo)
                {
                    // 优先尝试从 JSON 恢复，减少首次提取耗时。
                    this.logger.Info("尝试从 JSON 恢复 MediaInfo");
                    var restoreResult = await MediaInfoService.DeserializeMediaInfo(e.Item, directoryService, "OnItemAdded", true).ConfigureAwait(false);
                    
                    // 如果不存在Json文件，则使用ffprobe 提取一次
                    if (restoreResult == MediaInfoService.MediaInfoRestoreResult.Failed)
                    {
                        // 恢复失败时先触发媒体信息提取，再写入 JSON。
                        this.logger.Info("恢复失败，开始提取 MediaInfo");

                        // 触发一次刷新以提取 MediaInfo。
                        e.Item.DateLastRefreshed = new DateTimeOffset();
                        using (FfprobeGuard.Allow())
                        {
                            this.logger.Info("恢复失败，是初次入库，进行下载元数据");
                            // 构建用于媒体信息提取的刷新参数与库选项。
                            var metadataRefreshOptions = new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
                            {
                                EnableRemoteContentProbe = true,
                                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                                ReplaceAllMetadata = true,
                                ReplaceAllImages = false
                            };

                            var itemCollectionFolders = (BaseItem[])this.libraryManager.GetCollectionFolders(e.Item);
                            var itemLibraryOptions = this.libraryManager.GetLibraryOptions(e.Item);
                            e.Item.DateLastRefreshed = new DateTimeOffset();
                            await this.providerManager
                                .RefreshSingleItem(e.Item, metadataRefreshOptions, itemCollectionFolders, itemLibraryOptions, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        // 提取完成后写入 JSON。
                        this.logger.Info("MediaInfo 提取完成，写入 JSON");
                        _ = MediaInfoService.SerializeMediaInfo(e.Item.InternalId, directoryService, true, "OnItemAdded WriteNewJson");
                    }
                    // 使用Json媒体信息数据，恢复成功后扫描所在物理路径，确保库状态刷新。
                    else if (restoreResult == MediaInfoService.MediaInfoRestoreResult.Restored)
                    {
                        var itemPath = e.Item.Path ?? e.Item.ContainingFolderPath ?? e.Item.Id.ToString();
                        this.logger.Info($"JSON 恢复成功，准备扫描物理路径 item: {itemPath}");
                        var scanOptions = new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
                        {
                            EnableRemoteContentProbe = false,
                            MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                            ReplaceAllMetadata = false,
                            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                            ReplaceAllImages = false,
                            EnableThumbnailImageExtraction = false,
                            EnableSubtitleDownloading = false
                        };

                        var parentPath = e.Item.ContainingFolderPath;
                        if (!string.IsNullOrEmpty(parentPath))
                        {
                            if (!this.fileSystem.DirectoryExists(parentPath))
                            {
                                this.logger.Info($"物理路径不存在，跳过扫描: {parentPath}");
                            }
                            else
                            {
                                var parentFolder = this.libraryManager.FindByPath(parentPath, true) as Folder;
                                if (parentFolder != null)
                                {
                                    this.logger.Info($"刷新父级条目: {parentPath}");
                                    try
                                    {
                                        var collectionFolders = (BaseItem[])this.libraryManager.GetCollectionFolders(parentFolder);
                                        var libraryOptions = this.libraryManager.GetLibraryOptions(parentFolder);
                                        using (FfprobeGuard.Allow())
                                        {
                                            await this.providerManager
                                            .RefreshSingleItem(parentFolder, scanOptions, collectionFolders, libraryOptions, CancellationToken.None)
                                            .ConfigureAwait(false);
                                        }
                                    }
                                    catch (Exception refreshEx)
                                    {
                                        this.logger.Error($"刷新父级条目失败: {parentPath}");
                                        this.logger.Error(refreshEx.Message);
                                        this.logger.Debug(refreshEx.StackTrace);
                                    }
                                }
                                else
                                {
                                    this.logger.Info($"未找到物理路径对应的文件夹项，跳过刷新: {parentPath}");
                                }
                            }
                        }
                        else
                        {
                            this.logger.Info($"未找到条目所在物理路径，跳过扫描 item: {itemPath}");
                        }
                    }
                }
                // 已有 MediaInfo 时，直接用媒体信息覆盖写入 JSON，保持最新。
                else
                {
                    this.logger.Info("已有 MediaInfo，覆盖写入 JSON");
                    _ = MediaInfoService.SerializeMediaInfo(e.Item.InternalId, directoryService, true, "OnItemAdded Overwrite");
                }

                if (this.Options.IntroSkip?.ScanIntroOnItemAdded == true && e.Item is Episode episode)
                {
                    if (IntroScanService.HasIntroMarkers(episode))
                    {
                        this.logger.Info("入库片头扫描跳过: 已存在片头标记");
                        return;
                    }

                    this.logger.Info("入库片头扫描: 触发片头检测");
                    await IntroScanService
                        .ScanEpisodesAsync(new List<Episode> { episode }, CancellationToken.None, null)
                        .ConfigureAwait(false);
                }

            }
            catch (Exception ex)
            {
                // 记录异常，避免影响库事件流程。
                this.logger.Error(ex.Message);
                this.logger.Debug(ex.StackTrace);
            }
        }



        /// <summary>条目移除且非恢复模式时，删除已持久化的 JSON。</summary>
        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            this.logger.Info($"{e.Item.Path} 删除剧集事件");
            // 未开启删除开关时直接跳过。
            if (!this.Options.MainPage.DeleteMediaInfoJsonOnRemove || !this.Options.MainPage.PersistMediaInfoEnabled)
            {
                return;
            }

            if (!(e.Item is Video))
            {
                return;
            }

            if (!LibraryService.IsItemInScope(e.Item))
            {
                return;
            }

            var directoryService = new DirectoryService(this.logger, this.fileSystem);
            logger.Info("同步删除 媒体信息 Json");
            MediaInfoService.DeleteMediaInfoJson(e.Item, directoryService, "Item Removed Event");
        }

        private string GetLatestReleaseVersion()
        {
            EnsureReleaseHistoryCache();
            return latestReleaseVersionCache;
        }

        private string GetCurrentVersion()
        {
            var version = this.GetType().Assembly.GetName().Version;
            return version == null ? "未知" : $"v{version.ToString(3)}";
        }

        private string GetReleaseHistoryBody()
        {
            EnsureReleaseHistoryCache();
            return releaseHistoryBodyCache;
        }

        private void EnsureReleaseHistoryCache()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - releaseHistoryCheckedUtc < LatestVersionCacheDuration &&
                !string.IsNullOrWhiteSpace(releaseHistoryBodyCache))
            {
                return;
            }

            lock (ReleaseHistoryLock)
            {
                if (now - releaseHistoryCheckedUtc < LatestVersionCacheDuration &&
                    !string.IsNullOrWhiteSpace(releaseHistoryBodyCache))
                {
                    return;
                }

                releaseHistoryCheckedUtc = now;
                var historyInfo = FetchReleaseHistoryInfo();
                releaseHistoryBodyCache = historyInfo.HistoryBody;
                latestReleaseVersionCache = historyInfo.LatestVersion;
            }
        }

        private ReleaseHistoryInfo FetchReleaseHistoryInfo()
        {
            try
            {
                var sb = new StringBuilder();
                var page = 1;
                var latestAssigned = false;
                var latestVersion = "未知";
                while (true)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{GitHubReleaseHistoryUrl}{page}");
                    request.Headers.UserAgent.ParseAdd("MediaInfoKeeper");
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");

                    var token = this.Options?.GitHub?.GitHubToken;
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
                    }

                    using var response = HttpClient.SendAsync(request).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        this.logger.Info($"获取 GitHub 历史版本失败: {(int)response.StatusCode} {response.ReasonPhrase}");
                        return new ReleaseHistoryInfo("获取失败", "获取失败");
                    }

                    var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Array ||
                        document.RootElement.GetArrayLength() == 0)
                    {
                        break;
                    }

                    foreach (var release in document.RootElement.EnumerateArray())
                    {
                        var tag = release.TryGetProperty("tag_name", out var tagElement)
                            ? tagElement.GetString()
                            : string.Empty;
                        var name = release.TryGetProperty("name", out var nameElement)
                            ? nameElement.GetString()
                            : string.Empty;
                        var body = release.TryGetProperty("body", out var bodyElement)
                            ? bodyElement.GetString()
                            : string.Empty;
                        var publishedAt = release.TryGetProperty("published_at", out var publishedElement)
                            ? publishedElement.GetString()
                            : string.Empty;
                        var publishedAtLocal = publishedAt;
                        if (!string.IsNullOrWhiteSpace(publishedAt) &&
                            DateTimeOffset.TryParse(publishedAt, out var publishedOffset))
                        {
                            publishedAtLocal = publishedOffset
                                .ToOffset(TimeSpan.FromHours(8))
                                .ToString("yyyy-MM-dd HH:mm:ss");
                        }

                        if (string.IsNullOrWhiteSpace(body))
                        {
                            body = "无更新说明";
                        }

                        if (!latestAssigned)
                        {
                            latestVersion = !string.IsNullOrWhiteSpace(tag) ? tag.Trim()
                                : !string.IsNullOrWhiteSpace(name) ? name.Trim()
                                : "未知";
                            latestAssigned = true;
                        }

                        sb.Append(string.IsNullOrWhiteSpace(tag) ? "Release" : tag.Trim());
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            sb.Append(" - ").Append(name.Trim());
                        }

                        if (!string.IsNullOrWhiteSpace(publishedAtLocal))
                        {
                            sb.Append(" (").Append(publishedAtLocal.Trim()).Append(')');
                        }

                        sb.AppendLine();
                        sb.AppendLine(body.Trim());
                        sb.AppendLine("----");
                    }

                    page++;
                }

                if (sb.Length == 0)
                {
                    return new ReleaseHistoryInfo("暂无发布记录", latestVersion);
                }

                return new ReleaseHistoryInfo(sb.ToString().TrimEnd(), latestVersion);
            }
            catch (Exception ex)
            {
                this.logger.Info($"获取 GitHub 历史版本失败: {ex.Message}");
                this.logger.Debug(ex.StackTrace);
                return new ReleaseHistoryInfo("获取失败", "获取失败");
            }
        }

        private readonly struct ReleaseHistoryInfo
        {
            public ReleaseHistoryInfo(string historyBody, string latestVersion)
            {
                HistoryBody = historyBody;
                LatestVersion = latestVersion;
            }

            public string HistoryBody { get; }

            public string LatestVersion { get; }
        }
    }
}
