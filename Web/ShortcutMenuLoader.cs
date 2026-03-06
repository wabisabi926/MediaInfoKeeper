using System;
using System.IO;
using System.Reflection;
using MediaBrowser.Controller.Configuration;

namespace MediaInfoKeeper.Web
{
    internal static class ShortcutMenuLoader
    {
        public static string ModifiedShortcutsString { get; private set; }

        public static MemoryStream MediaInfoKeeperJs { get; private set; }

        public static void Initialize(IServerConfigurationManager configurationManager)
        {
            try
            {
                MediaInfoKeeperJs = GetResourceStream("mediainfokeeper.js");
                BuildShortcutBootstrap(configurationManager);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Error($"{nameof(ShortcutMenuLoader)} Init Failed");
                Plugin.Instance.Logger.Error(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }
        }

        private static MemoryStream GetResourceStream(string resourceName)
        {
            var name = typeof(Plugin).Namespace + ".Resources." + resourceName;
            var manifestResourceStream = typeof(ShortcutMenuLoader).GetTypeInfo().Assembly.GetManifestResourceStream(name);
            var destination = new MemoryStream((int)manifestResourceStream.Length);
            manifestResourceStream.CopyTo(destination);
            destination.Position = 0;
            return destination;
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
