using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Common;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 为中文标题生成拼音首字母排序名，并清理前端前缀分组。
    /// </summary>
    public static class PinyinSortName
    {
        private static readonly HashSet<char> ValidPrefixChars = new HashSet<char>("#ABCDEFGHIJKLMNOPQRSTUVWXYZ");

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo createSortNameMethod;
        private static MethodInfo getPrefixesMethod;
        private static MethodInfo getArtistPrefixesMethod;
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
                var controllerVersion = typeof(BaseItem).Assembly.GetName().Version;
                createSortNameMethod = PatchMethodResolver.Resolve(
                    typeof(BaseItem),
                    controllerVersion,
                    new MethodSignatureProfile
                    {
                        Name = "baseitem-createsortname-span-exact",
                        MethodName = "CreateSortName",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(ReadOnlySpan<char>) },
                        ReturnType = typeof(ReadOnlySpan<char>),
                        IsStatic = false
                    },
                    logger,
                    "PinyinSortName.BaseItem.CreateSortName");

                var embyApi = Assembly.Load("Emby.Api");
                var embyApiVersion = embyApi?.GetName().Version;
                var tagServiceType = embyApi?.GetType("Emby.Api.UserLibrary.TagService");
                var getPrefixesRequestType = embyApi?.GetType("Emby.Api.UserLibrary.GetPrefixes");
                var getArtistPrefixesRequestType = embyApi?.GetType("Emby.Api.UserLibrary.GetArtistPrefixes");

                if (tagServiceType == null || getPrefixesRequestType == null || getArtistPrefixesRequestType == null)
                {
                    PatchLog.InitFailed(logger, nameof(PinyinSortName), "缺少 Emby.Api.UserLibrary 相关类型");
                    return;
                }

                getPrefixesMethod = PatchMethodResolver.Resolve(
                    tagServiceType,
                    embyApiVersion,
                    new MethodSignatureProfile
                    {
                        Name = "tagservice-get-prefixes-exact",
                        MethodName = "Get",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[] { getPrefixesRequestType },
                        ReturnType = typeof(object),
                        IsStatic = false
                    },
                    logger,
                    "PinyinSortName.TagService.GetPrefixes");

                getArtistPrefixesMethod = PatchMethodResolver.Resolve(
                    tagServiceType,
                    embyApiVersion,
                    new MethodSignatureProfile
                    {
                        Name = "tagservice-get-artistprefixes-exact",
                        MethodName = "Get",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[] { getArtistPrefixesRequestType },
                        ReturnType = typeof(object),
                        IsStatic = false
                    },
                    logger,
                    "PinyinSortName.TagService.GetArtistPrefixes");

                if (createSortNameMethod == null || getPrefixesMethod == null || getArtistPrefixesMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(PinyinSortName), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.pinyinsortname");
                PatchLog.Patched(logger, nameof(PinyinSortName), createSortNameMethod);
                PatchLog.Patched(logger, nameof(PinyinSortName), getPrefixesMethod);
                PatchLog.Patched(logger, nameof(PinyinSortName), getArtistPrefixesMethod);

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("PinyinSortName 初始化失败。");
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
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(
                createSortNameMethod,
                postfix: new HarmonyMethod(typeof(PinyinSortName), nameof(CreateSortNamePostfix)));
            harmony.Patch(
                getPrefixesMethod,
                postfix: new HarmonyMethod(typeof(PinyinSortName), nameof(GetPrefixesPostfix)));
            harmony.Patch(
                getArtistPrefixesMethod,
                postfix: new HarmonyMethod(typeof(PinyinSortName), nameof(GetPrefixesPostfix)));
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.Unpatch(createSortNameMethod, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Unpatch(getPrefixesMethod, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Unpatch(getArtistPrefixesMethod, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void CreateSortNamePostfix(BaseItem __instance, ref ReadOnlySpan<char> __result)
        {
            if (!isEnabled || __instance == null)
            {
                return;
            }

            if (!__instance.SupportsUserData ||
                !__instance.EnableAlphaNumericSorting ||
                __instance is IHasSeries ||
                __instance.IsFieldLocked(MetadataFields.SortName))
            {
                return;
            }

            if (!(__instance is Video) &&
                !(__instance is Audio) &&
                !(__instance is IItemByName) &&
                !(__instance is Folder))
            {
                return;
            }

            var currentSortName = __result.ToString();
            if (!LanguageUtility.IsChinese(currentSortName))
            {
                return;
            }

            var sortNameSource = __instance is BoxSet
                ? LanguageUtility.RemoveDefaultCollectionName(currentSortName)
                : currentSortName;
            string pinyinInitials;
            try
            {
                pinyinInitials = LanguageUtility.ConvertToPinyinInitials(sortNameSource);
            }
            catch (Exception ex)
            {
                logger?.Warn("PinyinSortName 已跳过：拼音转换失败。{0}", ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(pinyinInitials))
            {
                return;
            }

            __result = pinyinInitials.AsSpan();
        }

        [HarmonyPostfix]
        private static void GetPrefixesPostfix(ref object __result)
        {
            if (!isEnabled || !(__result is NameValuePair[] pairs))
            {
                return;
            }

            var filteredPairs = pairs
                .Where(p => p?.Name?.Length == 1 && ValidPrefixChars.Contains(p.Name[0]))
                .ToArray();

            if (filteredPairs.Length != pairs.Length && filteredPairs.Any(p => p.Name[0] != '#'))
            {
                __result = filteredPairs;
            }
        }
    }
}
