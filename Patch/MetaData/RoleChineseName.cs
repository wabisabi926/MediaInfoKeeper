using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Patch
{
    public static class RoleChineseName
    {
        private static readonly object InitLock = new object();

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo updatePeopleMethod;
        private static bool isEnabled;
        private static bool isPatched;

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

            lock (InitLock)
            {
                if (harmony != null)
                {
                    Configure(enable);
                    return;
                }

                var libraryManagerType = Plugin.LibraryManager?.GetType() ?? Type.GetType("Emby.Server.Implementations.Library.LibraryManager, Emby.Server.Implementations");
                if (libraryManagerType == null)
                {
                    PatchLog.InitFailed(logger, nameof(RoleChineseName), "LibraryManager 类型缺失");
                    return;
                }

                var version = libraryManagerType.Assembly.GetName().Version;
                updatePeopleMethod = PatchMethodResolver.Resolve(
                    libraryManagerType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "librarymanager-updatepeople-exact",
                        MethodName = "UpdatePeople",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[] { typeof(BaseItem), typeof(List<PersonInfo>), typeof(bool) },
                        ReturnType = typeof(void),
                        IsStatic = false
                    },
                    logger,
                    "RoleChineseName.LibraryManager.UpdatePeople");

                if (updatePeopleMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(RoleChineseName), "UpdatePeople 目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.rolechinesename");
                if (isEnabled)
                {
                    Patch();
                }
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;
            if (harmony == null)
            {
                return;
            }

            if (enable)
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
            if (isPatched || harmony == null || updatePeopleMethod == null)
            {
                return;
            }

            harmony.Patch(
                updatePeopleMethod,
                prefix: new HarmonyMethod(typeof(RoleChineseName), nameof(UpdatePeoplePrefix)));
            PatchLog.Patched(logger, nameof(RoleChineseName), updatePeopleMethod);
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || updatePeopleMethod == null)
            {
                return;
            }

            harmony.Unpatch(updatePeopleMethod, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        private static void UpdatePeoplePrefix(BaseItem item, List<PersonInfo> people)
        {
            if (!isEnabled || item == null || people == null || people.Count == 0)
            {
                return;
            }

            try
            {
                DoubanService.EnhancePeopleRole(item, people);
            }
            catch (Exception ex)
            {
                logger?.Error("RoleChineseName prefix 异常: {0}", ex);
            }
        }
    }
}
