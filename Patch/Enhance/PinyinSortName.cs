using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
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
        private static int backfillState;

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
                EnsureSortNamesBackfilled();
            }
            else
            {
                Unpatch();
                Interlocked.Exchange(ref backfillState, 0);
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
            EnsureSortNamesBackfilled();
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

            var pinyinInitials = BuildPinyinSortName(__instance, currentSortName);
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

            var normalizedPairs = pairs
                .Select(NormalizePrefixPair)
                .Where(p => p != null)
                .GroupBy(p => p.Name, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(p => p.Name == "#" ? 0 : 1)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .ToArray();

            if (normalizedPairs.Length > 0)
            {
                __result = normalizedPairs;
            }
        }

        private static NameValuePair NormalizePrefixPair(NameValuePair pair)
        {
            var normalizedPrefix = NormalizePrefix(pair?.Name);
            if (normalizedPrefix == null)
            {
                return null;
            }

            if (pair == null)
            {
                return new NameValuePair { Name = normalizedPrefix };
            }

            pair.Name = normalizedPrefix;
            return pair;
        }

        private static string NormalizePrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return null;
            }

            var trimmedPrefix = prefix.Trim();
            if (trimmedPrefix.Length == 0)
            {
                return null;
            }

            if (trimmedPrefix.Length == 1 && ValidPrefixChars.Contains(trimmedPrefix[0]))
            {
                return trimmedPrefix;
            }

            var leadingCharacter = GetLeadingCharacter(trimmedPrefix);
            if (leadingCharacter == null)
            {
                return null;
            }

            if (LanguageUtility.IsChinese(leadingCharacter))
            {
                try
                {
                    var initials = LanguageUtility.ConvertToPinyinInitials(leadingCharacter);
                    var firstLetter = ExtractFirstValidPrefix(initials);
                    if (firstLetter != null)
                    {
                        return firstLetter;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn("PinyinSortName 已跳过：前缀转换失败。{0}", ex.Message);
                }
            }

            return ExtractFirstValidPrefix(leadingCharacter);
        }

        private static string GetLeadingCharacter(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmedValue = value.Trim();
            foreach (var character in trimmedValue)
            {
                if (!char.IsWhiteSpace(character))
                {
                    return character.ToString();
                }
            }

            return null;
        }

        private static string ExtractFirstValidPrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            foreach (var character in value.Trim())
            {
                if (char.IsLetter(character))
                {
                    var upper = char.ToUpperInvariant(character);
                    if (upper >= 'A' && upper <= 'Z')
                    {
                        return upper.ToString();
                    }
                }

                if (char.IsDigit(character) || char.IsPunctuation(character))
                {
                    return "#";
                }
            }

            return null;
        }

        private static void EnsureSortNamesBackfilled()
        {
            if (!isEnabled || Plugin.LibraryManager == null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref backfillState, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(BackfillSortNamesAsync);
        }

        private static Task BackfillSortNamesAsync()
        {
            var startedAtUtc = DateTimeOffset.UtcNow;

            try
            {
                var checkpointUtc = Plugin.Instance?.Options?.Enhance?.PinyinSortNameLastProcessedUtc
                    ?? DateTimeOffset.MinValue;
                var query = new InternalItemsQuery
                {
                    Recursive = true,
                    MinDateLastSaved = checkpointUtc == DateTimeOffset.MinValue
                        ? DateTimeOffset.MinValue
                        : checkpointUtc.AddSeconds(-1)
                };

                var candidates = Plugin.LibraryManager.GetItemList(query);
                var candidateCount = candidates.Length;

                var updatedCount = 0;
                foreach (var item in candidates)
                {
                    if (!TryApplyPinyinSortName(item))
                    {
                        continue;
                    }

                    try
                    {
                        item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                        updatedCount++;
                    }
                    catch (Exception ex)
                    {
                        logger?.Warn("PinyinSortName 回写失败：{0} ({1})", item.Name, ex.Message);
                    }
                }

                Plugin.Instance?.UpdatePinyinSortNameLastProcessedUtc(startedAtUtc);
                logger?.Info(
                    "PinyinSortName 已同步排序名：候选 {0} 项，更新 {1} 项，时间起点 {2:O}。",
                    candidateCount,
                    updatedCount,
                    checkpointUtc);
            }
            catch (Exception ex)
            {
                logger?.Error("PinyinSortName 同步现有条目排序名失败。");
                logger?.Error(ex.ToString());
            }
            finally
            {
                Interlocked.Exchange(ref backfillState, 0);
            }

            return Task.CompletedTask;
        }

        private static bool TryApplyPinyinSortName(BaseItem item)
        {
            if (!IsEligibleItem(item))
            {
                return false;
            }

            var desiredSortName = BuildPinyinSortName(item, item.Name);
            if (string.IsNullOrWhiteSpace(desiredSortName))
            {
                return false;
            }

            if (string.Equals(item.SortName, desiredSortName, StringComparison.Ordinal))
            {
                return false;
            }

            item.SortName = desiredSortName;
            return true;
        }

        private static bool IsEligibleItem(BaseItem item)
        {
            if (item == null ||
                !item.SupportsUserData ||
                !item.EnableAlphaNumericSorting ||
                item is IHasSeries ||
                item.IsFieldLocked(MetadataFields.SortName))
            {
                return false;
            }

            return item is Video || item is Audio || item is IItemByName || item is Folder;
        }

        private static string BuildPinyinSortName(BaseItem item, string source)
        {
            if (!LanguageUtility.IsChinese(source))
            {
                return null;
            }

            var sortNameSource = item is BoxSet
                ? LanguageUtility.RemoveDefaultCollectionName(source)
                : source;

            try
            {
                return LanguageUtility.ConvertToPinyinInitials(sortNameSource);
            }
            catch (Exception ex)
            {
                logger?.Warn("PinyinSortName 已跳过：拼音转换失败。{0}", ex.Message);
                return null;
            }
        }
    }
}
