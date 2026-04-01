using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Notifications;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 接管部分库变更通知内容，并在深度删除后补发自定义通知。
    /// </summary>
    public static class NotificationSystem
    {
        private static Harmony harmony;
        private static ILogger logger;
        private static bool isPatched;
        private static MethodInfo convertToGroups;
        private static MethodInfo sendNotification;
        private static MethodInfo queueNotification;
        private static MethodInfo deleteItem;

        private static readonly AsyncLocal<Dictionary<long, List<(int? IndexNumber, int? ParentIndexNumber)>>>
            GroupDetails = new AsyncLocal<Dictionary<long, List<(int? IndexNumber, int? ParentIndexNumber)>>>();
        private static readonly AsyncLocal<string> Description = new AsyncLocal<string>();
        private static readonly AsyncLocal<bool> AllowCustomLibraryNew = new AsyncLocal<bool>();

        private sealed class ScopeGuard : IDisposable
        {
            private readonly Action onDispose;

            public ScopeGuard(Action onDisposeAction)
            {
                onDispose = onDisposeAction;
            }

            public void Dispose()
            {
                onDispose?.Invoke();
            }
        }

        public static bool IsReady => harmony != null && isPatched;

        public static IDisposable BeginCustomLibraryNewScope()
        {
            var previous = AllowCustomLibraryNew.Value;
            AllowCustomLibraryNew.Value = true;
            return new ScopeGuard(() => AllowCustomLibraryNew.Value = previous);
        }

        private static bool IsTakeOverLibraryNewEnabled()
        {
            return Plugin.Instance?.Options?.MainPage?.PlugginEnabled == true &&
                   Plugin.Instance.Options.Enhance?.TakeOverSystemLibraryNew == true;
        }

        public static void Initialize(ILogger pluginLogger)
        {
            if (harmony != null)
            {
                return;
            }

            logger = pluginLogger;

            try
            {
                var notificationsAssembly = Assembly.Load("Emby.Notifications");
                var notificationsVersion = notificationsAssembly?.GetName().Version;
                var notificationManagerType = notificationsAssembly?.GetType("Emby.Notifications.NotificationManager");
                var notificationQueueManagerType =
                    notificationsAssembly?.GetType("Emby.Notifications.NotificationQueueManager");

                convertToGroups = PatchMethodResolver.Resolve(
                    notificationManagerType,
                    notificationsVersion,
                    new MethodSignatureProfile
                    {
                        Name = "notificationmanager-converttogroups-exact",
                        MethodName = "ConvertToGroups",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(ItemChangeEventArgs[]) }
                    },
                    logger,
                    "NotificationSystem.ConvertToGroups");

                sendNotification = PatchMethodResolver.Resolve(
                    notificationManagerType,
                    notificationsVersion,
                    new MethodSignatureProfile
                    {
                        Name = "notificationmanager-sendnotification-exact",
                        MethodName = "SendNotification",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(INotifier), typeof(NotificationInfo[]), typeof(NotificationRequest), typeof(bool)
                        }
                    },
                    logger,
                    "NotificationSystem.SendNotification");

                queueNotification = PatchMethodResolver.Resolve(
                    notificationQueueManagerType,
                    notificationsVersion,
                    new MethodSignatureProfile
                    {
                        Name = "notificationqueuemanager-queuenotification-exact",
                        MethodName = "QueueNotification",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(INotifier), typeof(InternalNotificationRequest), typeof(int) }
                    },
                    logger,
                    "NotificationSystem.QueueNotification");

                var implAssembly = Assembly.Load("Emby.Server.Implementations");
                var implVersion = implAssembly?.GetName().Version;
                var libraryManagerType = implAssembly?.GetType("Emby.Server.Implementations.Library.LibraryManager");

                deleteItem = PatchMethodResolver.Resolve(
                    libraryManagerType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "librarymanager-deleteitem-4args-exact",
                        MethodName = "DeleteItem",
                        BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                        IsStatic = false,
                        ParameterTypes = new[] { typeof(BaseItem), typeof(DeleteOptions), typeof(BaseItem), typeof(bool) },
                        ReturnType = typeof(void)
                    },
                    logger,
                    "NotificationSystem.DeleteItem(4)");

                if (convertToGroups == null || sendNotification == null || queueNotification == null || deleteItem == null)
                {
                    PatchLog.InitFailed(logger, nameof(NotificationSystem), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.enhancenotification");
                Patch();
            }
            catch (Exception e)
            {
                logger?.Error("NotificationSystem 初始化失败。");
                logger?.Error(e.Message);
                logger?.Error(e.ToString());
                harmony = null;
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(convertToGroups,
                postfix: new HarmonyMethod(typeof(NotificationSystem), nameof(ConvertToGroupsPostfix)));
            harmony.Patch(sendNotification,
                prefix: new HarmonyMethod(typeof(NotificationSystem), nameof(SendNotificationPrefix)));
            harmony.Patch(queueNotification,
                prefix: new HarmonyMethod(typeof(NotificationSystem), nameof(QueueNotificationPrefix)));
            harmony.Patch(deleteItem,
                prefix: new HarmonyMethod(typeof(NotificationSystem), nameof(DeleteItemPrefix)),
                finalizer: new HarmonyMethod(typeof(NotificationSystem), nameof(DeleteItemFinalizer)));

            isPatched = true;
        }

        [HarmonyPostfix]
        private static void ConvertToGroupsPostfix(ItemChangeEventArgs[] list,
            ref Dictionary<long, List<ItemChangeEventArgs>> __result)
        {
            var filteredItems = list.Where(i => i.Item.SeriesId != 0L).ToArray();

            if (filteredItems.Length == 0)
            {
                return;
            }

            GroupDetails.Value = filteredItems.GroupBy(i => i.Item.SeriesId)
                .ToDictionary(g => g.Key, g => g.Select(i => (i.Item.IndexNumber, i.Item.ParentIndexNumber)).ToList());
        }

        [HarmonyPrefix]
        private static bool SendNotificationPrefix(INotifier notifier, NotificationInfo[] notifications,
            NotificationRequest request, bool enableUserDataInDto)
        {
            if (IsTakeOverLibraryNewEnabled() &&
                string.Equals(request?.EventId, "library.new", StringComparison.OrdinalIgnoreCase) &&
                !AllowCustomLibraryNew.Value)
            {
                return false;
            }

            if (notifications.FirstOrDefault()?.GroupItems is true
                && request.Item is Series series && GroupDetails.Value != null
                && GroupDetails.Value.TryGetValue(series.InternalId, out var groupDetails))
            {
                var groupedBySeason = groupDetails.Where(e => e.ParentIndexNumber.HasValue)
                    .GroupBy(e => e.ParentIndexNumber)
                    .OrderBy(g => g.Key)
                    .ToList();

                var descriptions = new List<string>();

                foreach (var seasonGroup in groupedBySeason)
                {
                    var seasonIndex = seasonGroup.Key;
                    var episodesBySeason = seasonGroup
                        .Where(e => e.IndexNumber.HasValue)
                        .OrderBy(e => e.IndexNumber.Value)
                        .Select(e => e.IndexNumber.Value)
                        .Distinct()
                        .ToList();

                    if (!episodesBySeason.Any())
                    {
                        continue;
                    }

                    var episodeRanges = new List<string>();
                    var rangeStart = episodesBySeason[0];
                    var lastEpisodeInRange = rangeStart;

                    for (var i = 1; i < episodesBySeason.Count; i++)
                    {
                        var current = episodesBySeason[i];
                        if (current != lastEpisodeInRange + 1)
                        {
                            episodeRanges.Add(rangeStart == lastEpisodeInRange
                                ? $"E{rangeStart:D2}"
                                : $"E{rangeStart:D2}-E{lastEpisodeInRange:D2}");
                            rangeStart = current;
                        }

                        lastEpisodeInRange = current;
                    }

                    episodeRanges.Add(rangeStart == lastEpisodeInRange
                        ? $"E{rangeStart:D2}"
                        : $"E{rangeStart:D2}-E{lastEpisodeInRange:D2}");

                    descriptions.Add($"S{seasonIndex:D2} {string.Join(", ", episodeRanges)}");
                }

                var summary = string.Join(" / ", descriptions);

                var tmdbId = series.GetProviderId(MetadataProviders.Tmdb);

                if (!string.IsNullOrEmpty(tmdbId))
                {
                    summary += $"{Environment.NewLine}{Environment.NewLine}TmdbId: {tmdbId}";
                }

                Description.Value = summary;
            }

            return true;
        }

        [HarmonyPrefix]
        private static void QueueNotificationPrefix(INotifier sender, InternalNotificationRequest request, int priority)
        {
            if (!string.IsNullOrEmpty(Description.Value))
            {
                request.Description = Description.Value;
                Description.Value = null;
            }
        }

        [HarmonyPrefix]
        private static void DeleteItemPrefix(ILibraryManager __instance, BaseItem item, DeleteOptions options,
            BaseItem parent, bool notifyParentItem, out Dictionary<string, bool> __state)
        {
            __state = null;

            if (options.DeleteFileLocation && Plugin.LibraryService != null)
            {
                var collectionFolder = options.CollectionFolders ?? __instance.GetCollectionFolders(item);
                var scope = item.GetDeletePaths(true, collectionFolder).Select(i => i.FullName).ToArray();

                __state = Plugin.LibraryService.PrepareDeepDelete(item, scope);
            }
        }

        [HarmonyFinalizer]
        private static void DeleteItemFinalizer(Exception __exception, BaseItem item, Dictionary<string, bool> __state)
        {
            if (__state != null && __state.Count > 0 && __exception is null && Plugin.NotificationApi != null)
            {
                Task.Run(() =>
                        Plugin.NotificationApi.DeepDeleteSendNotification(item, new HashSet<string>(__state.Keys)))
                    .ConfigureAwait(false);
            }
        }
    }
}
