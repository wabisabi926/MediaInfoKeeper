namespace MediaInfoKeeper.Options.View
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaInfoKeeper.Options.Store;
    using MediaInfoKeeper.Options.UIBaseClasses;

    internal class MainPageController : ControllerBase, IHasTabbedUIPages
    {
        private readonly PluginInfo pluginInfo;
        private readonly MainPageOptionsStore mainPageOptionsStore;
        private readonly List<IPluginUIPageController> tabPages = new List<IPluginUIPageController>();

        public MainPageController(PluginInfo pluginInfo,
            MainPageOptionsStore mainPageOptionsStore,
            MediaInfoOptionsStore mediaInfoOptionsStore,
            GitHubOptionsStore gitHubOptionsStore,
            IntroSkipOptionsStore introSkipOptionsStore,
            NetWorkOptionsStore netWorkOptionsStore,
            EnhanceOptionsStore enhanceOptionsStore,
            MetaDataOptionsStore metaDataOptionsStore
#if DEBUG
            , DebugOptionsStore debugOptionsStore
#endif
            )
            : base(pluginInfo.Id)
        {
            this.pluginInfo = pluginInfo;
            this.mainPageOptionsStore = mainPageOptionsStore;

            this.PageInfo = new PluginPageInfo
            {
                Name = "MediaInfoKeeper",
                EnableInMainMenu = true,
                DisplayName = "MediaInfoKeeper",
                MenuIcon = "video_settings",
                IsMainConfigPage = true
            };

            this.tabPages.Add(new TabPageController(pluginInfo, nameof(MediaInfoPageView), "媒体信息",
                e => new MediaInfoPageView(pluginInfo, mediaInfoOptionsStore)));
            
            this.tabPages.Add(new TabPageController(pluginInfo, nameof(MetaDataPageView), "元数据",
                e => new MetaDataPageView(pluginInfo, metaDataOptionsStore)));
            
            this.tabPages.Add(new TabPageController(pluginInfo, nameof(IntroSkipPageView), "片头片尾",
                e => new IntroSkipPageView(pluginInfo, introSkipOptionsStore)));

            this.tabPages.Add(new TabPageController(pluginInfo, nameof(EnhancePageView), "增强功能",
                e => new EnhancePageView(pluginInfo, enhanceOptionsStore)));
            
            this.tabPages.Add(new TabPageController(pluginInfo, nameof(NetWorkPageView), "网络代理",
                e => new NetWorkPageView(pluginInfo, netWorkOptionsStore)));

            this.tabPages.Add(new TabPageController(pluginInfo, nameof(GitHubPageView), "GitHub",
                e => new GitHubPageView(pluginInfo, gitHubOptionsStore)));

#if DEBUG
            this.tabPages.Add(new TabPageController(pluginInfo, nameof(DebugPageView), "Debug",
                e => new DebugPageView(pluginInfo, debugOptionsStore)));
#endif
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new MainPageView(this.pluginInfo, this.mainPageOptionsStore);
            return Task.FromResult(view);
        }

        public IReadOnlyList<IPluginUIPageController> TabPageControllers => this.tabPages.AsReadOnly();
    }
}
