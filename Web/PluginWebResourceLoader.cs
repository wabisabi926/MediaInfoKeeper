using System;
using System.IO;
using System.Reflection;
using MediaBrowser.Controller.Configuration;

namespace MediaInfoKeeper.Web
{
    internal static class PluginWebResourceLoader
    {
        public static string ModifiedShortcutsString { get; private set; }

        public static MemoryStream MediaInfoKeeperJs { get; private set; }

        public static MemoryStream EdeJs { get; private set; }

        public static void Initialize(IServerConfigurationManager configurationManager)
        {
            try
            {
                PatchHtmlVideoPlayer(configurationManager);
                MediaInfoKeeperJs = GetResourceStream("mediainfokeeper.js");
                EdeJs = GetResourceStream("ede.js");
                BuildShortcutBootstrap(configurationManager);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Error($"{nameof(PluginWebResourceLoader)} Init Failed");
                Plugin.Instance.Logger.Error(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }
        }

        private static MemoryStream GetResourceStream(string resourceName)
        {
            var name = typeof(Plugin).Namespace + ".Resources." + resourceName;
            var manifestResourceStream = typeof(PluginWebResourceLoader).GetTypeInfo().Assembly.GetManifestResourceStream(name);
            var destination = new MemoryStream((int)manifestResourceStream.Length);
            manifestResourceStream.CopyTo(destination);
            destination.Position = 0;
            return destination;
        }

        private static void PatchHtmlVideoPlayer(IServerConfigurationManager configurationManager)
        {
            try
            {
                var dashboardSourcePath = configurationManager.Configuration.DashboardSourcePath ??
                                          Path.Combine(configurationManager.ApplicationPaths.ApplicationResourcesPath,
                                              "dashboard-ui");
                var pluginPath = Path.Combine(dashboardSourcePath, "modules", "htmlvideoplayer", "plugin.js");
                if (!File.Exists(pluginPath))
                {
                    return;
                }

                const string source = "&&(elem.crossOrigin=initialSubtitleStream)";
                var content = File.ReadAllText(pluginPath);
                var patchedContent = content.Replace(source, string.Empty);
                if (!string.Equals(content, patchedContent, StringComparison.Ordinal))
                {
                    File.WriteAllText(pluginPath, patchedContent);
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Warn("PatchHtmlVideoPlayer failed: {0}", ex.Message);
                Plugin.Instance.Logger.Debug(ex.StackTrace);
            }
        }

        private static void BuildShortcutBootstrap(IServerConfigurationManager configurationManager)
        {
            var dashboardSourcePath = configurationManager.Configuration.DashboardSourcePath ??
                                      Path.Combine(configurationManager.ApplicationPaths.ApplicationResourcesPath,
                                          "dashboard-ui");

            const string bootstrapScript = @"
;(function loadMediaInfoKeeperShortcutModule() {
    if (window.__mediaInfoKeeperShortcutBootstrapLoaded) {
        return;
    }
    window.__mediaInfoKeeperShortcutBootstrapLoaded = true;

    if (typeof require !== 'function') {
        return;
    }

    require(['components/mediainfokeeper/mediainfokeeper'], function () {}, function () {});
})();
";

            ModifiedShortcutsString = File.ReadAllText(Path.Combine(dashboardSourcePath, "modules", "shortcuts.js")) +
                                      bootstrapScript;
        }
    }
}
