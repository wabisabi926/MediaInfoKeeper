namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Options;

    internal class MediaInfoOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public MediaInfoOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public MediaInfoOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            return options.GetMediaInfoOptions();
        }

        public void SetOptions(MediaInfoOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.MediaInfo = options ?? new MediaInfoOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
