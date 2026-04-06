namespace MediaInfoKeeper.Options.View
{
    using System.Threading.Tasks;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaInfoKeeper.Options;
    using MediaInfoKeeper.Options.Store;
    using MediaInfoKeeper.Options.UIBaseClasses.Views;

    internal class MediaInfoPageView : PluginPageView
    {
        private readonly MediaInfoOptionsStore store;

        public MediaInfoPageView(PluginInfo pluginInfo, MediaInfoOptionsStore store)
            : base(pluginInfo.Id)
        {
            this.store = store;
            this.ContentData = store.GetOptions();
        }

        public MediaInfoOptions Options => this.ContentData as MediaInfoOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            this.store.SetOptions(this.Options);
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
