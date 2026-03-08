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
        private readonly object runtimeLock = new object();
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly SemaphoreSlim introScanSemaphore = new SemaphoreSlim(1, 1);
        private volatile AudioFingerprintRuntime audioFingerprintRuntime;

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
            if (progress != null)
            {
                this.logger.Info($"片头扫描开始，总条目 {total}");
            }

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
                    var hasMarkersAfterDetect = HasIntroMarkers(episode);
                    this.logger.Info(
                        $"片头检测结果: status={(detected && hasMarkersAfterDetect ? "SUCCESS" : "FAIL")}, detected={detected}, hasMarkers={hasMarkersAfterDetect}, cost={stopwatch.ElapsedMilliseconds}ms, item={displayName}");

                    if (!detected)
                    {
                        this.logger.Warn($"片头检测失败: reason=DetectorReturnedFalse, item={displayName}");
                    }
                    else if (hasMarkersAfterDetect)
                    {
                        this.logger.Info($"片头检测成功: marker 已写入, item={displayName}");
                        Plugin.ChaptersJsonStore.OverWriteToFile(episode);
                    }
                    else
                    {
                        this.logger.Warn($"片头检测失败: reason=NoMarkerGenerated, item={displayName}");
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

            if (progress != null)
            {
                this.logger.Info($"扫描完成，条目数 {total}");
            }
        }

        public bool HasIntroMarkers(BaseItem item)
        {
            return Plugin.IntroSkipChapterApi.GetIntroStart(item).HasValue ||
                   Plugin.IntroSkipChapterApi.GetIntroEnd(item).HasValue;
        }

        public bool QueueEpisodeScan(Episode episode, string source)
        {
            if (episode == null)
            {
                return false;
            }

            if (HasIntroMarkers(episode))
            {
                this.logger.Info($"{source} 片头扫描跳过: {episode.Path} 已存在片头标记");
                return false;
            }

            _ = Task.Run(async () =>
            {
                var semaphoreHeld = false;
                try
                {
                    this.logger.Info($"{source} 片头扫描: 新的扫描任务 {episode.FileName ?? episode.Path}");
                    var directoryService = new DirectoryService(this.logger, Plugin.FileSystem);

                    if (HasIntroMarkers(episode))
                    {
                        this.logger.Info($"{source} 片头扫描跳过: {episode.FileName ?? episode.Path} 已存在片头标记");
                        return;
                    }

                    episode = await Plugin.MediaInfoService
                        .PrepareEpisodeForIntroScanAsync(episode, directoryService, source + "Pre")
                        .ConfigureAwait(false);
                    if (episode == null)
                    {
                        return;
                    }

                    this.logger.Info($"{source} 片头扫描: 预提取完成，加入扫描队列 {episode.Path} InternalId: {episode.InternalId}");

                    await introScanSemaphore.WaitAsync().ConfigureAwait(false);
                    semaphoreHeld = true;

                    this.logger.Info($"{source} 片头扫描: 开始片头检测 {episode.FileName ?? episode.Path} InternalId: {episode.InternalId}");
                    await ScanEpisodesAsync(new List<Episode> { episode }, CancellationToken.None, null)
                        .ConfigureAwait(false);

                    if (!HasIntroMarkers(episode))
                    {
                        this.logger.Info($"{source} 片头扫描: 未生成标记，2 分钟后重试 1 次");
                        if (semaphoreHeld)
                        {
                            introScanSemaphore.Release();
                            semaphoreHeld = false;
                        }

                        await Task.Delay(TimeSpan.FromMinutes(2), CancellationToken.None)
                            .ConfigureAwait(false);

                        episode = await Plugin.MediaInfoService
                            .PrepareEpisodeForIntroScanAsync(episode, directoryService, source + "RetryPre")
                            .ConfigureAwait(false);
                        if (episode == null)
                        {
                            return;
                        }

                        await introScanSemaphore.WaitAsync().ConfigureAwait(false);
                        semaphoreHeld = true;
                        await ScanEpisodesAsync(new List<Episode> { episode }, CancellationToken.None, null)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    this.logger.Error($"{source} 片头扫描任务异常");
                    this.logger.Error(ex.Message);
                    this.logger.Debug(ex.StackTrace);
                }
                finally
                {
                    if (semaphoreHeld)
                    {
                        introScanSemaphore.Release();
                    }
                }
            });

            return true;
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

                var createTitleFingerprintAsync = PatchMethodResolver.Resolve(
                    managerType,
                    providersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "audiofingerprintmanager-createtitlefingerprint-async",
                        MethodName = "CreateTitleFingerprint",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) }
                    },
                    this.logger,
                    "IntroScanService.CreateTitleFingerprint");
                var isIntroDetectionSupported = PatchMethodResolver.Resolve(
                    managerType,
                    providersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "audiofingerprintmanager-isintrodetectionsupported",
                        MethodName = "IsIntroDetectionSupported",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(Episode), typeof(LibraryOptions) }
                    },
                    this.logger,
                    "IntroScanService.IsIntroDetectionSupported");
                var getAllFingerprintFilesForSeason = PatchMethodResolver.Resolve(
                    managerType,
                    providersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "audiofingerprintmanager-getallfingerprintfilesforseason",
                        MethodName = "GetAllFingerprintFilesForSeason",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(Season), typeof(Episode[]), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) }
                    },
                    this.logger,
                    "IntroScanService.GetAllFingerprintFilesForSeason");
                var updateSequencesForSeason = PatchMethodResolver.Resolve(
                    managerType,
                    providersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "audiofingerprintmanager-updatesequencesforseason",
                        MethodName = "UpdateSequencesForSeason",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(Season), seasonFingerprintInfoType, typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) }
                    },
                    this.logger,
                    "IntroScanService.UpdateSequencesForSeason");

                if (isIntroDetectionSupported == null ||
                    createTitleFingerprintAsync == null ||
                    getAllFingerprintFilesForSeason == null ||
                    updateSequencesForSeason == null)
                {
                    this.logger.Warn("AudioFingerprintManager 关键方法缺失");
                    return null;
                }

                audioFingerprintRuntime = new AudioFingerprintRuntime(
                    managerType,
                    seasonFingerprintInfoType,
                    isIntroDetectionSupported,
                    createTitleFingerprintAsync,
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

            var getServiceMethod = appHost.GetType().GetMethod("GetService", new[] { typeof(Type) });
            if (getServiceMethod != null)
            {
                var service = getServiceMethod.Invoke(appHost, new object[] { serviceType });
                if (service != null)
                {
                    this.logger.Debug($"服务解析 {serviceType.FullName}: GetService(Type) 成功");
                    return service;
                }
            }
            else
            {
                this.logger.Debug($"服务解析提示 {serviceType.FullName}: AppHost 未公开 GetService(Type)，跳过");
            }

            var resolvedByAppHost = TryResolveViaAppHost(appHost, serviceType);
            if (resolvedByAppHost != null)
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: AppHost.Resolve<T>() 成功");
                return resolvedByAppHost;
            }

            this.logger.Debug($"服务解析失败 {serviceType.FullName}: GetService(Type) 与 AppHost.Resolve<T>() 均返回空");
            return null;
        }

        private object TryResolveViaAppHost(object appHost, Type serviceType)
        {
            var resolved = TryInvokeGenericServiceResolver(appHost, serviceType, "Resolve");
            if (resolved != null)
            {
                return resolved;
            }

            resolved = TryInvokeGenericServiceResolver(appHost, serviceType, "TryResolve");
            if (resolved != null)
            {
                return resolved;
            }

            resolved = TryInvokeGenericServiceResolver(appHost, serviceType, "GetExports", false);
            if (resolved != null)
            {
                return resolved;
            }

            return TryInvokeGenericServiceResolver(appHost, serviceType, "GetExports", true);
        }

        private object TryInvokeGenericServiceResolver(object appHost, Type serviceType, string methodName, params object[] args)
        {
            try
            {
                var resolver = appHost.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, methodName, StringComparison.Ordinal) &&
                        m.IsGenericMethodDefinition &&
                        m.GetGenericArguments().Length == 1 &&
                        m.GetParameters().Length == (args?.Length ?? 0));
                if (resolver == null)
                {
                    return null;
                }

                var result = resolver.MakeGenericMethod(serviceType).Invoke(appHost, args);
                return UnwrapServiceResult(result);
            }
            catch (Exception ex)
            {
                this.logger.Debug($"服务解析失败 {serviceType.FullName}: AppHost.{methodName}<T>() 异常: {ex.Message}");
                return null;
            }
        }

        private static object UnwrapServiceResult(object result)
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

            var supportedArgs = new object[] { episode, libraryOptions };
            LogInvocation(runtime.IsIntroDetectionSupported, supportedArgs);
            var supportedResult = await InvokeWithResultAsync(detector, runtime.IsIntroDetectionSupported, supportedArgs).ConfigureAwait(false);
            if (supportedResult is bool supported && !supported)
            {
                this.logger.Debug("AudioFingerprintManager.IsIntroDetectionSupported 返回 false");
                return false;
            }
            this.logger.Debug($"IsIntroDetectionSupported result: {supportedResult ?? "null"}");

            this.logger.Debug("触发 CreateTitleFingerprint 生成指纹");
            var fingerprintArgs = new object[] { episode, libraryOptions, directoryService, cancellationToken };
            LogInvocation(runtime.CreateTitleFingerprintAsync, fingerprintArgs);
            await InvokeWithResultAsync(detector, runtime.CreateTitleFingerprintAsync, fingerprintArgs).ConfigureAwait(false);

            var season = TryGetSeason(episode);
            if (season == null)
            {
                this.logger.Debug("无法获取 Season，跳过 UpdateSequencesForSeason");
                return true;
            }

            this.logger.Debug($"Season resolved: {season.Name} (id={season.InternalId})");
            var seasonEpisodes = FetchSeasonEpisodes(season);
            this.logger.Debug($"Season episodes loaded: count={seasonEpisodes.Length}");

            this.logger.Debug("触发 GetAllFingerprintFilesForSeason 收集指纹");
            var getArgs = new object[] { season, seasonEpisodes, libraryOptions, directoryService, cancellationToken };
            LogInvocation(runtime.GetAllFingerprintFilesForSeason, getArgs);
            var seasonFingerprintInfo = await InvokeWithResultAsync(detector, runtime.GetAllFingerprintFilesForSeason, getArgs).ConfigureAwait(false);
            this.logger.Debug($"GetAllFingerprintFilesForSeason result type: {seasonFingerprintInfo?.GetType().FullName ?? "null"}");

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
                MethodInfo getAllFingerprintFilesForSeason,
                MethodInfo updateSequencesForSeason)
            {
                ManagerType = managerType;
                SeasonFingerprintInfoType = seasonFingerprintInfoType;
                IsIntroDetectionSupported = isIntroDetectionSupported;
                CreateTitleFingerprintAsync = createTitleFingerprintAsync;
                GetAllFingerprintFilesForSeason = getAllFingerprintFilesForSeason;
                UpdateSequencesForSeason = updateSequencesForSeason;
            }

            public Type ManagerType { get; }

            public Type SeasonFingerprintInfoType { get; }

            public MethodInfo IsIntroDetectionSupported { get; }

            public MethodInfo CreateTitleFingerprintAsync { get; }

            public MethodInfo GetAllFingerprintFilesForSeason { get; }

            public MethodInfo UpdateSequencesForSeason { get; }
        }
    }
}
