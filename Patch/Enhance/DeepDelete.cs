using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 在删除媒体项成功后，按挂载路径执行本地深度清理。
    /// </summary>
    public static class DeepDelete
    {
        private sealed class DeepDeleteState
        {
            public Dictionary<string, bool> MountPaths { get; set; }
        }

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;
        private static MethodInfo deleteItem4Args;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            if (harmony != null)
            {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try
            {
                var implAssembly = Assembly.Load("Emby.Server.Implementations");
                var libraryManagerType = implAssembly?.GetType("Emby.Server.Implementations.Library.LibraryManager");
                var version = implAssembly?.GetName().Version;

                deleteItem4Args = PatchMethodResolver.Resolve(
                    libraryManagerType,
                    version,
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
                    "DeepDelete.DeleteItem(4)");

                if (deleteItem4Args == null)
                {
                    PatchLog.InitFailed(logger, nameof(DeepDelete), "DeleteItem(4) 目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.deepdelete");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception e)
            {
                logger?.Error("DeepDelete 初始化失败。");
                logger?.Error(e.Message);
                logger?.Error(e.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool enable)
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

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            if (deleteItem4Args != null)
            {
                harmony.Patch(
                    deleteItem4Args,
                    prefix: new HarmonyMethod(typeof(DeepDelete), nameof(DeleteItem4Prefix)),
                    finalizer: new HarmonyMethod(typeof(DeepDelete), nameof(DeleteItemFinalizer)));
            }

            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            if (deleteItem4Args != null)
            {
                harmony.Unpatch(deleteItem4Args, HarmonyPatchType.Prefix, harmony.Id);
                harmony.Unpatch(deleteItem4Args, HarmonyPatchType.Finalizer, harmony.Id);
            }

            isPatched = false;
        }

        [HarmonyPrefix]
        private static bool DeleteItem4Prefix(ILibraryManager __instance, BaseItem item, DeleteOptions options,
            BaseItem parent, bool notifyParentItem, out DeepDeleteState __state)
        {
            __state = PrepareState(__instance, item, options);
            return true;
        }

        [HarmonyFinalizer]
        private static void DeleteItemFinalizer(Exception __exception, DeepDeleteState __state)
        {
            if (__exception != null)
            {
                logger?.Warn("DeepDelete - DeleteItem 发生异常，跳过深度删除。");
                logger?.Warn(__exception.ToString());
                return;
            }

            if (Plugin.LibraryService == null)
            {
                logger?.Warn("DeepDelete - LibraryService 不可用，跳过深度删除。");
                return;
            }

            if (__state?.MountPaths == null || __state.MountPaths.Count == 0)
            {
                logger?.Debug("DeepDelete - 未收集到可处理的挂载路径，跳过深度删除。");
                return;
            }

            Task.Run(() =>
            {
                var allCount = __state.MountPaths.Count;
                var localMountPaths = new HashSet<string>(__state.MountPaths
                    .Where(kv => kv.Value)
                    .Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
                var remoteCount = allCount - localMountPaths.Count;

                logger?.Debug("DeepDelete - 挂载路径统计: total={0}, local={1}, remote={2}",
                    allCount, localMountPaths.Count, remoteCount);

                if (localMountPaths.Count > 0)
                {
                    logger?.Debug("DeepDelete - 开始执行本地深度删除。");
                    Plugin.LibraryService.ExecuteDeepDelete(localMountPaths);
                }
                else
                {
                    logger?.Debug("DeepDelete - 仅检测到远程路径，跳过本地深度删除。");
                }
            }).ConfigureAwait(false);
        }

        private static DeepDeleteState PrepareState(ILibraryManager libraryManager, BaseItem item, DeleteOptions options)
        {
            if (!isEnabled)
            {
                return null;
            }

            if (options == null || item == null || Plugin.LibraryService == null)
            {
                return null;
            }

            if (!options.DeleteFileLocation)
            {
                logger?.Debug("DeepDelete - DeleteFileLocation=false，跳过深度删除准备。Item={0}", item.Name);
                return null;
            }

            try
            {
                var collectionFolders = options.CollectionFolders ?? libraryManager.GetCollectionFolders(item);
                var scope = item.GetDeletePaths(true, collectionFolders).Select(i => i.FullName).ToArray();
                var mountPaths = Plugin.LibraryService.PrepareDeepDelete(item, scope);
                var localCount = mountPaths.Count(kv => kv.Value);
                var remoteCount = mountPaths.Count - localCount;

                logger?.Debug("DeepDelete - 准备完成。Item={0}, scope={1}, mountPaths={2}, local={3}, remote={4}",
                    item.Name, scope.Length, mountPaths.Count, localCount, remoteCount);

                return new DeepDeleteState
                {
                    MountPaths = mountPaths
                };
            }
            catch (Exception ex)
            {
                logger?.Error("DeepDelete - PrepareDeepDelete 失败: {0}", ex.Message);
                logger?.Debug(ex.StackTrace);
                return null;
            }
        }
    }
}
