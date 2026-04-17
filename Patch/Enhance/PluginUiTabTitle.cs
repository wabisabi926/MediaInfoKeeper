using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 为带 Tab 的插件主页单独调整主 Tab 标题，避免和左侧菜单文案强绑定。
    /// </summary>
    public static class PluginUiTabTitle
    {
        private const string ModuleName = nameof(PluginUiTabTitle);
        private const string GenericUiAssemblyName = "Emby.Web.GenericUI";
        private const string UiPagesManagerTypeName = "Emby.Web.GenericUI.Control.UIPagesManager";
        private const string UiViewInfoTypeName = "Emby.Web.GenericUI.Model.UIViewInfo";
        private const string UiTabPageInfoTypeName = "Emby.Web.GenericUI.Model.UITabPageInfo";
        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo addTabPageInformationMethod;
        private static Type uiViewInfoType;
        private static Type uiTabPageInfoType;
        private static ConstructorInfo uiTabPageInfoConstructor;
        private static PropertyInfo uiViewInfoTabPageInfosProperty;
        private static PropertyInfo uiViewInfoPageIdProperty;
        private static PropertyInfo uiTabPageInfoPageIdProperty;
        private static PropertyInfo uiTabPageInfoDisplayNameProperty;
        private static PropertyInfo uiTabPageInfoPluginIdProperty;
        private static PropertyInfo uiTabPageInfoHrefProperty;
        private static PropertyInfo uiTabPageInfoNavKeyProperty;
        private static PropertyInfo uiTabPageInfoIndexProperty;
        private static bool isPatched;

        public static bool IsReady => harmony != null && isPatched;

        public static void Initialize(ILogger pluginLogger)
        {
            if (harmony != null)
            {
                return;
            }

            logger = pluginLogger;

            try
            {
                var genericUiAssembly = Assembly.Load(GenericUiAssemblyName);
                var assemblyVersion = genericUiAssembly?.GetName().Version;
                var uiPagesManagerType = genericUiAssembly?.GetType(UiPagesManagerTypeName, false);
                uiViewInfoType = genericUiAssembly?.GetType(UiViewInfoTypeName, false);
                uiTabPageInfoType = genericUiAssembly?.GetType(UiTabPageInfoTypeName, false);

                if (uiPagesManagerType == null || uiViewInfoType == null || uiTabPageInfoType == null)
                {
                    PatchLog.InitFailed(logger, ModuleName, "Emby.Web.GenericUI 关键类型缺失");
                    return;
                }

                addTabPageInformationMethod = PatchMethodResolver.Resolve(
                    uiPagesManagerType,
                    assemblyVersion,
                    new MethodSignatureProfile
                    {
                        Name = "uipagesmanager-addtabpageinformation-exact",
                        MethodName = "AddTabPageInformation",
                        BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                        IsStatic = false,
                        ParameterTypes = new[] { uiViewInfoType },
                        ReturnType = typeof(void)
                    },
                    logger,
                    "PluginUiTabTitle.AddTabPageInformation");

                uiTabPageInfoConstructor = uiTabPageInfoType.GetConstructor(new[]
                {
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(int)
                });

                uiViewInfoTabPageInfosProperty = uiViewInfoType.GetProperty("TabPageInfos", BindingFlags.Public | BindingFlags.Instance);
                uiViewInfoPageIdProperty = uiViewInfoType.GetProperty("PageId", BindingFlags.Public | BindingFlags.Instance);
                uiTabPageInfoPageIdProperty = uiTabPageInfoType.GetProperty("PageId", BindingFlags.Public | BindingFlags.Instance);
                uiTabPageInfoDisplayNameProperty = uiTabPageInfoType.GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance);
                uiTabPageInfoPluginIdProperty = uiTabPageInfoType.GetProperty("PluginId", BindingFlags.Public | BindingFlags.Instance);
                uiTabPageInfoHrefProperty = uiTabPageInfoType.GetProperty("Href", BindingFlags.Public | BindingFlags.Instance);
                uiTabPageInfoNavKeyProperty = uiTabPageInfoType.GetProperty("NavKey", BindingFlags.Public | BindingFlags.Instance);
                uiTabPageInfoIndexProperty = uiTabPageInfoType.GetProperty("Index", BindingFlags.Public | BindingFlags.Instance);

                if (addTabPageInformationMethod == null ||
                    uiTabPageInfoConstructor == null ||
                    uiViewInfoTabPageInfosProperty == null ||
                    uiViewInfoPageIdProperty == null ||
                    uiTabPageInfoPageIdProperty == null ||
                    uiTabPageInfoDisplayNameProperty == null ||
                    uiTabPageInfoPluginIdProperty == null ||
                    uiTabPageInfoHrefProperty == null ||
                    uiTabPageInfoNavKeyProperty == null ||
                    uiTabPageInfoIndexProperty == null)
                {
                    PatchLog.InitFailed(logger, ModuleName, "UIPagesManager 或 UITabPageInfo 依赖成员缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.pluginuitabtitle");
                Patch();
            }
            catch (Exception ex)
            {
                PatchLog.InitFailed(logger, ModuleName, ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                addTabPageInformationMethod = null;
                uiViewInfoType = null;
                uiTabPageInfoType = null;
                uiTabPageInfoConstructor = null;
                uiViewInfoTabPageInfosProperty = null;
                uiViewInfoPageIdProperty = null;
                uiTabPageInfoPageIdProperty = null;
                uiTabPageInfoDisplayNameProperty = null;
                uiTabPageInfoPluginIdProperty = null;
                uiTabPageInfoHrefProperty = null;
                uiTabPageInfoNavKeyProperty = null;
                uiTabPageInfoIndexProperty = null;
                isPatched = false;
            }
        }

        public static void Configure()
        {
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || addTabPageInformationMethod == null)
            {
                return;
            }

            harmony.Patch(
                addTabPageInformationMethod,
                postfix: new HarmonyMethod(typeof(PluginUiTabTitle), nameof(AddTabPageInformationPostfix)));
            PatchLog.Patched(logger, ModuleName, addTabPageInformationMethod);
            isPatched = true;
        }

        [HarmonyPostfix]
        private static void AddTabPageInformationPostfix(object __0)
        {
            try
            {
                if (__0 == null || uiViewInfoType == null || !uiViewInfoType.IsInstanceOfType(__0))
                {
                    return;
                }

                var tabPageInfos = uiViewInfoTabPageInfosProperty?.GetValue(__0) as IList;
                if (tabPageInfos == null || tabPageInfos.Count == 0)
                {
                    return;
                }

                var firstTab = tabPageInfos[0];
                if (firstTab == null || !uiTabPageInfoType.IsInstanceOfType(firstTab))
                {
                    return;
                }

                var currentPageId = uiViewInfoPageIdProperty?.GetValue(__0) as string;
                var firstTabPageId = uiTabPageInfoPageIdProperty?.GetValue(firstTab) as string;
                if (!IsMainPluginPageId(currentPageId) && !IsMainPluginPageId(firstTabPageId))
                {
                    return;
                }

                var currentDisplayName = uiTabPageInfoDisplayNameProperty?.GetValue(firstTab) as string;
                var desiredTitle = GetDesiredMainTabTitle();
                if (string.IsNullOrWhiteSpace(desiredTitle) ||
                    string.Equals(currentDisplayName, desiredTitle, StringComparison.Ordinal))
                {
                    return;
                }

                var replacement = uiTabPageInfoConstructor.Invoke(new object[]
                {
                    uiTabPageInfoPageIdProperty?.GetValue(firstTab) as string,
                    desiredTitle,
                    uiTabPageInfoPluginIdProperty?.GetValue(firstTab) as string,
                    uiTabPageInfoHrefProperty?.GetValue(firstTab) as string,
                    uiTabPageInfoNavKeyProperty?.GetValue(firstTab) as string,
                    (int)(uiTabPageInfoIndexProperty?.GetValue(firstTab) ?? 0)
                });

                tabPageInfos[0] = replacement;
            }
            catch (Exception ex)
            {
                logger?.Error("PluginUiTabTitle postfix failed: " + ex);
            }
        }

        private static string GetDesiredMainTabTitle()
        {
            var title = Plugin.Instance?.Options?.MainPage?.EditorTitle;
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            return null;
        }

        private static bool IsMainPluginPageId(string pageId)
        {
            if (string.IsNullOrWhiteSpace(pageId))
            {
                return false;
            }

            var pageName = GetMainPageControllerName();
            if (string.IsNullOrWhiteSpace(pageName))
            {
                return false;
            }

            return pageId.EndsWith(":" + pageName, StringComparison.Ordinal);
        }

        private static string GetMainPageControllerName()
        {
            var controllers = Plugin.Instance?.UIPageControllers;
            if (controllers == null)
            {
                return null;
            }

            foreach (var controller in controllers)
            {
                var pageName = controller?.PageInfo?.Name;
                if (!string.IsNullOrWhiteSpace(pageName))
                {
                    return pageName;
                }
            }

            return null;
        }
    }
}
