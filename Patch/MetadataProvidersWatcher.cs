using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 控制 Emby ProviderManager 的元数据刷新，仅在显式作用域内放行。
    /// </summary>
    public static class MetadataProvidersWatcher
    {
        private static Harmony harmony;
        private static MethodInfo staticCanRefresh;
        private static MethodInfo instanceCanRefresh;
        private static ILogger logger;
        private static bool isEnabled;
        public static bool IsReady => harmony != null && (staticCanRefresh != null || instanceCanRefresh != null);
        public static void Initialize(ILogger pluginLogger, bool enableWatcher)
        {
            if (harmony != null) return;

            logger = pluginLogger;
            isEnabled = enableWatcher;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManager = embyProviders?.GetType("Emby.Providers.Manager.ProviderManager");
                if (providerManager == null)
                {
                    PatchLog.InitFailed(logger, nameof(MetadataProvidersWatcher), "未找到 ProviderManager");
                    return;
                }

                staticCanRefresh = ResolveStaticCanRefresh(providerManager);
                instanceCanRefresh = ResolveInstanceCanRefresh(providerManager);

                if (staticCanRefresh == null && instanceCanRefresh == null)
                {
                    PatchLog.InitFailed(logger, nameof(MetadataProvidersWatcher), "未找到 CanRefresh 重载");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.metadata");

                try
                {
                    if (staticCanRefresh != null)
                    {
                        PatchLog.Patched(
                            logger,
                            nameof(MetadataProvidersWatcher),
                            staticCanRefresh);
                        harmony.Patch(staticCanRefresh,
                            prefix: new HarmonyMethod(typeof(MetadataProvidersWatcher), nameof(CanRefreshPrefix)));
                    }

                    if (instanceCanRefresh != null)
                    {
                        PatchLog.Patched(
                            logger,
                            nameof(MetadataProvidersWatcher),
                            instanceCanRefresh);
                        harmony.Patch(instanceCanRefresh,
                            prefix: new HarmonyMethod(typeof(MetadataProvidersWatcher), nameof(CanRefreshPrefix)));
                    }
                }
                catch (Exception patchEx)
                {
                    logger.Error("MetadataProvidersWatcher patch 失败");
                    logger.Error(patchEx.Message);
                    logger.Error(patchEx.ToString());
                    harmony = null;
                    return;
                }
            }
            catch (Exception e)
            {
                logger.Error("MetadataProvidersWatcher 初始化失败");
                logger.Error(e.Message);
                logger.Error(e.ToString());
                harmony = null;
            }
        }

        public static void Configure(bool enableWatcher)
        {
            isEnabled = enableWatcher;
        }

        private static bool CanRefreshPrefix(object __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (IsVideoImageProviderWithEpisode(__args, out var item))
            {
                // 刷新媒体信息时，VideoImageProvider 会调用 EpisodeMetadataProvider 的 CanRefresh 来判断是否需要刷新媒体信息，此时如果 Episode 已经存在 MediaInfo，则说明是刷新后的回调，此时加入延迟恢复队列，10s 后恢复媒体信息，以覆盖可能被刷新掉的 MediaInfo
                var workItem = Plugin.LibraryManager?.GetItemById(item.InternalId) ?? item;
                if (Plugin.MediaInfoService?.HasMediaInfo(workItem) == true && workItem.IsShortcut)
                {
                    TriggerMediaInfoRestore(workItem);
                }
            }
            return true;
        }

        private static bool IsVideoImageProviderWithEpisode(object[] args, out BaseItem item)
        {
            item = null;
            if (args == null || args.Length < 2) return false;

            var provider = args[0];
            if (provider == null) return false;

            var typeName = provider.GetType().FullName ?? provider.GetType().Name;
            if (!string.Equals(typeName, "Emby.Providers.MediaInfo.VideoImageProvider", StringComparison.Ordinal))
            {
                return false;
            }

            if (args[1] is Episode baseItem && baseItem.InternalId != 0)
            {
                item = baseItem;
                return true;
            }

            return false;
        }

        private static void TriggerMediaInfoRestore(BaseItem item)
        {
            if (item == null) return;

            Task.Run(async () =>
            {
                try
                {
                    logger?.Debug($"MetadataProvidersWatcher 检测到元数据刷新，刷新前存在 MediaInfo，已加入延迟检查队列 {item.FileName ?? item.Path} InternalId:{item.InternalId}");
                    logger?.Debug($"{item.FileName ?? item.Path} 30s之后检查媒体信息");
                    await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    if (Plugin.MediaInfoService.HasMediaInfo(item))
                    {
                        // 恢复时再次检查，看看是否fresh导致MediaInfo丢失，没丢失则跳过
                        logger?.Debug($"{item.FileName ?? item.Path} 刷新元数据后，媒体信息仍然存在，跳过恢复");
                        return;
                    }
                    logger?.Info($"MetadataProvidersWatcher {item.FileName ?? item.Path} 刷新元数据后，媒体信息丢失，开始尝试恢复媒体信息");
                    
                    Plugin.MediaSourceInfoStore.ApplyToItem(item);
                    if (!Plugin.IntroScanService.HasIntroMarkers(item))
                    {
                        Plugin.ChaptersStore.ApplyToItem(item);
                    }
                }
                catch (Exception ex)
                {
                    logger?.Error("MetadataProvidersWatcher 恢复 MediaInfo 失败");
                    logger?.Error(ex.Message);
                }
            });
        }

        private static MethodInfo ResolveStaticCanRefresh(Type providerManager)
        {
            try
            {
                var embyProvidersVersion = providerManager.Assembly.GetName().Version;
                return PatchMethodResolver.Resolve(
                    providerManager,
                    embyProvidersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "static-canrefresh-exact",
                        MethodName = "CanRefresh",
                        BindingFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[]
                        {
                            typeof(IMetadataProvider),
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(bool),
                            typeof(bool),
                            typeof(bool)
                        }
                    },
                    logger,
                    "MetadataProvidersWatcher.StaticCanRefresh");
            }
            catch
            {
                return null;
            }
        }

        private static MethodInfo ResolveInstanceCanRefresh(Type providerManager)
        {
            try
            {
                var embyProvidersVersion = providerManager.Assembly.GetName().Version;
                return PatchMethodResolver.Resolve(
                    providerManager,
                    embyProvidersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "instance-canrefresh-exact",
                        MethodName = "CanRefresh",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[]
                        {
                            typeof(IImageProvider),
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(ImageRefreshOptions),
                            typeof(bool),
                            typeof(bool)
                        }
                    },
                    logger,
                    "MetadataProvidersWatcher.InstanceCanRefresh");
            }
            catch
            {
                return null;
            }
        }

    }
}
