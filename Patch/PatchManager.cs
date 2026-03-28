using System;
using System.Collections.Generic;
using System.Linq;
using MediaInfoKeeper.Options;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 集中初始化各补丁，并汇总其启用状态、等待状态与说明信息。
    /// </summary>
    public static class PatchManager
    {
        private static readonly object InitLock = new object();
        private static bool initialized;
        private static ILogger logger;
        private static readonly List<PatchTracker> trackers = new List<PatchTracker>();

        public static IReadOnlyCollection<PatchTracker> Trackers => trackers.AsReadOnly();

        public static void Initialize(ILogger pluginLogger, PluginConfiguration options)
        {
            lock (InitLock)
            {
                logger = pluginLogger;
                if (!initialized)
                {
                    trackers.Clear();
                    initialized = true;
                }

                var safeOptions = EnsureOptions(options);
                var netWorkOptions = safeOptions.GetNetWorkOptions();

                Register("FfprobeGuard");
                FfprobeGuard.Initialize(logger, true);
                UpdateTracker("FfprobeGuard", true, FfprobeGuard.IsReady, null);

                Register("ProviderManager");
                ProviderManager.Initialize(logger, true);
                UpdateTracker("ProviderManager", true,
                    ProviderManager.IsReady, null);

                Register("ImageCapture");
                ImageCapture.Initialize(logger, safeOptions.MetaData.EnableImageCapture);
                UpdateTracker("ImageCapture", safeOptions.MetaData.EnableImageCapture, ImageCapture.IsReady, null);

                Register("MetadataProvidersWatcher");
                MetadataProvidersWatcher.Initialize(logger, true);
                UpdateTracker("MetadataProvidersWatcher", true,
                    MetadataProvidersWatcher.IsReady, null);

                Register("MovieDbTitle");
                MovieDbTitle.Initialize(logger, safeOptions.MetaData.EnableAlternativeTitleFallback);
                UpdateTracker(
                    "MovieDbTitle",
                    safeOptions.MetaData.EnableAlternativeTitleFallback,
                    MovieDbTitle.IsReady,
                    MovieDbTitle.IsWaiting ? "waiting for MovieDb assembly" : null,
                    MovieDbTitle.IsWaiting);

                Register("TvdbTitle");
                TvdbTitle.Initialize(logger, safeOptions.MetaData.EnableTvdbFallback);
                UpdateTracker(
                    "TvdbTitle",
                    safeOptions.MetaData.EnableTvdbFallback,
                    TvdbTitle.IsReady,
                    TvdbTitle.IsWaiting ? "waiting for Tvdb assembly" : null,
                    TvdbTitle.IsWaiting);

                Register("MovieDbEpisodeGroup");
                MovieDbEpisodeGroup.Initialize(
                    logger,
                    safeOptions.MetaData.EnableMovieDbEpisodeGroup,
                    safeOptions.MetaData.EnableLocalEpisodeGroup);
                UpdateTracker(
                    "MovieDbEpisodeGroup",
                    safeOptions.MetaData.EnableMovieDbEpisodeGroup,
                    MovieDbEpisodeGroup.IsReady,
                    MovieDbEpisodeGroup.IsWaiting ? "waiting for MovieDb assembly" : null,
                    MovieDbEpisodeGroup.IsWaiting);

                Register("OriginalPoster");
                OriginalPoster.Initialize(logger, safeOptions.MetaData.EnableOriginalPoster);
                UpdateTracker(
                    "OriginalPoster",
                    safeOptions.MetaData.EnableOriginalPoster,
                    OriginalPoster.IsReady,
                    OriginalPoster.IsWaiting ? "waiting for MovieDb assembly" : null,
                    OriginalPoster.IsWaiting);

                Register("UnlockIntroSkip");
                IntroUnlock.Initialize(logger, safeOptions.IntroSkip.UnlockIntroSkip);
                IntroUnlock.Configure(safeOptions);
                UpdateTracker("UnlockIntroSkip", safeOptions.IntroSkip.UnlockIntroSkip, IntroUnlock.IsReady, null);

                Register("IntroMarkerProtect");
                IntroMarkerProtect.Initialize(logger, safeOptions.IntroSkip.ProtectIntroMarkers);
                UpdateTracker("IntroMarkerProtect", safeOptions.IntroSkip.ProtectIntroMarkers, IntroMarkerProtect.IsReady, null);

                Register("NetworkServer");
                NetworkServer.Initialize(logger, netWorkOptions.EnableProxyServer);
                UpdateTracker(
                    "NetworkServer",
                    netWorkOptions.EnableProxyServer,
                    NetworkServer.IsReady,
                    BuildProxyNotes(),
                    false);

                Register("LocalDiscoveryAddress");
                LocalDiscoveryAddress.Initialize(logger, netWorkOptions.CustomLocalDiscoveryAddress);
                UpdateTracker(
                    "LocalDiscoveryAddress",
                    !string.IsNullOrWhiteSpace(netWorkOptions.CustomLocalDiscoveryAddress),
                    LocalDiscoveryAddress.IsConfiguredBehaviorReady,
                    BuildLocalDiscoveryNotes(),
                    false);

                Register("ChineseSearch");
                ChineseSearch.UpdateSearchScope(safeOptions.Enhance.SearchScope);
                ChineseSearch.Initialize(logger, safeOptions.Enhance);
                UpdateTracker(
                    "ChineseSearch",
                    safeOptions.Enhance.EnhanceChineseSearch || safeOptions.Enhance.EnhanceChineseSearchRestore,
                    ChineseSearch.IsReady,
                    null);

                Register("DeepDelete");
                DeepDelete.Initialize(logger, safeOptions.Enhance.EnableDeepDelete);
                UpdateTracker("DeepDelete", safeOptions.Enhance.EnableDeepDelete, DeepDelete.IsReady, null);

                Register("NfoMetadataEnhance");
                NfoMetadataEnhance.Initialize(logger, safeOptions.Enhance.EnableNfoMetadataEnhance);
                UpdateTracker(
                    "NfoMetadataEnhance",
                    safeOptions.Enhance.EnableNfoMetadataEnhance,
                    NfoMetadataEnhance.IsReady,
                    NfoMetadataEnhance.IsWaiting ? "waiting for NfoMetadata assembly" : null,
                    NfoMetadataEnhance.IsWaiting);

                Register("HidePersonNoImage");
                HidePersonNoImage.Initialize(logger, safeOptions.Enhance.HidePersonNoImage);
                UpdateTracker("HidePersonNoImage", safeOptions.Enhance.HidePersonNoImage, HidePersonNoImage.IsReady, null);

                Register("NoBoxsetsAutoCreation");
                NoBoxsetsAutoCreation.Initialize(logger, safeOptions.Enhance.NoBoxsetsAutoCreation);
                UpdateTracker(
                    "NoBoxsetsAutoCreation",
                    safeOptions.Enhance.NoBoxsetsAutoCreation,
                    NoBoxsetsAutoCreation.IsReady,
                    null);

                Register("EnforceLibraryOrder");
                EnforceLibraryOrder.Initialize(logger, safeOptions.Enhance.EnforceLibraryOrder);
                UpdateTracker(
                    "EnforceLibraryOrder",
                    safeOptions.Enhance.EnforceLibraryOrder,
                    EnforceLibraryOrder.IsReady,
                    null);

                Register("NotificationSystem");
                NotificationSystem.Initialize(logger);
                UpdateTracker("NotificationSystem", true, NotificationSystem.IsReady, null);

                Register("DashboardResourcePatch");
                DashboardResourcePatch.Initialize(
                    logger,
                    safeOptions.Enhance.DisableVideoSubtitleCrossOrigin,
                    safeOptions.Enhance.EnableDanmakuJs);
                UpdateTracker(
                    "DashboardResourcePatch",
                    safeOptions.Enhance.DisableVideoSubtitleCrossOrigin || safeOptions.Enhance.EnableDanmakuJs,
                    DashboardResourcePatch.IsReady,
                    null);

                Register("SystemLog");
                SystemLog.Initialize(logger, true, safeOptions.Enhance.SystemLogNameBlacklist);
                UpdateTracker("SystemLog", true, SystemLog.IsReady, SystemLog.IsReady ? null : "NamedLogger.Log 未命中");

                LogTrackerSummary();
            }
        }

        public static void Configure(PluginConfiguration options)
        {
            var safeOptions = EnsureOptions(options);
            var netWorkOptions = safeOptions.GetNetWorkOptions();

            FfprobeGuard.Configure(true);
            ProviderManager.Configure(true);
            ImageCapture.Configure(safeOptions.MetaData.EnableImageCapture);
            MetadataProvidersWatcher.Configure(true);
            IntroUnlock.Configure(safeOptions);
            NetworkServer.Configure(netWorkOptions.EnableProxyServer);
            LocalDiscoveryAddress.Configure(netWorkOptions.CustomLocalDiscoveryAddress);
            ChineseSearch.UpdateSearchScope(safeOptions.Enhance.SearchScope);
            ChineseSearch.Configure(safeOptions.Enhance);
            DeepDelete.Configure(safeOptions.Enhance.EnableDeepDelete);
            NfoMetadataEnhance.Configure(safeOptions.Enhance.EnableNfoMetadataEnhance);
            HidePersonNoImage.Configure(safeOptions.Enhance.HidePersonNoImage);
            NoBoxsetsAutoCreation.Configure(safeOptions.Enhance.NoBoxsetsAutoCreation);
            EnforceLibraryOrder.Configure(safeOptions.Enhance.EnforceLibraryOrder);
            DashboardResourcePatch.Configure(
                safeOptions.Enhance.DisableVideoSubtitleCrossOrigin,
                safeOptions.Enhance.EnableDanmakuJs);
            MovieDbTitle.Configure(safeOptions.MetaData.EnableAlternativeTitleFallback);
            TvdbTitle.Configure(safeOptions.MetaData.EnableTvdbFallback);
            MovieDbEpisodeGroup.Configure(safeOptions.MetaData.EnableMovieDbEpisodeGroup, safeOptions.MetaData.EnableLocalEpisodeGroup);
            OriginalPoster.Configure(safeOptions.MetaData.EnableOriginalPoster);
            IntroMarkerProtect.Configure(safeOptions.IntroSkip.ProtectIntroMarkers);

            UpdateTracker("FfprobeGuard", true, FfprobeGuard.IsReady, null);
            UpdateTracker("ProviderManager", true,
                ProviderManager.IsReady, null);
            UpdateTracker("ImageCapture", safeOptions.MetaData.EnableImageCapture, ImageCapture.IsReady, null);
            UpdateTracker("MetadataProvidersWatcher", true,
                MetadataProvidersWatcher.IsReady, null);
            UpdateTracker("MovieDbTitle", safeOptions.MetaData.EnableAlternativeTitleFallback, MovieDbTitle.IsReady,
                MovieDbTitle.IsWaiting ? "waiting for MovieDb assembly" : null, MovieDbTitle.IsWaiting);
            UpdateTracker("TvdbTitle", safeOptions.MetaData.EnableTvdbFallback, TvdbTitle.IsReady,
                TvdbTitle.IsWaiting ? "waiting for Tvdb assembly" : null, TvdbTitle.IsWaiting);
            UpdateTracker("MovieDbEpisodeGroup", safeOptions.MetaData.EnableMovieDbEpisodeGroup, MovieDbEpisodeGroup.IsReady,
                MovieDbEpisodeGroup.IsWaiting ? "waiting for MovieDb assembly" : null, MovieDbEpisodeGroup.IsWaiting);
            UpdateTracker("OriginalPoster", safeOptions.MetaData.EnableOriginalPoster, OriginalPoster.IsReady,
                OriginalPoster.IsWaiting ? "waiting for MovieDb assembly" : null, OriginalPoster.IsWaiting);
            UpdateTracker("UnlockIntroSkip", safeOptions.IntroSkip.UnlockIntroSkip, IntroUnlock.IsReady, null);
            UpdateTracker("IntroMarkerProtect", safeOptions.IntroSkip.ProtectIntroMarkers, IntroMarkerProtect.IsReady, null);
            UpdateTracker("NetworkServer", netWorkOptions.EnableProxyServer, NetworkServer.IsReady,
                BuildProxyNotes(), false);
            UpdateTracker(
                "LocalDiscoveryAddress",
                !string.IsNullOrWhiteSpace(netWorkOptions.CustomLocalDiscoveryAddress),
                LocalDiscoveryAddress.IsConfiguredBehaviorReady,
                BuildLocalDiscoveryNotes(),
                false);
            UpdateTracker(
                "ChineseSearch",
                safeOptions.Enhance.EnhanceChineseSearch || safeOptions.Enhance.EnhanceChineseSearchRestore,
                ChineseSearch.IsReady,
                null);
            UpdateTracker("DeepDelete", safeOptions.Enhance.EnableDeepDelete, DeepDelete.IsReady, null);
            UpdateTracker(
                "NfoMetadataEnhance",
                safeOptions.Enhance.EnableNfoMetadataEnhance,
                NfoMetadataEnhance.IsReady,
                NfoMetadataEnhance.IsWaiting ? "waiting for NfoMetadata assembly" : null,
                NfoMetadataEnhance.IsWaiting);
            UpdateTracker("HidePersonNoImage", safeOptions.Enhance.HidePersonNoImage, HidePersonNoImage.IsReady, null);
            UpdateTracker(
                "NoBoxsetsAutoCreation",
                safeOptions.Enhance.NoBoxsetsAutoCreation,
                NoBoxsetsAutoCreation.IsReady,
                null);
            UpdateTracker(
                "EnforceLibraryOrder",
                safeOptions.Enhance.EnforceLibraryOrder,
                EnforceLibraryOrder.IsReady,
                null);
            UpdateTracker("NotificationSystem", true, NotificationSystem.IsReady, null);
            UpdateTracker(
                "DashboardResourcePatch",
                safeOptions.Enhance.DisableVideoSubtitleCrossOrigin || safeOptions.Enhance.EnableDanmakuJs,
                DashboardResourcePatch.IsReady,
                null);
            SystemLog.Configure(true, safeOptions.Enhance.SystemLogNameBlacklist);
            UpdateTracker("SystemLog", true, SystemLog.IsReady, SystemLog.IsReady ? null : "NamedLogger.Log 未命中");

            LogTrackerSummary();
        }

        public static bool? IsHarmonyHealthy()
        {
            if (!trackers.Any())
            {
                return null;
            }

            return trackers.All(t => t.IsEnabled && t.Approach == PatchApproach.Harmony);
        }

        private static PluginConfiguration EnsureOptions(PluginConfiguration options)
        {
            var safe = options ?? new PluginConfiguration();
            safe.MainPage ??= new MainPageOptions();
            safe.IntroSkip ??= new IntroSkipOptions();
            safe.GetNetWorkOptions();
            safe.Enhance ??= new EnhanceOptions();
            safe.MetaData ??= new MetaDataOptions();
            return safe;
        }

        private static void Register(string name)
        {
            if (trackers.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal)))
            {
                return;
            }

            trackers.Add(new PatchTracker(name));
        }

        private static string BuildProxyNotes()
        {
            if (!NetworkServer.IsReady)
            {
                return "CreateHttpClientHandler 未命中";
            }

            return NetworkServer.IsHttpClientHookReady ? null : "HttpClientManager hook not ready";
        }

        private static string BuildLocalDiscoveryNotes()
        {
            var options = Plugin.Instance?.Options?.GetNetWorkOptions();
            var configuredValue = LocalDiscoveryAddress.NormalizeConfiguredValue(options?.CustomLocalDiscoveryAddress);
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                return null;
            }

            if (string.Equals(configuredValue, "BLOCKED", StringComparison.Ordinal))
            {
                return LocalDiscoveryAddress.IsUdpBlockReady ? "udp blocked" : "RespondToMessage 未命中";
            }

            if (LocalDiscoveryAddress.IsHttpReady && LocalDiscoveryAddress.IsUdpRewriteReady)
            {
                return "configured custom address";
            }

            if (LocalDiscoveryAddress.IsHttpReady && !LocalDiscoveryAddress.IsUdpRewriteReady)
            {
                return "http-only active";
            }

            if (!LocalDiscoveryAddress.IsHttpReady)
            {
                return "GetPublicSystemInfo/GetSystemInfo 未完全命中";
            }

            return "SendMessage/RespondToMessage 未完全命中";
        }

        private static void UpdateTracker(string name, bool enabled, bool success, string notes, bool waiting = false)
        {
            var tracker = trackers.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
            if (tracker == null)
            {
                return;
            }

            tracker.IsEnabled = enabled;
            tracker.Notes = notes;
            if (!enabled)
            {
                tracker.Health = PatchHealth.Disabled;
                return;
            }

            if (waiting)
            {
                tracker.Health = PatchHealth.Waiting;
                return;
            }

            tracker.Health = success ? PatchHealth.Enabled : PatchHealth.Failed;
        }

        private static void LogTrackerSummary()
        {
            if (!trackers.Any())
            {
                return;
            }

            var enabledCount = trackers.Count(t => t.Health == PatchHealth.Enabled);
            var disabledCount = trackers.Count(t => t.Health == PatchHealth.Disabled);
            var waitingCount = trackers.Count(t => t.Health == PatchHealth.Waiting);
            var failedCount = trackers.Count(t => t.Health == PatchHealth.Failed);

            logger?.Info(
                "补丁加载摘要：启用={0}，禁用={1}，等待={2}，失败={3}",
                enabledCount,
                disabledCount,
                waitingCount,
                failedCount);

            foreach (var tracker in trackers)
            {
                if (tracker.Health == PatchHealth.Failed)
                {
                    logger?.Warn("补丁状态：{0}=失败{1}",
                        tracker.Name,
                        string.IsNullOrWhiteSpace(tracker.Notes) ? string.Empty : " (" + tracker.Notes + ")");
                    continue;
                }

                if (tracker.Health == PatchHealth.Waiting)
                {
                    logger?.Info("补丁状态：{0}=等待{1}",
                        tracker.Name,
                        string.IsNullOrWhiteSpace(tracker.Notes) ? string.Empty : " (" + tracker.Notes + ")");
                    continue;
                }

                if (tracker.Health == PatchHealth.Disabled)
                {
                    logger?.Info("补丁状态：{0}=禁用", tracker.Name);
                }
            }
        }
    }
}
