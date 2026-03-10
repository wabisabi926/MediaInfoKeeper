using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// Filter people without images from selected item DTOs.
    /// </summary>
    public static class HidePersonNoImage
    {
        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo attachPeople;
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

            try
            {
                var implAssembly = Assembly.Load("Emby.Server.Implementations");
                var implVersion = implAssembly?.GetName().Version;
                var dtoServiceType = implAssembly?.GetType("Emby.Server.Implementations.Dto.DtoService");

                attachPeople = PatchMethodResolver.Resolve(
                    dtoServiceType,
                    implVersion,
                    new MethodSignatureProfile
                    {
                        Name = "dtoservice-attachpeople-exact",
                        MethodName = "AttachPeople",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[]
                        {
                            typeof(BaseItemDto),
                            typeof(BaseItem),
                            typeof(DtoOptions)
                        },
                        ReturnType = typeof(void)
                    },
                    logger,
                    "HidePersonNoImage.DtoService.AttachPeople");

                if (attachPeople == null)
                {
                    PatchLog.InitFailed(logger, nameof(HidePersonNoImage), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.hidepersonnoimage");
                PatchLog.Patched(logger, nameof(HidePersonNoImage), attachPeople);

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("HidePersonNoImage 初始化失败。");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
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
            if (isPatched || harmony == null || attachPeople == null)
            {
                return;
            }

            harmony.Patch(
                attachPeople,
                postfix: new HarmonyMethod(typeof(HidePersonNoImage), nameof(AttachPeoplePostfix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || attachPeople == null)
            {
                return;
            }

            harmony.Unpatch(attachPeople, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void AttachPeoplePostfix(BaseItemDto dto, BaseItem item, DtoOptions options)
        {
            if (!isEnabled || dto?.People == null || item == null)
            {
                return;
            }

            if (!(item is Movie) &&
                !(item is Series) &&
                !(item is Season) &&
                !(item is Episode))
            {
                return;
            }

            var originalPeople = dto.People;
            var filteredOutPeople = originalPeople
                .Where(p => p != null && !p.HasPrimaryImage)
                .ToArray();

            if (filteredOutPeople.Length == 0)
            {
                return;
            }

            dto.People = originalPeople.Where(p => p != null && p.HasPrimaryImage).ToArray();

            var removedNames = filteredOutPeople
                .Select(p => p.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(10)
                .ToArray();
            var removedNamesText = removedNames.Length == 0
                ? "[]"
                : "[" + string.Join(", ", removedNames) + (filteredOutPeople.Length > 10 ? ", ..." : string.Empty) + "]";

            logger?.Info(
                "HidePersonNoImage - 条目=\"{0}\" 类型={1} 人物过滤：过滤前={2}，过滤后={3}，移除={4}，移除人物={5}",
                item.Name ?? string.Empty,
                item.GetType().Name,
                originalPeople.Length,
                dto.People.Length,
                filteredOutPeople.Length,
                removedNamesText);
        }
    }
}
