using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Patch;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public class IntroScanService
    {
        private readonly object runtimeLock = new object();
        private readonly object introScanGateLock = new object();
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IFileSystem fileSystem;
        private SemaphoreSlim introScanSemaphore;
        private int configuredIntroScanConcurrency;
        private volatile AudioFingerprintRuntime audioFingerprintRuntime;
        private volatile AppHostResolverRuntime appHostResolverRuntime;

        public IntroScanService(
            ILogManager logManager,
            ILibraryManager libraryManager,
            IFileSystem fileSystem)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
            this.fileSystem = fileSystem;
        }

        /// <summary>按配置并发度扫描一批剧集的片头标记。</summary>
        public async Task ScanEpisodesAsync(IReadOnlyList<Episode> episodes, CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (episodes == null || episodes.Count == 0)
            {
                progress?.Report(100.0);
                this.logger.Info("扫描完成，条目数 0");
                return;
            }

            var total = episodes.Count;
            if (progress != null)
            {
                this.logger.Info($"片头扫描开始，总条目 {total}");
            }

            var progressCounter = new ProgressCounter();
            var tasks = episodes.Select(episode => ScanEpisodeWithConcurrencyGateAsync(episode, cancellationToken, total, progress, progressCounter))
                .ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (progress != null)
            {
                this.logger.Info($"扫描完成，条目数 {total}");
            }
        }

        /// <summary>判断条目当前是否已经存在片头标记。</summary>
        public bool HasIntroMarkers(BaseItem item)
        {
            return Plugin.IntroSkipChapterApi.GetIntroStart(item).HasValue ||
                   Plugin.IntroSkipChapterApi.GetIntroEnd(item).HasValue;
        }

        /// <summary>将单个剧集加入后台片头扫描队列，并在失败时延迟重试一次。</summary>
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
                SemaphoreSlim introScanGate = null;
                try
                {
                    this.logger.Debug($"{source} 片头扫描: 新的扫描任务 {episode.FileName ?? episode.Path}");

                    if (HasIntroMarkers(episode))
                    {
                        this.logger.Info($"{source} 片头扫描跳过: {episode.FileName ?? episode.Path} 已存在片头标记");
                        return;
                    }

                    episode = await PrepareEpisodeForDetectionAsync(episode, source + "Pre")
                        .ConfigureAwait(false);
                    if (episode == null)
                    {
                        return;
                    }

                    this.logger.Debug($"{source} 片头扫描: 预提取完成，加入扫描队列 {episode.Path} InternalId: {episode.InternalId}");

                    introScanGate = GetScanConcurrencyGate();
                    await introScanGate.WaitAsync().ConfigureAwait(false);
                    semaphoreHeld = true;

                    this.logger.Debug($"{source} 片头扫描: 开始片头检测 {episode.FileName ?? episode.Path} InternalId: {episode.InternalId}");
                    await ScanEpisodeCoreAsync(episode, CancellationToken.None, null, null, null).ConfigureAwait(false);

                    if (!HasIntroMarkers(episode))
                    {
                        this.logger.Info($"{source} 片头扫描: 未生成标记，2 分钟后重试 1 次");
                        if (semaphoreHeld)
                        {
                            introScanGate.Release();
                            semaphoreHeld = false;
                        }

                        await Task.Delay(TimeSpan.FromMinutes(2), CancellationToken.None)
                            .ConfigureAwait(false);

                        episode = await PrepareEpisodeForDetectionAsync(episode, source + "RetryPre")
                            .ConfigureAwait(false);
                        if (episode == null)
                        {
                            return;
                        }

                        introScanGate = GetScanConcurrencyGate();
                        await introScanGate.WaitAsync().ConfigureAwait(false);
                        semaphoreHeld = true;
                        await ScanEpisodeCoreAsync(episode, CancellationToken.None, null, null, null).ConfigureAwait(false);
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
                        introScanGate?.Release();
                    }
                }
            });

            return true;
        }

        /// <summary>为片头探测准备剧集状态，确保挂载可用、MediaInfo 可恢复且存在音频流。</summary>
        private async Task<Episode> PrepareEpisodeForDetectionAsync(Episode episode, string source)
        {
            if (episode == null)
            {
                return null;
            }

            var workEpisode = this.libraryManager.GetItemById(episode.InternalId) as Episode ?? episode;

            if (LibraryService.IsFileShortcut(workEpisode.Path ?? workEpisode.FileName))
            {
                var mountedPath = await Plugin.LibraryService.GetStrmMountPathAsync(workEpisode.Path).ConfigureAwait(false);
                if (string.IsNullOrEmpty(mountedPath))
                {
                    this.logger.Warn($"{source} 片头扫描预提取: {workEpisode.FileName} InternalId: {workEpisode.InternalId} 挂载路径解析失败，跳过扫描");
                    return null;
                }
            }

            if (!Plugin.MediaInfoService.HasMediaInfo(workEpisode))
            {
                this.logger.Info($"{source} 片头扫描预提取: {workEpisode.FileName} 无 MediaInfo，尝试从 JSON 恢复");
                var restoreResult = Plugin.MediaSourceInfoStore.ApplyToItem(workEpisode);
                Plugin.ChaptersStore.ApplyToItem(workEpisode);
                var restoreSucceeded =
                    restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored ||
                    restoreResult == MediaInfoDocument.MediaInfoRestoreResult.AlreadyExists;

                if (!restoreSucceeded)
                {
                    this.logger.Info($"{source} 片头扫描预提取: {workEpisode.FileName} 开始提取媒体信息");
                    workEpisode = await RefreshEpisodeForDetectionAsync(workEpisode, source + " Extract")
                        .ConfigureAwait(false);
                    if (workEpisode == null)
                    {
                        return null;
                    }

                    if (Plugin.MediaInfoService.HasMediaInfo(workEpisode))
                    {
                        Plugin.MediaSourceInfoStore.OverWriteToFile(workEpisode);
                    }
                }
            }

            workEpisode = this.libraryManager.GetItemById(workEpisode.InternalId) as Episode ?? workEpisode;
            if (!Plugin.MediaInfoService.HasMediaInfo(workEpisode))
            {
                this.logger.Warn($"{source} 片头扫描预提取: {workEpisode.FileName} 提取后仍无 MediaInfo，跳过扫描");
                return null;
            }

            var hasAudioStream = workEpisode.GetMediaStreams().Any(s => s.Type == MediaStreamType.Audio);
            if (!hasAudioStream)
            {
                this.logger.Warn($"{source} 片头扫描预提取: {workEpisode.FileName} MediaInfo 存在但无音频流，跳过扫描");
                return null;
            }

            return workEpisode;
        }

        /// <summary>为片头探测执行一次最小化刷新，以便补齐 MediaInfo。</summary>
        private async Task<Episode> RefreshEpisodeForDetectionAsync(Episode episode, string source)
        {
            if (episode == null)
            {
                return null;
            }

            try
            {
                var metadataRefreshOptions = new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllMetadata = false,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllImages = false,
                    EnableThumbnailImageExtraction = false,
                    EnableSubtitleDownloading = false
                };
                var collectionFolders = this.libraryManager.GetCollectionFolders(episode).Cast<BaseItem>().ToArray();
                var libraryOptions = this.libraryManager.GetLibraryOptions(episode);
                using (FfProcessGuard.Allow())
                {
                    episode.DateLastRefreshed = new DateTimeOffset();
                    await RefreshTaskRunner.RunAsync(
                            () => Plugin.ProviderManager
                                .RefreshSingleItem(episode, metadataRefreshOptions, collectionFolders, libraryOptions, CancellationToken.None))
                        .ConfigureAwait(false);
                }

                return episode;
            }
            catch (Exception ex)
            {
                this.logger.Error($"{source} 片头扫描: 未刷新条目触发刷新失败 {episode.Path} InternalId: {episode.InternalId}");
                this.logger.Error(ex.Message);
                this.logger.Debug(ex.StackTrace);
                return null;
            }
        }

        /// <summary>在并发门控下执行单个剧集的扫描，并汇总批量扫描进度。</summary>
        private async Task ScanEpisodeWithConcurrencyGateAsync(
            Episode episode,
            CancellationToken cancellationToken,
            int total,
            IProgress<double> progress,
            ProgressCounter progressCounter)
        {
            var semaphoreHeld = false;
            SemaphoreSlim introScanGate = null;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info("扫描已取消");
                    return;
                }

                introScanGate = GetScanConcurrencyGate();
                await introScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                semaphoreHeld = true;
                await ScanEpisodeCoreAsync(episode, cancellationToken, total, progress, () => Interlocked.Increment(ref progressCounter.Completed))
                    .ConfigureAwait(false);
            }
            finally
            {
                if (semaphoreHeld)
                {
                    introScanGate?.Release();
                }
            }
        }

        /// <summary>执行单个剧集的片头探测，并在成功后持久化标记结果。</summary>
        private async Task ScanEpisodeCoreAsync(
            Episode episode,
            CancellationToken cancellationToken,
            int? total,
            IProgress<double> progress,
            Func<int> onCompleted)
        {
            var displayName = episode?.Path ?? episode?.Name;
            if (episode == null)
            {
                return;
            }

            try
            {
                if (HasIntroMarkers(episode))
                {
                    this.logger.Info($"跳过 已存在片头标记: {displayName}");
                    return;
                }

                this.logger.Debug($"开始片头检测: {displayName}");
                var stopwatch = Stopwatch.StartNew();
                var detected = await DetectIntroAsync(episode, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                var hasMarkersAfterDetect = HasIntroMarkers(episode);
                this.logger.Info(
                    $"片头检测结果: 状态={(detected && hasMarkersAfterDetect ? "成功" : "失败")}, 已检测到={(detected ? "是" : "否")}, 已写入标记={(hasMarkersAfterDetect ? "是" : "否")}, 耗时={stopwatch.ElapsedMilliseconds}ms, 条目={displayName}");

                if (!detected)
                {
                    this.logger.Warn($"片头检测失败: reason=DetectorReturnedFalse, item={displayName}");
                }
                else if (hasMarkersAfterDetect)
                {
                    this.logger.Debug($"片头检测成功: marker 已写入, item={displayName}");
                    Plugin.ChaptersStore.OverWriteToFile(episode);
                }
                else
                {
                    this.logger.Warn($"片头检测失败: reason=NoMarkerGenerated, item={displayName}");
                }
            }
            catch (OperationCanceledException)
            {
                this.logger.Info($"扫描已取消 {displayName}");
                throw;
            }
            catch (Exception e)
            {
                this.logger.Error($"片头检测失败: {displayName}");
                this.logger.Error(e.Message);
                this.logger.Debug(e.StackTrace);
            }
            finally
            {
                if (onCompleted != null && total.HasValue)
                {
                    var completed = onCompleted();
                    this.logger.Info($"扫描进度 {completed}/{total}: {displayName} (id={episode.InternalId}, parent={episode.ParentId})");
                    progress?.Report(completed / (double)total.Value * 100);
                }
            }
        }

        /// <summary>按当前配置获取片头探测并发门控实例。</summary>
        private SemaphoreSlim GetScanConcurrencyGate()
        {
            var maxConcurrent = Math.Max(1, Plugin.Instance?.Options?.IntroSkip?.IntroDetectionMaxConcurrentCount ?? 1);
            lock (introScanGateLock)
            {
                if (introScanSemaphore == null || configuredIntroScanConcurrency != maxConcurrent)
                {
                    introScanSemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
                    configuredIntroScanConcurrency = maxConcurrent;
                }

                return introScanSemaphore;
            }
        }

        /// <summary>承载批量扫描时的已完成计数，便于线程安全地汇报进度。</summary>
        private sealed class ProgressCounter
        {
            public int Completed;
        }

        /// <summary>调用 Emby 的 AudioFingerprint 流程，对单个剧集执行片头探测。</summary>
        private async Task<bool> DetectIntroAsync(Episode episode, CancellationToken cancellationToken)
        {
            this.logger.Debug($"DetectIntroAsync: item={episode?.Path ?? episode?.Name}, id={episode?.InternalId}");

            try
            {
                var runtime = GetOrCreateAudioFingerprintRuntime();
                if (runtime?.ManagerType == null)
                {
                    this.logger.Warn("未找到 AudioFingerprintManager 类型");
                }
                else
                {
                    var detector = ResolveAppHostService(runtime.ManagerType);
                    if (detector == null)
                    {
                        this.logger.Warn($"AudioFingerprintManager 服务解析失败: {runtime.ManagerType.FullName}");
                    }
                    else
                    {
                        if (await RunAudioFingerprintWorkflowAsync(detector, episode, cancellationToken).ConfigureAwait(false))
                        {
                            return true;
                        }

                        this.logger.Debug($"AudioFingerprintManager 执行失败: {detector.GetType().FullName}");
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.Warn($"片头检测服务加载失败: {ex.Message}");
            }

            this.logger.Info("探测失败，可能尚未获取strm文件内容，请稍后再试。");
            return false;
        }

        /// <summary>解析并缓存 AudioFingerprint 相关类型与方法签名。</summary>
        private AudioFingerprintRuntime GetOrCreateAudioFingerprintRuntime()
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

        /// <summary>通过 IApplicationHost 的精确泛型入口解析指定服务。</summary>
        private object ResolveAppHostService(Type serviceType)
        {
            var appHost = Plugin.Instance.AppHost;
            if (appHost == null)
            {
                this.logger.Debug($"服务解析失败 {serviceType.FullName}: AppHost 为空");
                return null;
            }

            var resolverRuntime = GetOrCreateAppHostResolverRuntime(appHost.GetType());
            if (resolverRuntime == null)
            {
                this.logger.Debug($"服务解析失败 {serviceType.FullName}: AppHost 精确解析入口缺失");
                return null;
            }

            var service = InvokeGenericResolver(appHost, resolverRuntime.Resolve, serviceType);
            if (service != null)
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: IApplicationHost.Resolve<T>() 成功");
                return service;
            }

            service = InvokeGenericResolver(appHost, resolverRuntime.TryResolve, serviceType);
            if (service != null)
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: IApplicationHost.TryResolve<T>() 成功");
                return service;
            }

            service = InvokeGenericResolver(appHost, resolverRuntime.GetExports, serviceType, false);
            if (service != null)
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: IApplicationHost.GetExports<T>(false) 成功");
                return service;
            }

            service = InvokeGenericResolver(appHost, resolverRuntime.GetExports, serviceType, true);
            if (service != null)
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: IApplicationHost.GetExports<T>(true) 成功");
                return service;
            }

            this.logger.Debug($"服务解析失败 {serviceType.FullName}: IApplicationHost.Resolve/TryResolve/GetExports 均返回空");
            return null;
        }

        /// <summary>解析并缓存 IApplicationHost 在当前 AppHost 实现上的精确方法映射。</summary>
        private AppHostResolverRuntime GetOrCreateAppHostResolverRuntime(Type appHostType)
        {
            if (appHostType == null)
            {
                return null;
            }

            var cached = appHostResolverRuntime;
            if (cached != null && cached.AppHostType == appHostType)
            {
                return cached;
            }

            lock (runtimeLock)
            {
                cached = appHostResolverRuntime;
                if (cached != null && cached.AppHostType == appHostType)
                {
                    return cached;
                }

                var resolve = ResolveInterfaceGenericMethod(appHostType, typeof(IApplicationHost), "Resolve", Type.EmptyTypes);
                var tryResolve = ResolveInterfaceGenericMethod(appHostType, typeof(IApplicationHost), "TryResolve", Type.EmptyTypes);
                var getExports = ResolveInterfaceGenericMethod(appHostType, typeof(IApplicationHost), "GetExports", new[] { typeof(bool) });

                if (resolve == null || tryResolve == null || getExports == null)
                {
                    this.logger.Warn("IApplicationHost 服务解析入口缺失");
                    return null;
                }

                cached = new AppHostResolverRuntime(appHostType, resolve, tryResolve, getExports);
                appHostResolverRuntime = cached;
                return cached;
            }
        }

        /// <summary>从 IApplicationHost 接口映射到当前 AppHost 实现的精确泛型方法。</summary>
        private static MethodInfo ResolveInterfaceGenericMethod(Type appHostType, Type interfaceType, string methodName, Type[] parameterTypes)
        {
            if (appHostType == null || interfaceType == null || !interfaceType.IsAssignableFrom(appHostType))
            {
                return null;
            }

            var interfaceMethod = interfaceType.GetMethod(methodName, parameterTypes ?? Type.EmptyTypes);
            if (interfaceMethod == null || !interfaceMethod.IsGenericMethodDefinition)
            {
                return null;
            }

            var interfaceMap = appHostType.GetInterfaceMap(interfaceType);
            for (var i = 0; i < interfaceMap.InterfaceMethods.Length; i++)
            {
                if (interfaceMap.InterfaceMethods[i] != interfaceMethod)
                {
                    continue;
                }

                var targetMethod = interfaceMap.TargetMethods[i];
                if (targetMethod != null && targetMethod.IsGenericMethodDefinition)
                {
                    return targetMethod;
                }
            }

            return null;
        }

        /// <summary>调用 IApplicationHost 的泛型解析方法，并兼容返回集合或单值的结果。</summary>
        private object InvokeGenericResolver(object appHost, MethodInfo method, Type serviceType, params object[] args)
        {
            if (appHost == null || method == null || serviceType == null)
            {
                return null;
            }

            try
            {
                var result = method.MakeGenericMethod(serviceType).Invoke(appHost, args);
                return UnwrapServiceResolutionResult(result);
            }
            catch (Exception ex)
            {
                this.logger.Debug($"服务解析失败 {serviceType.FullName}: {method.Name}<T>() 异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>将服务解析结果统一拆成单个服务实例。</summary>
        private static object UnwrapServiceResolutionResult(object result)
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

        /// <summary>串起 AudioFingerprint 的支持性检查、指纹生成与片头序列更新流程。</summary>
        private async Task<bool> RunAudioFingerprintWorkflowAsync(
            object detector,
            Episode episode,
            CancellationToken cancellationToken)
        {
            this.logger.Debug($"AudioFingerprint workflow start: detector={detector.GetType().FullName}, item={episode?.Path ?? episode?.Name}, id={episode?.InternalId}");
            var runtime = GetOrCreateAudioFingerprintRuntime();
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
            LogMethodInvocation(runtime.IsIntroDetectionSupported, supportedArgs);
            var supportedResult = await InvokeMethodAsync(detector, runtime.IsIntroDetectionSupported, supportedArgs).ConfigureAwait(false);
            if (supportedResult is bool supported && !supported)
            {
                this.logger.Debug("AudioFingerprintManager.IsIntroDetectionSupported 返回 false");
                return false;
            }
            this.logger.Debug($"IsIntroDetectionSupported result: {supportedResult ?? "null"}");

            this.logger.Debug("触发 CreateTitleFingerprint 生成指纹");
            var fingerprintArgs = new object[] { episode, libraryOptions, directoryService, cancellationToken };
            LogMethodInvocation(runtime.CreateTitleFingerprintAsync, fingerprintArgs);
            await InvokeMethodAsync(detector, runtime.CreateTitleFingerprintAsync, fingerprintArgs).ConfigureAwait(false);

            Season season = episode?.Season;
            if (season == null && episode != null)
            {
                season = this.libraryManager.GetItemById(episode.ParentId) as Season;
            }

            if (season == null)
            {
                this.logger.Debug("无法获取 Season，跳过 UpdateSequencesForSeason");
                return true;
            }

            this.logger.Debug($"Season resolved: {season.Name} (id={season.InternalId})");
            var seasonEpisodes = season.GetEpisodes(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            }, cancellationToken).Items.OfType<Episode>().ToArray();
            this.logger.Debug($"Season episodes loaded: count={seasonEpisodes.Length}");

            this.logger.Debug("触发 GetAllFingerprintFilesForSeason 收集指纹");
            var getArgs = new object[] { season, seasonEpisodes, libraryOptions, directoryService, cancellationToken };
            LogMethodInvocation(runtime.GetAllFingerprintFilesForSeason, getArgs);
            var seasonFingerprintInfo = await InvokeMethodAsync(detector, runtime.GetAllFingerprintFilesForSeason, getArgs).ConfigureAwait(false);
            this.logger.Debug($"GetAllFingerprintFilesForSeason result type: {seasonFingerprintInfo?.GetType().FullName ?? "null"}");

            this.logger.Debug("触发 UpdateSequencesForSeason 生成片头序列");
            if (seasonFingerprintInfo == null && runtime.SeasonFingerprintInfoType != null && runtime.SeasonFingerprintInfoType.IsClass)
            {
                this.logger.Debug("SeasonFingerprintInfo 为空，跳过 UpdateSequencesForSeason");
                return false;
            }

            var updateArgs = new[] { season, seasonFingerprintInfo, (object)episode, libraryOptions, directoryService, cancellationToken };
            LogMethodInvocation(runtime.UpdateSequencesForSeason, updateArgs);
            await InvokeMethodAsync(detector, runtime.UpdateSequencesForSeason, updateArgs).ConfigureAwait(false);
            this.logger.Debug($"AudioFingerprint workflow完成: item={episode?.Path ?? episode?.Name}");

            return true;
        }

        /// <summary>记录反射调用的方法签名与实参数类型，便于排查版本差异。</summary>
        private void LogMethodInvocation(MethodInfo method, object[] args)
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

        /// <summary>统一调用可能返回 Task 的反射方法，并在需要时取出其结果值。</summary>
        private static async Task<object> InvokeMethodAsync(object target, MethodInfo method, object[] args)
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

        private sealed class AppHostResolverRuntime
        {
            public AppHostResolverRuntime(Type appHostType, MethodInfo resolve, MethodInfo tryResolve, MethodInfo getExports)
            {
                AppHostType = appHostType;
                Resolve = resolve;
                TryResolve = tryResolve;
                GetExports = getExports;
            }

            public Type AppHostType { get; }

            public MethodInfo Resolve { get; }

            public MethodInfo TryResolve { get; }

            public MethodInfo GetExports { get; }
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
