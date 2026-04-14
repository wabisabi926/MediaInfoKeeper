using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaInfoKeeper.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 保护快捷方式视频在图片刷新失败时不被清空现有主图。
    /// </summary>
    public static class ImageClearGuard
    {
        private static Harmony harmony;
        private static MethodInfo clearImages;
        private static ILogger logger;
        private static bool isEnabled;

        public static bool IsReady => harmony != null && clearImages != null;

        public static void Initialize(ILogger pluginLogger, bool enableGuard)
        {
            if (harmony != null)
            {
                return;
            }

            logger = pluginLogger;
            isEnabled = enableGuard;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManager = embyProviders?.GetType("Emby.Providers.Manager.ProviderManager");
                if (providerManager == null)
                {
                    PatchLog.InitFailed(logger, nameof(ImageClearGuard), "未找到 ProviderManager");
                    return;
                }

                clearImages = ResolveClearImages(providerManager.Assembly);
                if (clearImages == null)
                {
                    PatchLog.InitFailed(logger, nameof(ImageClearGuard), "未找到 ClearImages 重载");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.itemimageclear");
                PatchLog.Patched(logger, nameof(ImageClearGuard), clearImages);
                harmony.Patch(
                    clearImages,
                    prefix: new HarmonyMethod(typeof(ImageClearGuard), nameof(ClearImagesPrefix)));
            }
            catch (Exception ex)
            {
                logger?.Error("ItemImageClearGuard 初始化失败");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
            }
        }

        public static void Configure(bool enableGuard)
        {
            isEnabled = enableGuard;
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

            // SaveImage succeeds before ClearImages, so removing Primary here only protects
            // the current image when no replacement was actually written.
            imageTypesToClear = imageTypesToClear
                .Where(imageType => imageType != ImageType.Primary)
                .ToArray();
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
                    "ItemImageClearGuard.ClearImages");
            }
            catch
            {
                return null;
            }
        }
    }
}
