using System;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 将插件的“通知媒体库刷新（秒）”配置同步到 Emby 的 LibraryMonitor 等待时间。
    /// </summary>
    internal static class LibraryMonitorDelay
    {
        private static readonly object SyncRoot = new object();

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo getterMethod;
        private static bool isEnabled;
        private static int overrideDelaySeconds = -1;
        private static bool isPatched;

        public static bool IsReady => harmony != null && getterMethod != null && isPatched;

        public static void Initialize(ILogger pluginLogger, bool enable, int delaySeconds)
        {
            lock (SyncRoot)
            {
                if (harmony != null)
                {
                    Configure(enable, delaySeconds);
                    return;
                }

                logger = pluginLogger;
                isEnabled = enable;
                overrideDelaySeconds = delaySeconds;

                try
                {
                    var mediaBrowserModelAssembly = typeof(ServerConfiguration).Assembly;
                    var serverConfigurationType = typeof(ServerConfiguration);
                    getterMethod = PatchMethodResolver.Resolve(
                        serverConfigurationType,
                        mediaBrowserModelAssembly.GetName().Version,
                        new MethodSignatureProfile
                        {
                            Name = "serverconfiguration-get-librarymonitordelayseconds-exact",
                            MethodName = "get_LibraryMonitorDelaySeconds",
                            BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                            ParameterTypes = Type.EmptyTypes,
                            ReturnType = typeof(int),
                            IsStatic = false
                        },
                        logger,
                        "LibraryMonitorDelay.ServerConfiguration.get_LibraryMonitorDelaySeconds");

                    if (getterMethod == null)
                    {
                        PatchLog.InitFailed(logger, nameof(LibraryMonitorDelay), "ServerConfiguration.get_LibraryMonitorDelaySeconds 未找到");
                        return;
                    }

                    harmony = new Harmony("mediainfokeeper.librarymonitordelay");
                    Patch();
                }
                catch (Exception ex)
                {
                    logger?.Error("LibraryMonitorDelay 初始化失败。");
                    logger?.Error(ex.Message);
                    logger?.Error(ex.ToString());
                    harmony = null;
                    getterMethod = null;
                }
            }
        }

        public static void Configure(bool enable, int delaySeconds)
        {
            lock (SyncRoot)
            {
                isEnabled = enable;
                overrideDelaySeconds = delaySeconds;

                if (harmony == null || getterMethod == null)
                {
                    return;
                }

                Patch();
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || getterMethod == null)
            {
                return;
            }

            harmony.Patch(
                getterMethod,
                postfix: new HarmonyMethod(typeof(LibraryMonitorDelay), nameof(GetLibraryMonitorDelaySecondsPostfix)));
            PatchLog.Patched(logger, nameof(LibraryMonitorDelay), getterMethod);
            isPatched = true;
        }

        [HarmonyPostfix]
        private static void GetLibraryMonitorDelaySecondsPostfix(ref int __result)
        {
            if (!isEnabled || overrideDelaySeconds < 0)
            {
                return;
            }

            __result = Math.Max(0, overrideDelaySeconds);
        }
    }
}
