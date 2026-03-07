namespace MediaInfoKeeper.Options.View
{
    using System.Threading.Tasks;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaInfoKeeper.Options;
    using MediaInfoKeeper.Options.Store;
    using MediaInfoKeeper.Options.UIBaseClasses.Views;

    internal class NetWorkPageView : PluginPageView
    {
        private readonly NetWorkOptionsStore store;

        public NetWorkPageView(PluginInfo pluginInfo, NetWorkOptionsStore store)
            : base(pluginInfo.Id)
        {
            this.store = store;
            this.ContentData = store.GetOptions();
        }

        public NetWorkOptions Options => this.ContentData as NetWorkOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            this.store.SetOptions(this.Options);
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
