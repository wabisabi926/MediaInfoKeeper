using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;

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
                var assembly = Assembly.Load("Emby.Providers");
                if (assembly != null)
                {
                    this.logger.Debug($"Emby.Providers 已加载: {assembly.FullName}");
                }

                var markerAssembly = Assembly.Load("Emby.Server.Implementations");
                if (markerAssembly != null)
                {
                    this.logger.Debug($"Emby.Server.Implementations 已加载: {markerAssembly.FullName}");
                }

                var managerType = assembly?.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                if (managerType == null)
                {
                    managerType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(SafeGetTypes)
                        .FirstOrDefault(t => t?.FullName != null &&
                                             t.FullName.IndexOf("AudioFingerprintManager", StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (managerType == null)
                {
                    this.logger.Warn("未找到 AudioFingerprintManager 类型");
                    return null;
                }

                LogMethodCandidates(managerType, managerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                var detector = TryResolveService(managerType);
                if (detector == null)
                {
                    this.logger.Warn($"AudioFingerprintManager 服务解析失败: {managerType.FullName}");
                }

                return detector;
            }
            catch (Exception ex)
            {
                this.logger.Warn($"片头检测服务加载失败: {ex.Message}");
                return null;
            }
        }

        private IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private void LogMethodCandidates(Type type, MethodInfo[] methods)
        {
            if (type == null || methods == null || methods.Length == 0)
            {
                return;
            }

            try
            {
                var list = methods
                    .Select(m =>
                    {
                        var parameters = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        return $"{m.Name}({parameters})";
                    })
                    .Distinct()
                    .OrderBy(name => name)
                    .ToList();

                this.logger.Debug($"可用方法({type.FullName}): {string.Join(" | ", list)}");
            }
            catch (Exception ex)
            {
                this.logger.Debug($"枚举方法失败({type.FullName}): {ex.Message}");
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

            var genericResolver = hostType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.IsGenericMethodDefinition &&
                                     m.GetGenericArguments().Length == 1 &&
                                     m.GetParameters().Length == 0 &&
                                     ResolverNames.Contains(m.Name));
            if (genericResolver != null)
            {
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

            var typeResolver = hostType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.GetParameters().Length == 1 &&
                                     m.GetParameters()[0].ParameterType == typeof(Type) &&
                                     ResolverNames.Contains(m.Name));
            if (typeResolver != null)
            {
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
            var managerType = detector.GetType();
            var methods = managerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var isSupported = methods.FirstOrDefault(m =>
                m.Name == "IsIntroDetectionSupported" &&
                m.GetParameters().Any(p => typeof(Episode).IsAssignableFrom(p.ParameterType)));

            var createTitleFingerprint = methods.FirstOrDefault(m =>
                m.Name == "CreateTitleFingerprint" &&
                m.GetParameters().Any(p => typeof(Episode).IsAssignableFrom(p.ParameterType)) &&
                m.GetParameters().Any(p => p.ParameterType == typeof(IDirectoryService)));

            var getAllFingerprintFiles = methods.FirstOrDefault(m =>
                m.Name == "GetAllFingerprintFilesForSeason" &&
                m.GetParameters().Any(p => typeof(Season).IsAssignableFrom(p.ParameterType)));

            var updateSequences = methods.FirstOrDefault(m =>
                m.Name == "UpdateSequencesForSeason" &&
                m.GetParameters().Any(p => typeof(Season).IsAssignableFrom(p.ParameterType)));

            if (isSupported == null && createTitleFingerprint == null && updateSequences == null)
            {
                this.logger.Warn($"AudioFingerprint workflow未找到关键方法: type={managerType.FullName}");
                return false;
            }

            this.logger.Debug($"AudioFingerprint methods: IsSupported={isSupported?.Name ?? "null"}, CreateTitleFingerprint={createTitleFingerprint?.Name ?? "null"}, GetAllFingerprintFilesForSeason={getAllFingerprintFiles?.Name ?? "null"}, UpdateSequencesForSeason={updateSequences?.Name ?? "null"}");

            var directoryService = new DirectoryService(this.logger, Plugin.FileSystem);
            var hasLibraryOptions = this.libraryManager.GetLibraryOptions(episode) != null;
            this.logger.Debug($"LibraryOptions loaded: null={!hasLibraryOptions}");

            if (isSupported != null)
            {
                var supportedArgs = BuildArguments(isSupported, episode, cancellationToken, directoryService);
                LogInvocation(isSupported, supportedArgs);
                var supportedResult = await InvokeWithResultAsync(detector, isSupported, supportedArgs).ConfigureAwait(false);
                if (supportedResult is bool supported && !supported)
                {
                    this.logger.Debug("AudioFingerprintManager.IsIntroDetectionSupported 返回 false");
                    return false;
                }

                this.logger.Debug($"IsIntroDetectionSupported result: {supportedResult ?? "null"}");
            }

            if (createTitleFingerprint != null)
            {
                this.logger.Debug("触发 CreateTitleFingerprint 生成指纹");
                var fingerprintArgs = BuildArguments(createTitleFingerprint, episode, cancellationToken, directoryService);
                LogInvocation(createTitleFingerprint, fingerprintArgs);
                await InvokeWithResultAsync(detector, createTitleFingerprint, fingerprintArgs).ConfigureAwait(false);
            }

            if (updateSequences == null)
            {
                this.logger.Debug($"AudioFingerprint workflow完成（仅生成指纹）: item={episode?.Path ?? episode?.Name}");
                return createTitleFingerprint != null;
            }

            var season = TryGetSeason(episode);
            if (season == null)
            {
                this.logger.Debug("无法获取 Season，跳过 UpdateSequencesForSeason");
                return createTitleFingerprint != null;
            }

            this.logger.Debug($"Season resolved: {season.Name} (id={season.InternalId})");
            var seasonEpisodes = FetchSeasonEpisodes(season);
            this.logger.Debug($"Season episodes loaded: count={seasonEpisodes.Length}");

            object seasonFingerprintInfo = null;
            if (getAllFingerprintFiles != null)
            {
                this.logger.Debug("触发 GetAllFingerprintFilesForSeason 收集指纹");
                var getArgs = BuildArguments(getAllFingerprintFiles, episode, cancellationToken, directoryService, season, seasonEpisodes);
                LogInvocation(getAllFingerprintFiles, getArgs);
                seasonFingerprintInfo = await InvokeWithResultAsync(detector, getAllFingerprintFiles, getArgs).ConfigureAwait(false);
                this.logger.Debug($"GetAllFingerprintFilesForSeason result type: {seasonFingerprintInfo?.GetType().FullName ?? "null"}");
            }

            this.logger.Debug("触发 UpdateSequencesForSeason 生成片头序列");
            var updateArgs = BuildArguments(updateSequences, episode, cancellationToken, directoryService, season, seasonEpisodes, seasonFingerprintInfo);
            LogInvocation(updateSequences, updateArgs);
            await InvokeWithResultAsync(detector, updateSequences, updateArgs).ConfigureAwait(false);
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

        private object[] BuildArguments(
            MethodInfo method,
            Episode episode,
            CancellationToken cancellationToken,
            IDirectoryService directoryService = null,
            Season season = null,
            Episode[] seasonEpisodes = null,
            object extra = null)
        {
            var parameters = method.GetParameters();
            var args = new object[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                var pType = parameters[i].ParameterType;

                if (typeof(Episode).IsAssignableFrom(pType))
                {
                    args[i] = episode;
                }
                else if (pType == typeof(Episode[]))
                {
                    args[i] = seasonEpisodes;
                }
                else if (typeof(Season).IsAssignableFrom(pType))
                {
                    args[i] = season;
                }
                else if (pType == typeof(CancellationToken))
                {
                    args[i] = cancellationToken;
                }
                else if (pType == typeof(ILibraryManager))
                {
                    args[i] = this.libraryManager;
                }
                else if (pType == typeof(ILogger))
                {
                    args[i] = this.logger;
                }
                else if (pType == typeof(LibraryOptions))
                {
                    args[i] = this.libraryManager.GetLibraryOptions(episode);
                }
                else if (pType == typeof(IDirectoryService))
                {
                    args[i] = directoryService ?? new DirectoryService(this.logger, Plugin.FileSystem);
                }
                else if (extra != null && pType.IsInstanceOfType(extra))
                {
                    args[i] = extra;
                }
                else if (pType.IsValueType)
                {
                    args[i] = Activator.CreateInstance(pType);
                }
                else
                {
                    args[i] = null;
                }
            }

            return args;
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
    }
}
