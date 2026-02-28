using System;
using System.Collections.Generic;
using System.Linq;
using MediaInfoKeeper.Configuration;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
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

                Register("FfprobeGuard");
                FfprobeGuard.Initialize(logger, safeOptions.MainPage.DisableSystemFfprobe);
                UpdateTracker("FfprobeGuard", safeOptions.MainPage.DisableSystemFfprobe, FfprobeGuard.IsReady, null);

                Register("MetadataProvidersWatcher");
                MetadataProvidersWatcher.Initialize(logger, safeOptions.MetaData.EnableMetadataProvidersWatcher);
                UpdateTracker("MetadataProvidersWatcher", safeOptions.MetaData.EnableMetadataProvidersWatcher,
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

                Register("UnlockIntroSkip");
                IntroUnlock.Initialize(logger, safeOptions.IntroSkip.UnlockIntroSkip);
                IntroUnlock.Configure(safeOptions);
                UpdateTracker("UnlockIntroSkip", safeOptions.IntroSkip.UnlockIntroSkip, IntroUnlock.IsReady, null);

                Register("IntroMarkerProtect");
                IntroMarkerProtect.Initialize(logger, safeOptions.IntroSkip.ProtectIntroMarkers);
                UpdateTracker("IntroMarkerProtect", safeOptions.IntroSkip.ProtectIntroMarkers, IntroMarkerProtect.IsReady, null);

                Register("ProxyServer");
                ProxyServer.Initialize(logger, safeOptions.Proxy.EnableProxyServer);
                UpdateTracker(
                    "ProxyServer",
                    safeOptions.Proxy.EnableProxyServer,
                    ProxyServer.IsReady,
                    BuildProxyNotes(),
                    false);

                Register("EnhanceChineseSearch");
                EnhanceChineseSearch.UpdateSearchScope(safeOptions.EnhanceChineseSearch.SearchScope);
                EnhanceChineseSearch.Initialize(logger, safeOptions.EnhanceChineseSearch);
                UpdateTracker(
                    "EnhanceChineseSearch",
                    safeOptions.EnhanceChineseSearch.EnhanceChineseSearch || safeOptions.EnhanceChineseSearch.EnhanceChineseSearchRestore,
                    EnhanceChineseSearch.IsReady,
                    null);

                LogTrackerSummary();
            }
        }

        public static void Configure(PluginConfiguration options)
        {
            var safeOptions = EnsureOptions(options);

            FfprobeGuard.Configure(safeOptions.MainPage.DisableSystemFfprobe);
            MetadataProvidersWatcher.Configure(safeOptions.MetaData.EnableMetadataProvidersWatcher);
            IntroUnlock.Configure(safeOptions);
            ProxyServer.Configure(safeOptions.Proxy.EnableProxyServer);
            EnhanceChineseSearch.UpdateSearchScope(safeOptions.EnhanceChineseSearch.SearchScope);
            EnhanceChineseSearch.Configure(safeOptions.EnhanceChineseSearch);
            MovieDbTitle.Configure(safeOptions.MetaData.EnableAlternativeTitleFallback);
            TvdbTitle.Configure(safeOptions.MetaData.EnableTvdbFallback);
            IntroMarkerProtect.Configure(safeOptions.IntroSkip.ProtectIntroMarkers);

            UpdateTracker("FfprobeGuard", safeOptions.MainPage.DisableSystemFfprobe, FfprobeGuard.IsReady, null);
            UpdateTracker("MetadataProvidersWatcher", safeOptions.MetaData.EnableMetadataProvidersWatcher,
                MetadataProvidersWatcher.IsReady, null);
            UpdateTracker("MovieDbTitle", safeOptions.MetaData.EnableAlternativeTitleFallback, MovieDbTitle.IsReady,
                MovieDbTitle.IsWaiting ? "waiting for MovieDb assembly" : null, MovieDbTitle.IsWaiting);
            UpdateTracker("TvdbTitle", safeOptions.MetaData.EnableTvdbFallback, TvdbTitle.IsReady,
                TvdbTitle.IsWaiting ? "waiting for Tvdb assembly" : null, TvdbTitle.IsWaiting);
            UpdateTracker("UnlockIntroSkip", safeOptions.IntroSkip.UnlockIntroSkip, IntroUnlock.IsReady, null);
            UpdateTracker("IntroMarkerProtect", safeOptions.IntroSkip.ProtectIntroMarkers, IntroMarkerProtect.IsReady, null);
            UpdateTracker("ProxyServer", safeOptions.Proxy.EnableProxyServer, ProxyServer.IsReady,
                BuildProxyNotes(), false);
            UpdateTracker(
                "EnhanceChineseSearch",
                safeOptions.EnhanceChineseSearch.EnhanceChineseSearch || safeOptions.EnhanceChineseSearch.EnhanceChineseSearchRestore,
                EnhanceChineseSearch.IsReady,
                null);

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
            safe.Proxy ??= new ProxyOptions();
            safe.EnhanceChineseSearch ??= new EnhanceChineseSearchOptions();
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
            if (!ProxyServer.IsReady)
            {
                return "CreateHttpClientHandler 未命中";
            }

            return ProxyServer.IsMovieDbHookReady ? null : "MovieDb hook not ready";
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
