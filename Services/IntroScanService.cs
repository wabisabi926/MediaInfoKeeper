using System;
using System.Collections;
using System.Collections.Generic;
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
using MediaBrowser.Model.Tasks;

namespace MediaInfoKeeper.Services
{
    public class IntroScanService
    {
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

            foreach (var episode in episodes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info("扫描已取消");
                    return;
                }

                var displayName = episode.Path ?? episode.Name;
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
                    var detected = await TryDetectIntroAsync(episode, cancellationToken).ConfigureAwait(false);
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
            var detector = TryResolveAudioFingerprintManager();
            if (detector != null)
            {
                if (await TryRunAudioFingerprintWorkflowAsync(detector, episode, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }

                this.logger.Debug($"AudioFingerprintManager 执行失败: {detector.GetType().FullName}");
            }
            else
            {
                this.logger.Debug("未能解析 AudioFingerprintManager");
            }

            this.logger.Info("探测失败，可能尚未获取strm文件内容，请稍后再试，未找到片头检测方法，请检查 Emby 版本");
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
                if (managerType != null)
                {
                    LogMethodCandidates(managerType,
                        managerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                    return TryResolveService(managerType);
                }

                var candidates = FindTypeCandidates("AudioFingerprint");
                if (candidates.Count > 0)
                {
                    foreach (var type in candidates)
                    {
                        var resolved = TryResolveService(type);
                        if (resolved != null)
                        {
                            this.logger.Info($"已解析 AudioFingerprint 类型: {type.FullName}");
                            LogMethodCandidates(type,
                                type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                            return resolved;
                        }
                    }
                }

                LogTypeCandidates(assembly, "AudioFingerprintManager");
                return null;
            }
            catch (Exception ex)
            {
                this.logger.Warn($"片头检测服务加载失败: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> TryRunMarkerScheduledTaskAsync(Episode episode, CancellationToken cancellationToken)
        {
            var markerTask = TryResolveMarkerScheduledTask();
            if (markerTask == null)
            {
                return false;
            }

            var taskType = markerTask.GetType();
            var methods = taskType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            LogMethodCandidates(taskType, methods);

            var candidates = methods
                .Where(m =>
                    (m.Name.IndexOf("Intro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.Name.IndexOf("Marker", StringComparison.OrdinalIgnoreCase) >= 0) &&
                    (m.Name.IndexOf("Detect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.Name.IndexOf("Scan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.Name.IndexOf("Process", StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            foreach (var method in candidates)
            {
                if (!method.GetParameters().Any(p => typeof(Episode).IsAssignableFrom(p.ParameterType) ||
                                                     typeof(BaseItem).IsAssignableFrom(p.ParameterType)))
                {
                    continue;
                }

                this.logger.Info($"使用 MarkerScheduledTask 方法: {taskType.Name}.{method.Name}");
                var args = BuildArguments(method, episode, cancellationToken);
                var result = method.Invoke(markerTask, args);
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                }

                return true;
            }

            if (markerTask is IScheduledTask scheduledTask)
            {
                this.logger.Info("调用 MarkerScheduledTask.Execute 触发片头检测");
                await scheduledTask.Execute(cancellationToken, new Progress<double>()).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        private object TryResolveMarkerScheduledTask()
        {
            try
            {
                var assembly = Assembly.Load("Emby.Providers");
                var taskType = assembly?.GetType("Emby.Providers.Markers.MarkerScheduledTask");
                if (taskType != null)
                {
                    return TryResolveService(taskType);
                }

                var candidates = FindTypeCandidates("MarkerScheduledTask");
                if (candidates.Count == 0)
                {
                    candidates = FindTypeCandidates("Marker");
                }

                if (candidates.Count > 0)
                {
                    foreach (var type in candidates)
                    {
                        var resolved = TryResolveService(type);
                        if (resolved != null)
                        {
                            this.logger.Info($"已解析 Marker 任务类型: {type.FullName}");
                            return resolved;
                        }
                    }
                }

                LogTypeCandidates(assembly, "Marker");
                return null;
            }
            catch (Exception ex)
            {
                this.logger.Warn($"MarkerScheduledTask 加载失败: {ex.Message}");
                return null;
            }
        }

        private void LogTypeCandidates(Assembly assembly, string keyword)
        {
            if (assembly == null)
            {
                return;
            }

            try
            {
                var candidates = assembly.GetTypes()
                    .Where(t => t.FullName != null &&
                                t.FullName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(t => t.FullName)
                    .OrderBy(n => n)
                    .ToList();

                if (candidates.Count == 0)
                {
                    return;
                }

                this.logger.Warn($"未匹配到目标类型，候选类型({keyword}): {string.Join(", ", candidates)}");
            }
            catch (Exception ex)
            {
                this.logger.Debug($"枚举类型失败({keyword}): {ex.Message}");
            }
        }

        private List<Type> FindTypeCandidates(string keyword)
        {
            var list = new List<Type>();
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in assemblies)
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(t => t != null).ToArray();
                    }

                    foreach (var type in types)
                    {
                        if (type?.FullName == null)
                        {
                            continue;
                        }

                        if (type.FullName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            list.Add(type);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.Debug($"全局枚举类型失败({keyword}): {ex.Message}");
            }

            if (list.Count > 0)
            {
                var names = list.Select(t => $"{t.FullName} [{t.Assembly.GetName().Name}]")
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
                this.logger.Debug($"全局候选类型({keyword}): {string.Join(", ", names)}");
            }

            return list;
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
                        var parameters = string.Join(", ", m.GetParameters()
                            .Select(p => $"{p.ParameterType.Name} {p.Name}"));
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
                this.logger.Debug($"服务解析 {serviceType.FullName}: AppHost 实现 IServiceProvider");
                var service = serviceProvider.GetService(serviceType);
                if (service != null)
                {
                    this.logger.Debug($"服务解析 {serviceType.FullName}: IServiceProvider.GetService 成功");
                    return service;
                }

                this.logger.Debug($"服务解析 {serviceType.FullName}: IServiceProvider.GetService 返回空");
            }
            else
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: AppHost 未实现 IServiceProvider");
            }

            var hostType = appHost.GetType();
            this.logger.Debug($"服务解析 {serviceType.FullName}: AppHost 类型 {hostType.FullName}");
            var methods = hostType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var getExports = methods.FirstOrDefault(m =>
                m.Name == "GetExports" &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(Type));
            if (getExports != null)
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: 尝试 GetExports(Type)");
                if (getExports.Invoke(appHost, new object[] { serviceType }) is IEnumerable exports)
                {
                    var found = false;
                    foreach (var item in exports)
                    {
                        found = true;
                        return item;
                    }

                    if (!found)
                    {
                        this.logger.Debug($"服务解析 {serviceType.FullName}: GetExports(Type) 返回空集合");
                    }
                }
                else
                {
                    this.logger.Debug($"服务解析 {serviceType.FullName}: GetExports(Type) 返回 null");
                }
            }
            else
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: 未找到 GetExports(Type)");
            }

            var getExportsGeneric = methods.FirstOrDefault(m =>
                m.Name == "GetExports" &&
                m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 0);
            if (getExportsGeneric != null)
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: 尝试 GetExports<T>()");
                var result = getExportsGeneric.MakeGenericMethod(serviceType).Invoke(appHost, null);
                if (result is IEnumerable exports)
                {
                    var found = false;
                    foreach (var item in exports)
                    {
                        found = true;
                        return item;
                    }

                    if (!found)
                    {
                        this.logger.Debug($"服务解析 {serviceType.FullName}: GetExports<T>() 返回空集合");
                    }
                }
                else
                {
                    this.logger.Debug($"服务解析 {serviceType.FullName}: GetExports<T>() 返回 null");
                }
            }
            else
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: 未找到 GetExports<T>()");
            }

            var resolve = methods.FirstOrDefault(m =>
                m.Name == "Resolve" &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(Type));
            if (resolve != null)
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: 尝试 Resolve(Type)");
                return resolve.Invoke(appHost, new object[] { serviceType });
            }

            this.logger.Debug($"服务解析 {serviceType.FullName}: 未找到 Resolve(Type)");

            var getService = methods.FirstOrDefault(m =>
                m.Name == "GetService" &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType == typeof(Type));
            if (getService != null)
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: 尝试 GetService(Type)");
                return getService.Invoke(appHost, new object[] { serviceType });
            }

            this.logger.Debug($"服务解析 {serviceType.FullName}: 未找到 GetService(Type)");

            var resolved = TryResolveFromHostContainers(appHost, serviceType);
            if (resolved != null)
            {
                this.logger.Debug($"服务解析 {serviceType.FullName}: 通过内部容器成功");
                return resolved;
            }

            this.logger.Debug($"服务解析 {serviceType.FullName}: 内部容器解析失败");
            return null;
        }


        private object TryResolveFromHostContainers(object appHost, Type serviceType)
        {
            var visited = new HashSet<object>();
            return TryResolveFromObject(appHost, serviceType, "AppHost", visited, 0);
        }

        private object TryResolveFromObject(
            object target,
            Type serviceType,
            string sourceName,
            HashSet<object> visited,
            int depth)
        {
            if (target == null || !visited.Add(target) || depth > 2)
            {
                return null;
            }

            if (target is IServiceProvider serviceProvider)
            {
                var service = serviceProvider.GetService(serviceType);
                if (service != null)
                {
                    this.logger.Debug($"服务解析 {serviceType.FullName}: {sourceName} IServiceProvider 成功");
                    return service;
                }
            }

            var targetType = target.GetType();

            var resolved = TryResolveByKnownMethods(target, targetType, serviceType, sourceName);
            if (resolved != null)
            {
                return resolved;
            }

            foreach (var candidate in GetContainerCandidates(target, targetType))
            {
                var child = candidate.Value;
                if (child == null)
                {
                    continue;
                }

                var childSource = $"{sourceName}.{candidate.Name}";
                resolved = TryResolveByKnownMethods(child, child.GetType(), serviceType, childSource);
                if (resolved != null)
                {
                    return resolved;
                }

                resolved = TryResolveFromObject(child, serviceType, childSource, visited, depth + 1);
                if (resolved != null)
                {
                    return resolved;
                }
            }

            return null;
        }

        private object TryResolveByKnownMethods(
            object target,
            Type targetType,
            Type serviceType,
            string sourceName)
        {
            var method = FindMethod(targetType, "Resolve")
                         ?? FindMethod(targetType, "GetService")
                         ?? FindMethod(targetType, "TryResolve")
                         ?? FindMethod(targetType, "GetInstance")
                         ?? FindMethod(targetType, "GetExport")
                         ?? FindMethod(targetType, "GetExports");

            if (method != null)
            {
                try
                {
                    var result = method.Invoke(target, new object[] { serviceType });
                    if (result is IEnumerable exports)
                    {
                        foreach (var item in exports)
                        {
                            this.logger.Debug($"服务解析 {serviceType.FullName}: {sourceName}.{method.Name}(Type) 成功");
                            return item;
                        }

                        return null;
                    }

                    if (result != null)
                    {
                        this.logger.Debug($"服务解析 {serviceType.FullName}: {sourceName}.{method.Name}(Type) 成功");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    this.logger.Debug($"服务解析 {serviceType.FullName}: {sourceName}.{method.Name}(Type) 失败: {ex.Message}");
                }
            }

            var generic = FindGenericMethod(targetType, new[] { "Resolve", "GetService", "TryResolve", "GetInstance", "GetExport", "GetExports" });
            if (generic != null)
            {
                try
                {
                    var result = generic.MakeGenericMethod(serviceType).Invoke(target, null);
                    if (result is IEnumerable exports)
                    {
                        foreach (var item in exports)
                        {
                            this.logger.Debug($"服务解析 {serviceType.FullName}: {sourceName}.{generic.Name}<T>() 成功");
                            return item;
                        }

                        return null;
                    }

                    if (result != null)
                    {
                        this.logger.Debug($"服务解析 {serviceType.FullName}: {sourceName}.{generic.Name}<T>() 成功");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    this.logger.Debug($"服务解析 {serviceType.FullName}: {sourceName}.{generic.Name}<T>() 失败: {ex.Message}");
                }
            }

            return null;
        }

        private static MethodInfo FindMethod(Type type, string name)
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                    m.Name == name &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(Type));
        }

        private static MethodInfo FindGenericMethod(Type type, string[] names)
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.IsGenericMethodDefinition &&
                                     m.GetParameters().Length == 0 &&
                                     names.Contains(m.Name));
        }

        private static List<(string Name, object Value)> GetContainerCandidates(object host, Type hostType)
        {
            var list = new List<(string Name, object Value)>();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var field in hostType.GetFields(flags))
            {
                object value = null;
                try
                {
                    value = field.GetValue(host);
                }
                catch
                {
                    // ignore
                }

                if (value != null && value != host)
                {
                    list.Add((field.Name, value));
                }
            }

            foreach (var prop in hostType.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object value = null;
                try
                {
                    value = prop.GetValue(host);
                }
                catch
                {
                    // ignore
                }

                if (value != null && value != host)
                {
                    list.Add((prop.Name, value));
                }
            }

            return list;
        }

