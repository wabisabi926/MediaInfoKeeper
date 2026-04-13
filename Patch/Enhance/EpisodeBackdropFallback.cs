using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 当剧集条目缺少主图时，优先让 DTO 暴露父级背景图，并移除 series 海报回退。
    /// </summary>
    public static class EpisodeBackdropFallback
    {
        private static readonly object InitLock = new object();

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;
        private static MethodInfo getBaseItemDtoInternal;
        private static MethodInfo getImageTags;
        private static MethodInfo getImageCacheTag;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            lock (InitLock)
            {
                logger = pluginLogger;
                isEnabled = enable;

                if (harmony != null)
                {
                    Configure(enable);
                    return;
                }

                try
                {
                    var implementationAssembly = Assembly.Load("Emby.Server.Implementations");
                    var implementationVersion = implementationAssembly?.GetName().Version;
                    var dtoServiceType = implementationAssembly?.GetType("Emby.Server.Implementations.Dto.DtoService", false);
                    if (dtoServiceType == null)
                    {
                        PatchLog.InitFailed(logger, nameof(EpisodeBackdropFallback), "未找到 DtoService");
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
                        "EpisodeBackdropFallback.DtoService.GetBaseItemDtoInternal");

                    getImageTags = PatchMethodResolver.Resolve(
                        dtoServiceType,
                        implementationVersion,
                        new MethodSignatureProfile
                        {
                            Name = "dtoservice-getimagetags",
                            MethodName = "GetImageTags",
                            BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                            IsStatic = false,
                            ParameterTypes = new[]
                            {
                                typeof(BaseItem),
                                typeof(ItemImageInfo[])
                            },
                            ReturnType = typeof(string[])
                        },
                        logger,
                        "EpisodeBackdropFallback.DtoService.GetImageTags");

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
                        "EpisodeBackdropFallback.DtoService.GetImageCacheTag");

                    if (getBaseItemDtoInternal == null || getImageTags == null || getImageCacheTag == null)
                    {
                        PatchLog.InitFailed(logger, nameof(EpisodeBackdropFallback), "DTO 图片相关方法缺失");
                        return;
                    }

                    harmony = new Harmony("mediainfokeeper.episodebackdropfallback");
                    PatchLog.Patched(logger, nameof(EpisodeBackdropFallback), getBaseItemDtoInternal);

                    if (isEnabled)
                    {
                        Patch();
                    }
                }
                catch (Exception ex)
                {
                    PatchLog.InitFailed(logger, nameof(EpisodeBackdropFallback), ex.Message);
                    logger?.Error("EpisodeBackdropFallback 初始化异常：{0}", ex);
                    harmony = null;
                    isEnabled = false;
                }
            }
        }

        public static void Configure(bool enable)
        {
            lock (InitLock)
            {
                isEnabled = enable;

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
                postfix: new HarmonyMethod(typeof(EpisodeBackdropFallback), nameof(GetBaseItemDtoInternalPostfix)));
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
            DtoOptions options,
            ref BaseItemDto __result)
        {
            if (!isEnabled || __instance == null || item == null || __result == null)
            {
                return;
            }

            if (!(item is Episode episode))
            {
                return;
            }

            if (episode.GetImageInfo(ImageType.Primary, 0) != null)
            {
                return;
            }

            var series = episode.Series;
            if (series == null)
            {
                return;
            }

            var backdropLimit = Math.Max(1, options?.GetImageLimit(ImageType.Backdrop) ?? 0);
            var seriesBackdropImages = series
                .GetImages(ImageType.Backdrop)
                .Where(image => image != null)
                .Take(backdropLimit)
                .ToArray();

            if (seriesBackdropImages.Length > 0)
            {
                var backdropTags = GetBackdropTags(__instance, series, seriesBackdropImages);
                if (backdropTags != null && backdropTags.Length > 0)
                {
                    __result.SeriesPrimaryImageTag = null;
                    __result.PrimaryImageItemId = null;
                    __result.PrimaryImageTag = null;

                    __result.ParentBackdropItemId = series.GetClientId();
                    __result.ParentBackdropImageTags = backdropTags;

                    if (!__result.PrimaryImageAspectRatio.HasValue || __result.PrimaryImageAspectRatio.Value < 1.0)
                    {
                        __result.PrimaryImageAspectRatio = GetBackdropAspectRatio(seriesBackdropImages[0]);
                    }

                    return;
                }
            }

            var seriesPrimaryImage = series.GetImageInfo(ImageType.Primary, 0);
            if (seriesPrimaryImage == null)
            {
                return;
            }

            string seriesPrimaryTag;
            try
            {
                seriesPrimaryTag = getImageCacheTag?.Invoke(__instance, new object[] { series, seriesPrimaryImage }) as string;
            }
            catch (Exception ex)
            {
                logger?.Debug("EpisodeBackdropFallback.GetImageCacheTag(single) failed: {0}", ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(seriesPrimaryTag))
            {
                return;
            }

            __result.PrimaryImageItemId = series.GetClientId();
            __result.PrimaryImageTag = seriesPrimaryTag;
            __result.SeriesPrimaryImageTag = seriesPrimaryTag;

            if (!__result.PrimaryImageAspectRatio.HasValue || __result.PrimaryImageAspectRatio.Value < 1.0)
            {
                __result.PrimaryImageAspectRatio = GetBackdropAspectRatio(seriesPrimaryImage);
            }
        }

        private static string[] GetBackdropTags(object dtoService, BaseItem series, ItemImageInfo[] backdropImages)
        {
            try
            {
                var tags = getImageTags?.Invoke(dtoService, new object[] { series, backdropImages }) as string[];
                if (tags != null && tags.Length > 0)
                {
                    return tags;
                }
            }
            catch (Exception ex)
            {
                logger?.Debug("EpisodeBackdropFallback.GetImageTags failed: {0}", ex.Message);
            }

            try
            {
                return backdropImages
                    .Select(image => getImageCacheTag?.Invoke(dtoService, new object[] { series, image }) as string)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .ToArray();
            }
            catch (Exception ex)
            {
                logger?.Debug("EpisodeBackdropFallback.GetImageCacheTag failed: {0}", ex.Message);
                return Array.Empty<string>();
            }
        }

        private static double GetBackdropAspectRatio(ItemImageInfo imageInfo)
        {
            if (imageInfo != null && imageInfo.Width > 0 && imageInfo.Height > 0)
            {
                return imageInfo.Width / imageInfo.Height;
            }

            return 16d / 9d;
        }
    }
}
