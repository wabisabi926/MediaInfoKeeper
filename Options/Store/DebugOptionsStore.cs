#if DEBUG
namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Options;

    internal class DebugOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public DebugOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public DebugOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            return options.Debug ?? new DebugOptions();
        }

        public void SetOptions(DebugOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.Debug = options ?? new DebugOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
#endif
