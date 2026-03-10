namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Options;

    internal class MainPageOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public MainPageOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public MainPageOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            var mainPage = options.MainPage ?? new MainPageOptions();
            mainPage.NormalizeLegacyRefreshOptions();
            return mainPage;
        }

        public void SetOptions(MainPageOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.MainPage = options ?? new MainPageOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
