using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

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

        [DisplayName("启用 TMDB 剧集组刮削")]
        [Description("开启后支持按 TMDB 剧集组映射刮削剧集元数据（需在剧集外部ID中填写 TmdbEg，或启用本地剧集组文件）。")]
        public bool EnableMovieDbEpisodeGroup { get; set; } = true;

        [DisplayName("启用本地剧集组文件")]
        [Description("开启后在剧集目录读取 episodegroup.json；当在线剧集组可用时会自动写入本地文件用于后续复用。")]
        public bool EnableLocalEpisodeGroup { get; set; } = false;

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

        public override IEditObjectContainer CreateEditContainer()
        {
            var container = (EditObjectContainer)base.CreateEditContainer();
            var root = container.EditorRoot;
            if (root?.EditorItems == null || root.EditorItems.Length == 0)
            {
                return container;
            }

            var itemLookup = new Dictionary<string, EditorBase>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.EditorItems)
            {
                var key = item.Name ?? item.Id;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!itemLookup.ContainsKey(key))
                {
                    itemLookup.Add(key, item);
                }
            }

            var groupedItems = new List<EditorBase>();
            var groupIndex = 0;

            void AddGroup(string title, params string[] propertyNames)
            {
                var items = new List<EditorBase>();
                foreach (var propertyName in propertyNames)
                {
                    if (itemLookup.TryGetValue(propertyName, out var item))
                    {
                        items.Add(item);
                        itemLookup.Remove(propertyName);
                    }
                }

                if (items.Count == 0)
                {
                    return;
                }

                groupIndex++;
                groupedItems.Add(new EditorGroup(title, items.ToArray(), $"group{groupIndex}", root.Id, null));
            }

            AddGroup("",
                nameof(EnableMetadataProvidersWatcher));

            AddGroup("标题中文别名",
                nameof(EnableAlternativeTitleFallback),
                nameof(EnableTvdbFallback),
                nameof(FallbackLanguages),
                nameof(TvdbFallbackLanguages),
                nameof(BlockNonFallbackLanguage));

            AddGroup("剧集组刮削",
                nameof(EnableMovieDbEpisodeGroup),
                nameof(EnableLocalEpisodeGroup));

            var remaining = new List<EditorBase>();
            foreach (var item in root.EditorItems)
            {
                var key = item.Name ?? item.Id;
                if (!string.IsNullOrEmpty(key) && itemLookup.ContainsKey(key))
                {
                    remaining.Add(item);
                    itemLookup.Remove(key);
                }
            }

            if (remaining.Count > 0)
            {
                groupIndex++;
                groupedItems.Add(new EditorGroup("未分组", remaining.ToArray(), $"group{groupIndex}", root.Id, null));
            }

            if (groupedItems.Count > 0)
            {
                root.EditorItems = groupedItems.ToArray();
            }

            return container;
        }
    }
}
