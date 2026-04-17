using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaInfoKeeper.Services;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    internal static class MediaInfoClearGuard
    {
        private sealed class AllowSaveScope : IDisposable
        {
            private readonly int previousScopeCount;

            public AllowSaveScope(int previousScopeCount)
            {
                this.previousScopeCount = previousScopeCount;
            }

            public void Dispose()
            {
                AllowScopeCount.Value = previousScopeCount;
            }
        }

        private static readonly AsyncLocal<int> AllowScopeCount = new AsyncLocal<int>();
        private static Harmony harmony;
        private static MethodInfo saveMediaStreamsMethod;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && saveMediaStreamsMethod != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enabled)
        {
            if (harmony != null)
            {
                Configure(enabled);
                return;
            }

            logger = pluginLogger;
            isEnabled = enabled;
            if (!enabled)
            {
                return;
            }

            try
            {
                var embyServerImplementations = Assembly.Load("Emby.Server.Implementations");
                var sqliteItemRepositoryType =
                    embyServerImplementations?.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
                if (sqliteItemRepositoryType == null)
                {
                    PatchLog.InitFailed(logger, nameof(MediaInfoClearGuard), "未找到 SqliteItemRepository 类型");
                    return;
                }

                var assemblyVersion = embyServerImplementations.GetName().Version;
                saveMediaStreamsMethod = PatchMethodResolver.Resolve(
                    sqliteItemRepositoryType,
                    assemblyVersion,
                    new MethodSignatureProfile
                    {
                        Name = "sqliteitemrepository-savemediastreams-exact",
                        MethodName = "SaveMediaStreams",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[]
                        {
                            typeof(long),
                            typeof(List<MediaStream>),
                            typeof(CancellationToken),
                        }
                    },
                    logger,
                    "MediaInfoClearGuard.SaveMediaStreams");

                if (saveMediaStreamsMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(MediaInfoClearGuard), "未命中 SaveMediaStreams");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.ffprobe-strm-empty-media-guard");
                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, nameof(MediaInfoClearGuard), ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                saveMediaStreamsMethod = null;
                isPatched = false;
            }
        }

        public static void Configure(bool enabled)
        {
            isEnabled = enabled;
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

        public static IDisposable Allow()
        {
            var previousScopeCount = AllowScopeCount.Value;
            AllowScopeCount.Value = previousScopeCount + 1;
            return new AllowSaveScope(previousScopeCount);
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || saveMediaStreamsMethod == null)
            {
                return;
            }

            PatchLog.Patched(logger, nameof(MediaInfoClearGuard), saveMediaStreamsMethod);
            harmony.Patch(
                saveMediaStreamsMethod,
                prefix: new HarmonyMethod(typeof(MediaInfoClearGuard), nameof(SaveMediaStreamsPrefix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || saveMediaStreamsMethod == null)
            {
                return;
            }

            harmony.Unpatch(saveMediaStreamsMethod, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        private static bool SaveMediaStreamsPrefix(long itemId, List<MediaStream> streams)
        {
            if (!isEnabled)
            {
                return true;
            }

            if (AllowScopeCount.Value > 0)
            {
                return true;
            }

            var item = Plugin.LibraryManager?.GetItemById(itemId);
            var itemPath = item?.Path ?? item?.FileName;

            if (item == null || !LibraryService.IsFileShortcut(itemPath) || !WillClearMediaInfo(streams))
            {
                return true;
            }

            logger?.Info($"已阻止媒体信息丢失: {item.FileName ?? item.Path}");
            return false;
        }

        private static bool WillClearMediaInfo(List<MediaStream> streams)
        {
            return streams == null || !streams.Any(stream => stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio);
        }
    }
}
