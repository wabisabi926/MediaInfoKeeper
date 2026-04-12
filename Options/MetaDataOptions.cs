using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

namespace MediaInfoKeeper.Options
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

        public override string EditorTitle => "元数据";

        public override string EditorDescription => "元数据相关设置，包括刷新过程处理、TMDB 回退和 TVDB 回退。改完记得保存。";

        [DisplayName("允许提取 Strm 封面")]
        [Description("为 strm 音视频启用封面/缩略图提取，ImageCapture。")]
        public bool EnableImageCapture { get; set; } = true;

        [DisplayName("启用 TMDB 中文回退")]
        [Description("按备选语言顺序补全 TMDB 电影/剧集/季/集元数据，并尽量把英文放到最后。")]
        public bool EnableAlternativeTitleFallback { get; set; } = true;

        [DisplayName("启用豆瓣角色中文化")]
        [Description("当演员角色为空或非中文时，尝试用豆瓣演员表补全中文角色名。")]
        public bool EnablePersonRoleDoubanFallback { get; set; } = true;
        
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

        [DisplayName("启用缺失剧集增强")]
        [Description("开启后让 Emby 的“查看缺少的集”优先使用 TMDB，并支持按 TMDB 剧集组映射结果展示缺失剧集。")]
        public bool EnableMissingEpisodesEnhance { get; set; } = true;

        [DisplayName("优先原语言海报")]
        [Description("开启后优先原语言图片结果（支持 TMDB / TVDB / Fanart）。")]
        public bool EnableOriginalPoster { get; set; } = false;

        [DisplayName("启用本地剧集组文件")]
        [Description("开启后在剧集目录读取 episodegroup.json；当在线剧集组可用时会自动写入本地文件用于后续复用。")]
        public bool EnableLocalEpisodeGroup { get; set; } = false;

        [DisplayName("加载 dd-danmaku 弹幕js")]
        [Description("修改 index.html，注入 ede.js")]
        public bool EnableDanmakuJs { get; set; } = false;

        [DisplayName("弹幕 API BaseUrl")]
        [Description("例如 http://192.168.33.100:9321/token 。插件会调用 /search/episodes 和 /comment/{episodeId}?format=xml。danmu_api 项目 https://github.com/huangxd-/danmu_api")]
        public string DanmuApiBaseUrl { get; set; } = string.Empty;
        
        [DisplayName("入库后延迟下载弹幕（分钟）")]
        [Description("-1 表示入库不下载；入库后延迟对应分钟数再自动尝试下载弹幕。本插件兼容 emby-plugin-danmu 弹幕路由")]
        [MinValue(-1)]
        [MaxValue(360)]
        public int DownloadDanmuOnItemAddedDelayMinutes { get; set; } = -1;

        [DisplayName("覆盖已有弹幕文件")]
        [Description("开启后，弹幕下载任务会覆盖条目目录中已有的同名 .xml 文件；关闭时遇到现有文件会跳过")]
        public bool OverwriteExistingDanmuXml { get; set; } = false;
        
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

            void AddGroup(string title, string description, params string[] propertyNames)
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
                var group = new EditorGroup(title, items.ToArray(), $"group{groupIndex}", root.Id, null)
                {
                    Description = description
                };
                groupedItems.Add(group);
            }

            AddGroup("MetaData", "当 Emby 元数据刷新时，插件会监听元数据刷新过程并延迟恢复媒体信息，避免刷新后媒体信息丢失。",
                nameof(EnableImageCapture),
                nameof(EnableOriginalPoster),
                nameof(BlockNonFallbackLanguage));
            
            AddGroup("TMDB", "",
                nameof(EnableAlternativeTitleFallback),
                nameof(FallbackLanguages),
                nameof(EnableMovieDbEpisodeGroup),
                nameof(EnableMissingEpisodesEnhance),
                nameof(EnableLocalEpisodeGroup));

            AddGroup("Douban", "",
                nameof(EnablePersonRoleDoubanFallback));
            
            AddGroup("Danmaku", "",
                nameof(EnableDanmakuJs),
                nameof(DanmuApiBaseUrl),
                nameof(OverwriteExistingDanmuXml),
                nameof(DownloadDanmuOnItemAddedDelayMinutes));
            
            AddGroup("TVDB", "",
                nameof(EnableTvdbFallback),
                nameof(TvdbFallbackLanguages));
            
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
