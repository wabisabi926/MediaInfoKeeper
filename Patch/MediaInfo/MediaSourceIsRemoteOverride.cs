using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Options;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 在 Emby 重新生成静态/播放媒体源后，按插件配置覆盖 IsRemote。
    /// </summary>
    public static class MediaSourceIsRemoteOverride
    {
        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;

        private static MethodInfo getStaticMediaSourcesMethod;
        private static MethodInfo getPlayackMediaSourcesMethod;

        public static bool IsReady => harmony != null && isPatched &&
                                      getStaticMediaSourcesMethod != null &&
                                      getPlayackMediaSourcesMethod != null;

        public static bool IsOverrideEnabled()
        {
            return GetOverrideOption() != MediaInfoOptions.IsRemoteOverrideOption.EmbyDefault;
        }

        public static void Initialize(ILogger pluginLogger, bool enabled)
        {
            if (harmony != null)
            {
                Configure(enabled);
                return;
            }

            logger = pluginLogger;
            isEnabled = enabled;

            try
            {
                var serverImplementations = Assembly.Load("Emby.Server.Implementations");
                var mediaSourceManagerType =
                    serverImplementations?.GetType("Emby.Server.Implementations.Library.MediaSourceManager", false);
                var controllerAssembly = Assembly.Load("MediaBrowser.Controller");
                var modelAssembly = Assembly.Load("MediaBrowser.Model");

                var baseItemType = controllerAssembly?.GetType("MediaBrowser.Controller.Entities.BaseItem", false);
                var userType = controllerAssembly?.GetType("MediaBrowser.Controller.Entities.User", false);
                var libraryOptionsType = modelAssembly?.GetType("MediaBrowser.Model.Configuration.LibraryOptions", false);
                var deviceProfileType = modelAssembly?.GetType("MediaBrowser.Model.Dlna.DeviceProfile", false);

                if (mediaSourceManagerType == null ||
                    baseItemType == null ||
                    userType == null ||
                    libraryOptionsType == null ||
                    deviceProfileType == null)
                {
                    PatchLog.InitFailed(logger, nameof(MediaSourceIsRemoteOverride), "MediaSourceManager 相关类型未找到");
                    return;
                }

                var implVersion = serverImplementations.GetName().Version;
                getStaticMediaSourcesMethod = PatchMethodResolver.Resolve(
                    mediaSourceManagerType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "mediasourcemanager-getstaticmediasources-exact",
                        MethodName = "GetStaticMediaSources",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            baseItemType,
                            typeof(bool),
                            typeof(bool),
                            typeof(bool),
                            typeof(bool),
                            baseItemType.MakeArrayType(),
                            libraryOptionsType,
                            deviceProfileType,
                            userType,
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(List<MediaSourceInfo>)
                    },
                    logger,
                    "MediaSourceIsRemoteOverride.GetStaticMediaSources");

                getPlayackMediaSourcesMethod = PatchMethodResolver.Resolve(
                    mediaSourceManagerType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "mediasourcemanager-getplayackmediasources-exact",
                        MethodName = "GetPlayackMediaSources",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            baseItemType,
                            userType,
                            typeof(bool),
                            typeof(string),
                            typeof(bool),
                            typeof(bool),
                            deviceProfileType,
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<>).MakeGenericType(typeof(List<MediaSourceInfo>))
                    },
                    logger,
                    "MediaSourceIsRemoteOverride.GetPlayackMediaSources");

                if (getStaticMediaSourcesMethod == null || getPlayackMediaSourcesMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(MediaSourceIsRemoteOverride), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.isremoteoverride");
                Patch();
            }
            catch (Exception ex)
            {
                logger?.Error("MediaSourceIsRemoteOverride 初始化失败");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
            }
        }

        public static void Configure(bool enabled)
        {
            isEnabled = enabled;
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(
                getStaticMediaSourcesMethod,
                postfix: new HarmonyMethod(typeof(MediaSourceIsRemoteOverride), nameof(GetStaticMediaSourcesPostfix)));
            harmony.Patch(
                getPlayackMediaSourcesMethod,
                postfix: new HarmonyMethod(typeof(MediaSourceIsRemoteOverride), nameof(GetPlayackMediaSourcesPostfix)));

            PatchLog.Patched(logger, nameof(MediaSourceIsRemoteOverride), getStaticMediaSourcesMethod);
            PatchLog.Patched(logger, nameof(MediaSourceIsRemoteOverride), getPlayackMediaSourcesMethod);
            isPatched = true;
        }

        [HarmonyPostfix]
        private static void GetStaticMediaSourcesPostfix(BaseItem __0, ref List<MediaSourceInfo> __result)
        {
            if (!ShouldOverride(__0))
            {
                return;
            }

            Apply(__result);
        }

        [HarmonyPostfix]
        private static void GetPlayackMediaSourcesPostfix(BaseItem __0, ref object __result)
        {
            if (!ShouldOverride(__0) || __result is not Task<List<MediaSourceInfo>> mediaSourcesTask)
            {
                return;
            }

            __result = AwaitAndApply(mediaSourcesTask);
        }

        private static async Task<List<MediaSourceInfo>> AwaitAndApply(Task<List<MediaSourceInfo>> task)
        {
            var mediaSources = await task.ConfigureAwait(false);
            Apply(mediaSources);
            return mediaSources;
        }

        private static bool ShouldOverride(BaseItem item)
        {
            if (!isEnabled || item == null)
            {
                return false;
            }

            return LibraryService.IsFileShortcut(item.Path ?? item.FileName);
        }

        private static MediaInfoOptions.IsRemoteOverrideOption GetOverrideOption()
        {
            var configuredMode = Plugin.Instance?.Options?.GetMediaInfoOptions()?.IsRemoteOverride;
            if (!Enum.TryParse(configuredMode, true, out MediaInfoOptions.IsRemoteOverrideOption overrideOption))
            {
                overrideOption = MediaInfoOptions.IsRemoteOverrideOption.EmbyDefault;
            }

            return overrideOption;
        }

        private static void Apply(IEnumerable<MediaSourceInfo> mediaSources)
        {
            if (mediaSources == null)
            {
                return;
            }

            foreach (var mediaSource in mediaSources)
            {
                Apply(mediaSource);
            }
        }

        private static void Apply(MediaSourceInfo mediaSourceInfo)
        {
            if (mediaSourceInfo == null)
            {
                return;
            }

            switch (GetOverrideOption())
            {
                case MediaInfoOptions.IsRemoteOverrideOption.ForceTrue:
                    mediaSourceInfo.IsRemote = true;
                    break;
                case MediaInfoOptions.IsRemoteOverrideOption.ForceFalse:
                    mediaSourceInfo.IsRemote = false;
                    break;
            }
        }
    }
}
