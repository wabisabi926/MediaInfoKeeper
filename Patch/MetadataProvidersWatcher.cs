using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MediaInfoKeeper.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Common;

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
        private static IDirectoryService directoryService;
        public static bool IsReady => harmony != null && (staticCanRefresh != null || instanceCanRefresh != null);
        public static void Initialize(ILogger pluginLogger, bool enableWatcher)
        {
            if (harmony != null) return;

            logger = pluginLogger;
            isEnabled = enableWatcher;
            directoryService = new DirectoryService(pluginLogger, Plugin.FileSystem);

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

            if (TryGetWatchedShortcutItem(__args, out var item))
            {
                var workItem = Plugin.LibraryManager?.GetItemById(item.InternalId) ?? item;
                if (Plugin.MediaInfoService?.HasMediaInfo(workItem) == true && workItem.IsShortcut)
                {
                    TriggerMediaInfoRestore(workItem);
                }
            }
            return true;
        }

        private static bool TryGetWatchedShortcutItem(object[] args, out BaseItem item)
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

            if (args[1] is BaseItem baseItem &&
                baseItem.InternalId != 0 &&
                baseItem.IsShortcut &&
                (baseItem is Video || baseItem is Audio))
            {
                item = baseItem;
                return true;
            }

            return false;
        }

        private static void TriggerMediaInfoRestore(BaseItem item,bool force = false)
        {
            if (item == null) return;

            try
            {
                MediaInfoRestoreService.QueueRestore("MetadataProvidersWatcher", item, 30);
            }
            catch (Exception ex)
            {
                logger?.Error("MetadataProvidersWatcher 恢复 MediaInfo 失败");
                logger?.Error(ex.Message);
            }
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
