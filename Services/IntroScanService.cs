using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Patch;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public class IntroScanService
    {
        private static readonly string[] ResolverNames = { "Resolve", "GetService", "TryResolve", "GetInstance", "GetExport", "GetExports" };
        private readonly object runtimeLock = new object();
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private AudioFingerprintRuntime audioFingerprintRuntime;

        public IntroScanService(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }

        public async Task ScanEpisodesAsync(
            IReadOnlyList<Episode> episodes,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            if (episodes == null || episodes.Count == 0)
            {
                progress?.Report(100.0);
                this.logger.Info("扫描完成，条目数 0");
                return;
            }

            var total = episodes.Count;
            var current = 0;
            this.logger.Info($"片头扫描开始，总条目 {total}");

            foreach (var episode in episodes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info("扫描已取消");
                    return;
                }

                var displayName = episode.Path ?? episode.Name;
                this.logger.Info($"扫描进度 {current + 1}/{total}: {displayName} (id={episode.InternalId}, parent={episode.ParentId})");
                if (HasIntroMarkers(episode))
                {
                    this.logger.Info($"跳过 已存在片头标记: {displayName}");
                    current++;
                    progress?.Report(current / (double)total * 100);
                    continue;
                }

                try
                {
                    this.logger.Info($"开始片头检测: {displayName}");
                    var stopwatch = Stopwatch.StartNew();
                    var detected = await TryDetectIntroAsync(episode, cancellationToken).ConfigureAwait(false);
                    stopwatch.Stop();
                    this.logger.Info($"片头检测返回: detected={detected}, cost={stopwatch.ElapsedMilliseconds}ms, item={displayName}");

                    if (!detected)
                    {
                        this.logger.Warn($"片头检测未执行或未命中: {displayName}");
                    }
                    else if (HasIntroMarkers(episode))
                    {
                        this.logger.Info($"片头检测完成: {displayName}");
                        _ = Plugin.MediaInfoService.SerializeMediaInfo(
                            episode.InternalId,
                            new DirectoryService(this.logger, Plugin.FileSystem),
                            true,
                            "IntroScan Persist");
                    }
                    else
                    {
                        this.logger.Warn($"片头检测完成但未生成标记: {displayName}");
                    }
                }
                catch (OperationCanceledException)
                {
                    this.logger.Info($"扫描已取消 {displayName}");
                    return;
                }
                catch (Exception e)
                {
                    this.logger.Error($"片头检测失败: {displayName}");
                    this.logger.Error(e.Message);
                    this.logger.Debug(e.StackTrace);
                }

                current++;
                progress?.Report(current / (double)total * 100);
            }

            this.logger.Info($"扫描完成，条目数 {total}");
        }

        public bool HasIntroMarkers(BaseItem item)
        {
            return Plugin.IntroSkipChapterApi.GetIntroStart(item).HasValue ||
                   Plugin.IntroSkipChapterApi.GetIntroEnd(item).HasValue;
        }

        public async Task<bool> TryDetectIntroAsync(Episode episode, CancellationToken cancellationToken)
        {
            this.logger.Debug($"TryDetectIntroAsync: item={episode?.Path ?? episode?.Name}, id={episode?.InternalId}");
            var detector = TryResolveAudioFingerprintManager();
            if (detector != null)
            {
                if (await TryRunAudioFingerprintWorkflowAsync(detector, episode, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                this.logger.Debug($"AudioFingerprintManager 执行失败: {detector.GetType().FullName}");
            }
            else
            {
                this.logger.Debug("未能解析 AudioFingerprintManager");
            }

            this.logger.Info("探测失败，可能尚未获取strm文件内容，请稍后再试。");
            return false;
        }

        private object TryResolveAudioFingerprintManager()
        {
            try
            {
                var runtime = EnsureAudioFingerprintRuntime();
                if (runtime?.ManagerType == null)
                {
                    this.logger.Warn("未找到 AudioFingerprintManager 类型");
                    return null;
                }

                var detector = TryResolveService(runtime.ManagerType);
                if (detector == null)
                {
                    this.logger.Warn($"AudioFingerprintManager 服务解析失败: {runtime.ManagerType.FullName}");
                }

                return detector;
            }
            catch (Exception ex)
            {
                this.logger.Warn($"片头检测服务加载失败: {ex.Message}");
                return null;
            }
        }

        private AudioFingerprintRuntime EnsureAudioFingerprintRuntime()
        {
            if (audioFingerprintRuntime != null)
            {
                return audioFingerprintRuntime;
            }

            lock (runtimeLock)
            {
                if (audioFingerprintRuntime != null)
                {
                    return audioFingerprintRuntime;
                }

                var providersAssembly = Assembly.Load("Emby.Providers");
                if (providersAssembly == null)
                {
                    return null;
                }

                var providersVersion = providersAssembly.GetName().Version;
                var managerType = providersAssembly.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                var seasonFingerprintInfoType = providersAssembly.GetType("Emby.Providers.Markers.SeasonFingerprintInfo");
                if (managerType == null || seasonFingerprintInfoType == null)
                {
                    this.logger.Warn("AudioFingerprintManager 关键类型缺失");
                    return null;
                }

                var createTitleFingerprintAsync = VersionedMethodResolver.Resolve(
                    managerType,
                    providersVersion,
                    new[]
                    {
                        new MethodSignatureProfile
                        {
                            Name = "audiofingerprintmanager-createtitlefingerprint-async",
                            MethodName = "CreateTitleFingerprint",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            ParameterTypes = new[] { typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) }
                        }
                    },
                    this.logger,
                    "IntroScanService.CreateTitleFingerprint");
                var createTitleFingerprintSync = VersionedMethodResolver.Resolve(
                    managerType,
                    providersVersion,
                    new[]
                    {
                        new MethodSignatureProfile
                        {
                            Name = "audiofingerprintmanager-createtitlefingerprint-sync",
                            MethodName = "CreateTitleFingerprint",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            ParameterTypes = new[] { typeof(Episode), typeof(LibraryOptions), typeof(string), typeof(CancellationToken) }
                        }
                    },
                    this.logger,
                    "IntroScanService.CreateTitleFingerprintSync");
                var isIntroDetectionSupported = VersionedMethodResolver.Resolve(
                    managerType,
                    providersVersion,
                    new[]
                    {
                        new MethodSignatureProfile
                        {
                            Name = "audiofingerprintmanager-isintrodetectionsupported",
                            MethodName = "IsIntroDetectionSupported",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            ParameterTypes = new[] { typeof(Episode), typeof(LibraryOptions) }
                        }
                    },
                    this.logger,
                    "IntroScanService.IsIntroDetectionSupported");
                var getAllFingerprintFilesForSeason = VersionedMethodResolver.Resolve(
                    managerType,
                    providersVersion,
                    new[]
                    {
                        new MethodSignatureProfile
                        {
                            Name = "audiofingerprintmanager-getallfingerprintfilesforseason",
                            MethodName = "GetAllFingerprintFilesForSeason",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            ParameterTypes = new[] { typeof(Season), typeof(Episode[]), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) }
                        }
                    },
                    this.logger,
                    "IntroScanService.GetAllFingerprintFilesForSeason");
                var updateSequencesForSeason = VersionedMethodResolver.Resolve(
                    managerType,
                    providersVersion,
                    new[]
                    {
                        new MethodSignatureProfile
                        {
                            Name = "audiofingerprintmanager-updatesequencesforseason",
                            MethodName = "UpdateSequencesForSeason",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            ParameterTypes = new[] { typeof(Season), seasonFingerprintInfoType, typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) }
                        }
                    },
                    this.logger,
                    "IntroScanService.UpdateSequencesForSeason");

                if (isIntroDetectionSupported == null || updateSequencesForSeason == null ||
                    (createTitleFingerprintAsync == null && createTitleFingerprintSync == null))
                {
                    this.logger.Warn("AudioFingerprintManager 关键方法缺失");
                    return null;
                }

                audioFingerprintRuntime = new AudioFingerprintRuntime(
                    managerType,
                    seasonFingerprintInfoType,
                    isIntroDetectionSupported,
                    createTitleFingerprintAsync,
                    createTitleFingerprintSync,
                    getAllFingerprintFilesForSeason,
                    updateSequencesForSeason);
                return audioFingerprintRuntime;
            }
        }

        private object TryResolveService(Type serviceType)
        {
            var appHost = Plugin.Instance.AppHost;
            if (appHost == null)
            {
                this.logger.Debug($"服务解析失败 {serviceType.FullName}: AppHost 为空");
                return null;
            }

            if (appHost is IServiceProvider serviceProvider)
            {
                var service = serviceProvider.GetService(serviceType);
                if (service != null)
                {
                    this.logger.Debug($"服务解析 {serviceType.FullName}: IServiceProvider.GetService 成功");
                    return service;
                }
            }

            var hostType = appHost.GetType();
            this.logger.Debug($"服务解析 {serviceType.FullName}: AppHost 类型 {hostType.FullName}");

            var hostMethods = hostType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var resolverName in ResolverNames)
            {
                var genericResolver = hostMethods
                    .Where(m => string.Equals(m.Name, resolverName, StringComparison.Ordinal) &&
                                m.IsGenericMethodDefinition &&
                                m.GetGenericArguments().Length == 1 &&
                                m.GetParameters().Length == 0)
                    .OrderBy(m => m.IsPublic ? 0 : 1)
                    .FirstOrDefault();
                if (genericResolver == null)
                {
                    continue;
                }

                try
                {
                    var result = genericResolver.MakeGenericMethod(serviceType).Invoke(appHost, null);
                    var resolved = UnwrapExports(result);
                    if (resolved != null)
                    {
                        this.logger.Debug($"服务解析 {serviceType.FullName}: AppHost.{genericResolver.Name}<T>() 成功");
                        return resolved;
                    }
                }
                catch (Exception ex)
                {
                    this.logger.Debug($"服务解析 {serviceType.FullName}: AppHost.{genericResolver.Name}<T>() 失败: {ex.Message}");
                }
            }

            foreach (var resolverName in ResolverNames)
            {
                var typeResolver = hostMethods
                    .Where(m => string.Equals(m.Name, resolverName, StringComparison.Ordinal))
                    .Where(m =>
                    {
                        var p = m.GetParameters();
                        return p.Length == 1 && p[0].ParameterType == typeof(Type);
                    })
                    .OrderBy(m => m.IsPublic ? 0 : 1)
                    .FirstOrDefault();
                if (typeResolver == null)
                {
                    continue;
                }

                try
                {
                    var result = typeResolver.Invoke(appHost, new object[] { serviceType });
                    var resolved = UnwrapExports(result);
                    if (resolved != null)
                    {
                        this.logger.Debug($"服务解析 {serviceType.FullName}: AppHost.{typeResolver.Name}(Type) 成功");
                        return resolved;
                    }
                }
                catch (Exception ex)
                {
                    this.logger.Debug($"服务解析 {serviceType.FullName}: AppHost.{typeResolver.Name}(Type) 失败: {ex.Message}");
                }
            }

            this.logger.Debug($"服务解析 {serviceType.FullName}: 未找到可用解析器");
            return null;
        }

        private static object UnwrapExports(object result)
        {
            if (result == null)
            {
                return null;
            }

            if (result is IEnumerable enumerable && result is not string)
            {
                foreach (var item in enumerable)
                {
                    return item;
                }

                return null;
            }

            return result;
        }

        private async Task<bool> TryRunAudioFingerprintWorkflowAsync(
            object detector,
            Episode episode,
            CancellationToken cancellationToken)
        {
            this.logger.Debug($"AudioFingerprint workflow start: detector={detector.GetType().FullName}, item={episode?.Path ?? episode?.Name}, id={episode?.InternalId}");
            var runtime = EnsureAudioFingerprintRuntime();
            if (runtime == null)
            {
                this.logger.Warn("AudioFingerprint workflow未完成方法初始化");
                return false;
            }

            var directoryService = new DirectoryService(this.logger, Plugin.FileSystem);
            var libraryOptions = this.libraryManager.GetLibraryOptions(episode);
            var hasLibraryOptions = libraryOptions != null;
            this.logger.Debug($"LibraryOptions loaded: null={!hasLibraryOptions}");

            if (runtime.IsIntroDetectionSupported != null)
            {
                var supportedArgs = new object[] { episode, libraryOptions };
                LogInvocation(runtime.IsIntroDetectionSupported, supportedArgs);
                var supportedResult = await InvokeWithResultAsync(detector, runtime.IsIntroDetectionSupported, supportedArgs).ConfigureAwait(false);
                if (supportedResult is bool supported && !supported)
                {
                    this.logger.Debug("AudioFingerprintManager.IsIntroDetectionSupported 返回 false");
                    return false;
                }

                this.logger.Debug($"IsIntroDetectionSupported result: {supportedResult ?? "null"}");
            }

            if (runtime.CreateTitleFingerprintAsync != null)
            {
                this.logger.Debug("触发 CreateTitleFingerprint 生成指纹");
                var fingerprintArgs = new object[] { episode, libraryOptions, directoryService, cancellationToken };
                LogInvocation(runtime.CreateTitleFingerprintAsync, fingerprintArgs);
                await InvokeWithResultAsync(detector, runtime.CreateTitleFingerprintAsync, fingerprintArgs).ConfigureAwait(false);
            }
            else if (runtime.CreateTitleFingerprintSync != null)
            {
                this.logger.Debug("触发 CreateTitleFingerprint(legacy) 生成指纹");
                var fingerprintArgs = new object[] { episode, libraryOptions, null, cancellationToken };
                LogInvocation(runtime.CreateTitleFingerprintSync, fingerprintArgs);
                await InvokeWithResultAsync(detector, runtime.CreateTitleFingerprintSync, fingerprintArgs).ConfigureAwait(false);
            }

            if (runtime.UpdateSequencesForSeason == null)
            {
                this.logger.Debug($"AudioFingerprint workflow完成（仅生成指纹）: item={episode?.Path ?? episode?.Name}");
                return runtime.CreateTitleFingerprintAsync != null || runtime.CreateTitleFingerprintSync != null;
            }

            var season = TryGetSeason(episode);
            if (season == null)
            {
                this.logger.Debug("无法获取 Season，跳过 UpdateSequencesForSeason");
                return runtime.CreateTitleFingerprintAsync != null || runtime.CreateTitleFingerprintSync != null;
            }

            this.logger.Debug($"Season resolved: {season.Name} (id={season.InternalId})");
            var seasonEpisodes = FetchSeasonEpisodes(season);
            this.logger.Debug($"Season episodes loaded: count={seasonEpisodes.Length}");

            object seasonFingerprintInfo = null;
            if (runtime.GetAllFingerprintFilesForSeason != null)
            {
                this.logger.Debug("触发 GetAllFingerprintFilesForSeason 收集指纹");
                var getArgs = new object[] { season, seasonEpisodes, libraryOptions, directoryService, cancellationToken };
                LogInvocation(runtime.GetAllFingerprintFilesForSeason, getArgs);
                seasonFingerprintInfo = await InvokeWithResultAsync(detector, runtime.GetAllFingerprintFilesForSeason, getArgs).ConfigureAwait(false);
                this.logger.Debug($"GetAllFingerprintFilesForSeason result type: {seasonFingerprintInfo?.GetType().FullName ?? "null"}");
            }

            this.logger.Debug("触发 UpdateSequencesForSeason 生成片头序列");
            if (seasonFingerprintInfo == null && runtime.SeasonFingerprintInfoType != null && runtime.SeasonFingerprintInfoType.IsClass)
            {
                this.logger.Debug("SeasonFingerprintInfo 为空，跳过 UpdateSequencesForSeason");
                return false;
            }

            var updateArgs = new[] { season, seasonFingerprintInfo, (object)episode, libraryOptions, directoryService, cancellationToken };
            LogInvocation(runtime.UpdateSequencesForSeason, updateArgs);
            await InvokeWithResultAsync(detector, runtime.UpdateSequencesForSeason, updateArgs).ConfigureAwait(false);
            this.logger.Debug($"AudioFingerprint workflow完成: item={episode?.Path ?? episode?.Name}");

            return true;
        }

        private void LogInvocation(MethodInfo method, object[] args)
        {
            if (method == null)
            {
                return;
            }

            var parameters = method.GetParameters();
            var parts = new List<string>(parameters.Length);
            for (var i = 0; i < parameters.Length; i++)
            {
                var value = i < args.Length ? args[i] : null;
                var valueType = value?.GetType().Name ?? "null";
                parts.Add($"{parameters[i].Name}:{parameters[i].ParameterType.Name}={valueType}");
            }

            this.logger.Debug($"调用 {method.DeclaringType?.Name}.{method.Name} 参数: {string.Join(", ", parts)}");
        }

        private static async Task<object> InvokeWithResultAsync(object target, MethodInfo method, object[] args)
        {
            var result = method.Invoke(target, args);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                {
                    return taskType.GetProperty("Result")?.GetValue(task);
                }

                return null;
            }

            return result;
        }

        private Season TryGetSeason(Episode episode)
        {
            if (episode?.Season != null)
            {
                return episode.Season;
            }

            if (episode == null)
            {
                return null;
            }

            return this.libraryManager.GetItemById(episode.ParentId) as Season;
        }

        private Episode[] FetchSeasonEpisodes(Season season)
        {
            if (season == null)
            {
                return Array.Empty<Episode>();
            }

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video },
                ParentIds = new[] { season.InternalId }
            };

            return this.libraryManager.GetItemList(query).OfType<Episode>().ToArray();
        }

        private sealed class AudioFingerprintRuntime
        {
            public AudioFingerprintRuntime(
                Type managerType,
                Type seasonFingerprintInfoType,
                MethodInfo isIntroDetectionSupported,
                MethodInfo createTitleFingerprintAsync,
                MethodInfo createTitleFingerprintSync,
                MethodInfo getAllFingerprintFilesForSeason,
                MethodInfo updateSequencesForSeason)
            {
                ManagerType = managerType;
                SeasonFingerprintInfoType = seasonFingerprintInfoType;
                IsIntroDetectionSupported = isIntroDetectionSupported;
                CreateTitleFingerprintAsync = createTitleFingerprintAsync;
                CreateTitleFingerprintSync = createTitleFingerprintSync;
                GetAllFingerprintFilesForSeason = getAllFingerprintFilesForSeason;
                UpdateSequencesForSeason = updateSequencesForSeason;
            }

            public Type ManagerType { get; }

            public Type SeasonFingerprintInfoType { get; }

            public MethodInfo IsIntroDetectionSupported { get; }

            public MethodInfo CreateTitleFingerprintAsync { get; }

            public MethodInfo CreateTitleFingerprintSync { get; }

            public MethodInfo GetAllFingerprintFilesForSeason { get; }

            public MethodInfo UpdateSequencesForSeason { get; }
        }
    }
}
