namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Options;

    internal class NetWorkOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public NetWorkOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public NetWorkOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            return options.GetNetWorkOptions();
        }

        public void SetOptions(NetWorkOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.NetWork = options ?? new NetWorkOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
