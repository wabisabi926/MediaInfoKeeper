using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 在播放信息与直播打开入口期间临时放行 ffprobe/ffmpeg，不拦截视频与直播启播分析媒体信息。
    /// </summary>
    public static class PlaybackFfprocess
    {
        private static Harmony harmony;
        private static MethodInfo playbackInfoEntry;
        private static MethodInfo openLiveStreamEntry;
        private static ILogger logger;

        public static bool IsReady => harmony != null && playbackInfoEntry != null && openLiveStreamEntry != null;

        public static void Initialize(ILogger pluginLogger, bool enabled)
        {
            if (harmony != null)
            {
                return;
            }

            logger = pluginLogger;
            if (!enabled)
            {
                return;
            }

            try
            {
                var mediaEncoding = Assembly.Load("Emby.Server.MediaEncoding");
                var serverImplementations = Assembly.Load("Emby.Server.Implementations");
                var mediaInfoServiceType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.MediaInfoService");
                var getPostedPlaybackInfoType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.GetPostedPlaybackInfo");
                var mediaSourceManagerType = serverImplementations?.GetType("Emby.Server.Implementations.Library.MediaSourceManager");

                if (mediaInfoServiceType == null || getPostedPlaybackInfoType == null)
                {
                    PatchLog.InitFailed(logger, nameof(PlaybackFfprocess), "未找到 MediaInfoService/GetPostedPlaybackInfo 类型");
                    return;
                }

                if (mediaSourceManagerType == null)
                {
                    PatchLog.InitFailed(logger, nameof(PlaybackFfprocess), "未找到 MediaSourceManager 类型");
                    return;
                }

                playbackInfoEntry = PatchMethodResolver.Resolve(
                    mediaInfoServiceType,
                    mediaEncoding.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "playbackinfo-entry-exact",
                        MethodName = "GetPlaybackInfo",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[]
                        {
                            getPostedPlaybackInfoType,
                            typeof(bool),
                            typeof(string),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<>).MakeGenericType(typeof(PlaybackInfoResponse))
                    },
                    logger,
                    "PlaybackFfprocessAllowance.MediaInfoService.GetPlaybackInfo");
                openLiveStreamEntry = PatchMethodResolver.Resolve(
                    mediaSourceManagerType,
                    serverImplementations.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "openlivestream-entry-exact",
                        MethodName = "OpenLiveStream",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[]
                        {
                            typeof(LiveStreamRequest),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<>).MakeGenericType(typeof(LiveStreamResponse))
                    },
                    logger,
                    "PlaybackFfprocessAllowance.MediaSourceManager.OpenLiveStream");

                if (playbackInfoEntry == null || openLiveStreamEntry == null)
                {
                    PatchLog.InitFailed(logger, nameof(PlaybackFfprocess), "播放入口目标方法缺失");
                    return;
                }

                PatchLog.Patched(logger, nameof(PlaybackFfprocess), playbackInfoEntry);
                PatchLog.Patched(logger, nameof(PlaybackFfprocess), openLiveStreamEntry);

                harmony = new Harmony("mediainfokeeper.playbackffprocess");
                harmony.Patch(
                    playbackInfoEntry,
                    prefix: new HarmonyMethod(typeof(PlaybackFfprocess), nameof(PlaybackInfoPrefix)),
                    postfix: new HarmonyMethod(typeof(PlaybackFfprocess), nameof(AsyncPostfix)));
                harmony.Patch(
                    openLiveStreamEntry,
                    prefix: new HarmonyMethod(typeof(PlaybackFfprocess), nameof(OpenLiveStreamPrefix)),
                    postfix: new HarmonyMethod(typeof(PlaybackFfprocess), nameof(AsyncPostfix)));
            }
            catch (Exception ex)
            {
                logger?.Error("PlaybackFfprocessAllowance 初始化失败");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
            }
        }

        public static void Configure(bool enabled)
        {
            // 跟随 FfProcessGuard 启用状态，此补丁当前为一次性安装。
        }

        private static void PlaybackInfoPrefix(object __0, out FfProcessGuard.AllowanceHandle __state)
        {
            var requestType = __0?.GetType();
            var itemId = requestType?.GetProperty("Id")?.GetValue(__0) as string;
            __state = FfProcessGuard.BeginAllow(CreatePlaybackContext(ParseItemId(itemId)));
        }

        private static void OpenLiveStreamPrefix(LiveStreamRequest __0, out FfProcessGuard.AllowanceHandle __state)
        {
            __state = FfProcessGuard.BeginAllow();
        }

        private static void AsyncPostfix(ref object __result, FfProcessGuard.AllowanceHandle __state)
        {
            if (__state == null)
            {
                return;
            }

            if (__result is Task task)
            {
                __result = AwaitWithScope(task, __state);
                return;
            }

            FfProcessGuard.EndAllow(__state);
        }

        private static object AwaitWithScope(Task task, FfProcessGuard.AllowanceHandle allowance)
        {
            var taskType = task.GetType();
            if (taskType == typeof(Task))
            {
                return AwaitTask(task, allowance);
            }

            if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = taskType.GetGenericArguments()[0];
                var method = typeof(PlaybackFfprocess)
                    .GetMethod(nameof(AwaitGenericTask), BindingFlags.Static | BindingFlags.NonPublic)
                    ?.MakeGenericMethod(resultType);
                return method?.Invoke(null, new object[] { task, allowance }) ?? task;
            }

            return task;
        }

        private static async Task AwaitTask(Task task, FfProcessGuard.AllowanceHandle allowance)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                FfProcessGuard.EndAllow(allowance);
            }
        }

        private static async Task<T> AwaitGenericTask<T>(Task<T> task, FfProcessGuard.AllowanceHandle allowance)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                FfProcessGuard.EndAllow(allowance);
            }
        }

        private static long? ParseItemId(string itemId)
        {
            return long.TryParse(itemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedItemId)
                ? parsedItemId
                : null;
        }

        private static FfProcessGuard.AllowanceContext CreatePlaybackContext(long? itemId)
        {
            BaseItem item = null;
            if (itemId.GetValueOrDefault() > 0)
            {
                item = Plugin.LibraryManager?.GetItemById(itemId.Value);
            }

            var itemInternalId = item?.InternalId > 0
                ? item.InternalId
                : itemId.GetValueOrDefault();

            return new FfProcessGuard.AllowanceContext
            {
                ItemInternalId = itemInternalId,
                ItemPath = item?.Path ?? item?.FileName,
                AllowFfprocess = true
            };
        }

    }
}
