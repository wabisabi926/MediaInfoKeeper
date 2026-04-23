using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 当歌曲条目缺少主图时，优先使用专辑（展示父级）主图作为 DTO 的主图回退。
    /// </summary>
    public static class AudioAlbumPrimaryFallback
    {
        private static readonly object InitLock = new object();

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;
        private static MethodInfo getBaseItemDtoInternal;
        private static MethodInfo getImageCacheTag;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enableAudioAlbumPrimaryFallback)
        {
            lock (InitLock)
            {
                logger = pluginLogger;
                isEnabled = enableAudioAlbumPrimaryFallback;

                if (harmony != null)
                {
                    Configure(enableAudioAlbumPrimaryFallback);
                    return;
                }

                try
                {
                    var implementationAssembly = Assembly.Load("Emby.Server.Implementations");
                    var implementationVersion = implementationAssembly?.GetName().Version;
                    var dtoServiceType = implementationAssembly?.GetType("Emby.Server.Implementations.Dto.DtoService", false);
                    if (dtoServiceType == null)
                    {
                        PatchLog.InitFailed(logger, nameof(AudioAlbumPrimaryFallback), "未找到 DtoService");
                        return;
                    }

                    getBaseItemDtoInternal = PatchMethodResolver.Resolve(
                        dtoServiceType,
                        implementationVersion,
                        new MethodSignatureProfile
                        {
                            Name = "dtoservice-getbaseitemdtointernal",
                            MethodName = "GetBaseItemDtoInternal",
                            BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                            IsStatic = false,
                            ParameterTypes = new[]
                            {
                                typeof(BaseItem),
                                typeof(DtoOptions),
                                typeof(User),
                                typeof(CancellationToken)
                            },
                            ReturnType = typeof(BaseItemDto)
                        },
                        logger,
                        "AudioAlbumPrimaryFallback.DtoService.GetBaseItemDtoInternal");

                    getImageCacheTag = PatchMethodResolver.Resolve(
                        dtoServiceType,
                        implementationVersion,
                        new MethodSignatureProfile
                        {
                            Name = "dtoservice-getimagecachetag-itemimageinfo",
                            MethodName = "GetImageCacheTag",
                            BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                            IsStatic = false,
                            ParameterTypes = new[]
                            {
                                typeof(BaseItem),
                                typeof(ItemImageInfo)
                            },
                            ReturnType = typeof(string)
                        },
                        logger,
                        "AudioAlbumPrimaryFallback.DtoService.GetImageCacheTag");

                    if (getBaseItemDtoInternal == null || getImageCacheTag == null)
                    {
                        PatchLog.InitFailed(logger, nameof(AudioAlbumPrimaryFallback), "DTO 图片相关方法缺失");
                        return;
                    }

                    harmony = new Harmony("mediainfokeeper.audioalbumprimaryfallback");
                    PatchLog.Patched(logger, nameof(AudioAlbumPrimaryFallback), getBaseItemDtoInternal);

                    if (isEnabled)
                    {
                        Patch();
                    }
                }
                catch (Exception ex)
                {
                    PatchLog.InitFailed(logger, nameof(AudioAlbumPrimaryFallback), ex.Message);
                    logger?.Error("AudioAlbumPrimaryFallback 初始化异常：{0}", ex);
                    harmony = null;
                    isEnabled = false;
                }
            }
        }

        public static void Configure(bool enableAudioAlbumPrimaryFallback)
        {
            lock (InitLock)
            {
                isEnabled = enableAudioAlbumPrimaryFallback;
                if (harmony == null)
                {
                    return;
                }

                if (isEnabled)
                {
                    Patch();
                }
                else
                {
                    Unpatch();
                }
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || getBaseItemDtoInternal == null)
            {
                return;
            }

            harmony.Patch(
                getBaseItemDtoInternal,
                postfix: new HarmonyMethod(typeof(AudioAlbumPrimaryFallback), nameof(GetBaseItemDtoInternalPostfix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || getBaseItemDtoInternal == null)
            {
                return;
            }

            harmony.Unpatch(getBaseItemDtoInternal, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void GetBaseItemDtoInternalPostfix(
            object __instance,
            BaseItem item,
            User user,
            ref BaseItemDto __result)
        {
            if (!isEnabled || __instance == null || item is not Audio audio || __result == null)
            {
                return;
            }

            if (audio.GetImageInfo(ImageType.Primary, 0) != null)
            {
                return;
            }

            var displayParentId = audio.ImageDisplayParentId;
            if (displayParentId == 0 || displayParentId == audio.InternalId)
            {
                return;
            }

            var displayParent = Plugin.LibraryManager?.GetItemById(displayParentId);
            if (!TryResolvePrimaryImageSource(displayParent, user, out var imageOwner, out var imageInfo))
            {
                return;
            }

            try
            {
                var displayParentPrimaryTag = getImageCacheTag?.Invoke(
                    __instance,
                    new object[] { imageOwner, imageInfo }) as string;
                if (string.IsNullOrWhiteSpace(displayParentPrimaryTag))
                {
                    return;
                }

                __result.PrimaryImageItemId = imageOwner.GetClientId();
                __result.PrimaryImageTag = displayParentPrimaryTag;
            }
            catch (Exception ex)
            {
                logger?.Debug("AudioAlbumPrimaryFallback failed: {0}", ex.Message);
            }
        }

        private static bool TryResolvePrimaryImageSource(
            BaseItem displayParent,
            User user,
            out BaseItem imageOwner,
            out ItemImageInfo imageInfo)
        {
            imageOwner = null;
            imageInfo = null;
            if (displayParent == null)
            {
                return false;
            }

            var primaryImage = displayParent.GetImageInfo(ImageType.Primary, 0);
            if (primaryImage != null)
            {
                imageOwner = displayParent;
                imageInfo = primaryImage;
                return true;
            }

            if (displayParent is not Folder folder)
            {
                return false;
            }

            // Match Emby's folder cover behavior by borrowing the first audio child's primary image.
            var query = new InternalItemsQuery(user)
            {
                Recursive = displayParent is MusicAlbum || displayParent is Season,
                IsFolder = false,
                EnableTotalRecordCount = false,
                Limit = 1,
                ImageTypes = new[] { ImageType.Primary },
                IncludeItemTypes = new[] { nameof(Audio) }
            };

            var childWithImage = folder.GetItems(query, CancellationToken.None).Items.FirstOrDefault();
            var childPrimaryImage = childWithImage?.GetImageInfo(ImageType.Primary, 0);
            if (childWithImage == null || childPrimaryImage == null)
            {
                return false;
            }

            imageOwner = childWithImage;
            imageInfo = childPrimaryImage;
            return true;
        }
    }
}
