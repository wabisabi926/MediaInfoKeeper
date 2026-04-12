using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaInfoKeeper.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 监视快捷方式媒体的图片刷新时机，并触发 MediaInfo 恢复任务。
    /// </summary>
    public static class MetadataProvidersWatcher
    {
        private static Harmony harmony;
        private static MethodInfo staticCanRefresh;
        private static MethodInfo instanceCanRefresh;
        private static MethodInfo clearImages;
        private static ILogger logger;
        private static bool isEnabled;
        public static bool IsReady => harmony != null &&
                                      clearImages != null &&
                                      (staticCanRefresh != null || instanceCanRefresh != null);
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
                clearImages = ResolveClearImages(providerManager.Assembly);

                if ((staticCanRefresh == null && instanceCanRefresh == null) || clearImages == null)
                {
                    PatchLog.InitFailed(logger, nameof(MetadataProvidersWatcher), "未找到 CanRefresh/ClearImages 重载");
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

                    PatchLog.Patched(
                        logger,
                        nameof(MetadataProvidersWatcher),
                        clearImages);
                    harmony.Patch(clearImages,
                        prefix: new HarmonyMethod(typeof(MetadataProvidersWatcher), nameof(ClearImagesPrefix)));
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

        private static void ClearImagesPrefix(BaseItem item, ref ImageType[] imageTypesToClear, int numBackdropToKeep)
        {
            if (!isEnabled ||
                item is not Video ||
                item.InternalId == 0 ||
                !item.HasImage(ImageType.Primary) ||
                imageTypesToClear == null ||
                !imageTypesToClear.Contains(ImageType.Primary) ||
                !LibraryService.IsFileShortcut(item.Path ?? item.FileName))
            {
                return;
            }

            var workItem = Plugin.LibraryManager?.GetItemById(item.InternalId) ?? item;
            if (Plugin.LibraryService != null && !Plugin.LibraryService.IsItemInScope(workItem))
            {
                return;
            }

            // Keep the existing primary when no provider produced a replacement.
            // Provider ordering still decides who wins because successful SaveImage calls happen before ClearImages.
            imageTypesToClear = imageTypesToClear
                .Where(imageType => imageType != ImageType.Primary)
                .ToArray();
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
                if (Plugin.MediaInfoService?.HasMediaInfo(workItem) == true &&
                    LibraryService.IsFileShortcut(workItem.Path ?? workItem.FileName))
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
            var isVideoProvider = string.Equals(typeName, "Emby.Providers.MediaInfo.VideoImageProvider", StringComparison.Ordinal);
            var isAudioProvider = string.Equals(typeName, "Emby.Providers.MediaInfo.AudioImageProvider", StringComparison.Ordinal);

            if (!isVideoProvider && !isAudioProvider)
            {
                return false;
            }

            if (args[1] is BaseItem baseItem &&
                baseItem.InternalId != 0 &&
                (baseItem is Video || baseItem is Audio || baseItem is MusicAlbum))
            {
                item = baseItem;
                return true;
            }

            return false;
        }

        private static void TriggerMediaInfoRestore(BaseItem item)
        {
            if (item == null) return;

            try
            {
                MediaInfoRecoveryService.QueueRestore(item, 5);
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

        private static MethodInfo ResolveClearImages(Assembly embyProvidersAssembly)
        {
            try
            {
                var itemImageProvider = embyProvidersAssembly?.GetType("Emby.Providers.Manager.ItemImageProvider");
                if (itemImageProvider == null)
                {
                    return null;
                }

                var embyProvidersVersion = embyProvidersAssembly.GetName().Version;
                return PatchMethodResolver.Resolve(
                    itemImageProvider,
                    embyProvidersVersion,
                    new MethodSignatureProfile
                    {
                        Name = "itemimageprovider-clearimages-exact",
                        MethodName = "ClearImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[]
                        {
                            typeof(BaseItem),
                            typeof(ImageType[]),
                            typeof(int)
                        },
                        ReturnType = typeof(void),
                        IsStatic = false
                    },
                    logger,
                    "MetadataProvidersWatcher.ClearImages");
            }
            catch
            {
                return null;
            }
        }

    }
}