        private async Task<bool> TryRunAudioFingerprintWorkflowAsync(
            object detector,
            Episode episode,
            CancellationToken cancellationToken)
        {
            var managerType = detector.GetType();
            var methods = managerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var isSupported = methods.FirstOrDefault(m =>
                m.Name == "IsIntroDetectionSupported" &&
                m.GetParameters().Any(p => typeof(Episode).IsAssignableFrom(p.ParameterType)));

            var createTitleFingerprint = methods.FirstOrDefault(m =>
                m.Name == "CreateTitleFingerprint" &&
                m.GetParameters().Any(p => typeof(Episode).IsAssignableFrom(p.ParameterType)));

            var getAllFingerprintFiles = methods.FirstOrDefault(m =>
                m.Name == "GetAllFingerprintFilesForSeason" &&
                m.GetParameters().Any(p => typeof(Season).IsAssignableFrom(p.ParameterType)));

            var updateSequences = methods.FirstOrDefault(m =>
                m.Name == "UpdateSequencesForSeason" &&
                m.GetParameters().Any(p => typeof(Season).IsAssignableFrom(p.ParameterType)));

            if (isSupported == null && createTitleFingerprint == null && updateSequences == null)
            {
                return false;
            }

            var directoryService = new DirectoryService(this.logger, Plugin.FileSystem);
            var libraryOptions = this.libraryManager.GetLibraryOptions(episode);

            if (isSupported != null)
            {
                var supportedArgs = BuildArguments(isSupported, episode, cancellationToken, directoryService);
                var supportedResult = await InvokeWithResultAsync(detector, isSupported, supportedArgs)
                    .ConfigureAwait(false);
                if (supportedResult is bool supported && !supported)
                {
                    this.logger.Debug("AudioFingerprintManager.IsIntroDetectionSupported 返回 false");
                    return false;
                }
            }

            if (createTitleFingerprint != null)
            {
                this.logger.Debug("触发 CreateTitleFingerprint 生成指纹");
                var fingerprintArgs = BuildArguments(createTitleFingerprint, episode, cancellationToken, directoryService);
                await InvokeWithResultAsync(detector, createTitleFingerprint, fingerprintArgs).ConfigureAwait(false);
            }

            if (updateSequences == null)
            {
                return createTitleFingerprint != null;
            }

            var season = TryGetSeason(episode);
            if (season == null)
            {
                this.logger.Debug("无法获取 Season，跳过 UpdateSequencesForSeason");
                return createTitleFingerprint != null;
            }

            var seasonEpisodes = FetchSeasonEpisodes(season);
            object seasonFingerprintInfo = null;

            if (getAllFingerprintFiles != null)
            {
                this.logger.Debug("触发 GetAllFingerprintFilesForSeason 收集指纹");
                var getArgs = BuildArguments(getAllFingerprintFiles, episode, cancellationToken, directoryService, season,
                    seasonEpisodes);
                seasonFingerprintInfo = await InvokeWithResultAsync(detector, getAllFingerprintFiles, getArgs)
                    .ConfigureAwait(false);
            }

            this.logger.Debug("触发 UpdateSequencesForSeason 生成片头序列");
            var updateArgs = BuildArguments(updateSequences, episode, cancellationToken, directoryService, season,
                seasonEpisodes, seasonFingerprintInfo);
            await InvokeWithResultAsync(detector, updateSequences, updateArgs).ConfigureAwait(false);

            return true;
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

            return this.libraryManager
                .GetItemList(query)
                .OfType<Episode>()
                .ToArray();
        }

    }
}
