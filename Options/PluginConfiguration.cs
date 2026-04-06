using System.ComponentModel;
using Emby.Web.GenericEdit;

namespace MediaInfoKeeper.Options
{
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "MediaInfoKeeper";

        public override string EditorDescription => "将媒体信息与章节保存为 JSON，并在需要时从 JSON 恢复。";

        // Main page
        [DisplayName("MediaInfo Keeper")]
        public MainPageOptions MainPage { get; set; } = new MainPageOptions();

        // Tab pages (order follows MainPageController)
        [DisplayName("MediaInfo")]
        public MediaInfoOptions MediaInfo { get; set; }

        [DisplayName("IntroSkip")]
        public IntroSkipOptions IntroSkip { get; set; } = new IntroSkipOptions();

        [DisplayName("Enhance")]
        public EnhanceOptions Enhance { get; set; } = new EnhanceOptions();

        [DisplayName("MetaData")]
        public MetaDataOptions MetaData { get; set; } = new MetaDataOptions();

        [DisplayName("Network")]
        public NetWorkOptions NetWork { get; set; }

        [Browsable(false)]
        // TODO: 旧配置兼容字段（历史键名 "Proxy"）；在确认无历史配置迁移需求后可删除。
        public NetWorkOptions Proxy { get; set; }

        [DisplayName("GitHub")]
        public GitHubOptions GitHub { get; set; } = new GitHubOptions();

#if DEBUG
        [DisplayName("Debug")]
        public DebugOptions Debug { get; set; } = new DebugOptions();
#endif

        public NetWorkOptions GetNetWorkOptions()
        {
            // 兼容顺序：NetWork(新) -> Proxy(旧) -> 默认值。
            var options = NetWork ?? Proxy ?? new NetWorkOptions();
            NetWork ??= options;
            return options;
        }

        public MediaInfoOptions GetMediaInfoOptions()
        {
            var options = MediaInfo ?? BuildMediaInfoOptionsFromLegacy();
            MediaInfo ??= options;
            return options;
        }

        private MediaInfoOptions BuildMediaInfoOptionsFromLegacy()
        {
            MainPage ??= new MainPageOptions();

            return new MediaInfoOptions
            {
                ExtractMediaInfoOnItemAdded = MainPage.ExtractMediaInfoOnItemAdded,
                DeleteMediaInfoJsonOnRemove = MainPage.DeleteMediaInfoJsonOnRemove,
                MediaInfoJsonRootFolder = string.IsNullOrWhiteSpace(MainPage.MediaInfoJsonRootFolder)
                    ? MediaInfoOptions.GetDefaultMediaInfoJsonRootFolder()
                    : MainPage.MediaInfoJsonRootFolder,
                MaxConcurrentCount = MainPage.MaxConcurrentCount
            };
        }
    }
}
