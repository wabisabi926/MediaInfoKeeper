using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;

namespace MediaInfoKeeper.Configuration
{
    public class MetaDataOptions : EditableOptionsBase
    {
        private static readonly string[] SupportedFallbackLanguages =
        {
            "zh-SG",
            "zh-HK",
            "zh-TW",
        };

        private static readonly string[] SupportedTvdbFallbackLanguages =
        {
            "zho",
            "zhtw",
            "yue",
        };

        public override string EditorTitle => "MetaData";

        [DisplayName("启用剧集元数据变动监听")]
        [Description("开启后将监控媒体元数据刷新过程，当剧集触发封面刷新时延迟恢复媒体信息，避免 .strm 刷新后媒体信息丢失。同时，当修改strm文件内容时，emby会触发刷新媒体，所以也会恢复媒体信息。")]
        public bool EnableMetadataProvidersWatcher { get; set; } = true;

        [DisplayName("启用 TMDB 中文回退")]
        [Description("按备选语言顺序补全 TMDB 电影/剧集/季/集元数据，并尽量把英文放到最后。")]
        public bool EnableAlternativeTitleFallback { get; set; } = true;

        [Browsable(false)]
        public List<EditorSelectOption> FallbackLanguageList { get; set; } = new List<EditorSelectOption>();

        [DisplayName("TMDB 备选语言")]
        [Description("按从左到右优先级回退；会在英文前插入。默认 zh-SG。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(FallbackLanguageList))]
        public string FallbackLanguages { get; set; } = "zh-sg";

        [DisplayName("启用 TVDB 中文回退")]
        [Description("按备选语言顺序补全 TVDB 电影/剧集/季/集元数据，并尽量把英文放到最后。")]
        public bool EnableTvdbFallback { get; set; } = true;

        [Browsable(false)]
        public List<EditorSelectOption> TvdbFallbackLanguageList { get; set; } = new List<EditorSelectOption>();

        [DisplayName("TVDB 备选语言")]
        [Description("按从左到右优先级回退；会在英文前插入。默认 zhtw,yue。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(TvdbFallbackLanguageList))]
        public string TvdbFallbackLanguages { get; set; } = "zhtw,yue";

        [DisplayName("屏蔽非备选语言简介")]
        [Description("开启后，TMDB/TVDB 的电影/剧集/季/集简介若不在备选语言范围（如英文）将被置空。")]
        public bool BlockNonFallbackLanguage { get; set; } = false;

        public void Initialize()
        {
            FallbackLanguageList.Clear();
            foreach (var language in SupportedFallbackLanguages)
            {
                FallbackLanguageList.Add(new EditorSelectOption
                {
                    Value = language.ToLowerInvariant(),
                    Name = language,
                    IsEnabled = true
                });
            }

            TvdbFallbackLanguageList.Clear();
            foreach (var language in SupportedTvdbFallbackLanguages)
            {
                TvdbFallbackLanguageList.Add(new EditorSelectOption
                {
                    Value = language,
                    Name = language,
                    IsEnabled = true
                });
            }
        }
    }
}
