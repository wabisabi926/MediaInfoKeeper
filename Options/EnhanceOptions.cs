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
    public class EnhanceOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Enhance";

        [DisplayName("启用增强搜索")]
        [Description("支持中文模糊搜索与拼音搜索，默认关闭。")]
        public bool EnhanceChineseSearch { get; set; } = false;

        [Browsable(false)]
        public bool EnhanceChineseSearchRestore { get; set; } = false;

        public enum SearchItemType
        {
            Movie,
            Collection,
            Series,
            Season,
            Episode,
            Person,
            LiveTv,
            Playlist,
            Video
        }

        [Browsable(false)]
        public List<EditorSelectOption> SearchItemTypeList { get; set; } = new List<EditorSelectOption>();

        [DisplayName("搜索范围")]
        [Description("选择要参与搜索的类型，留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(SearchItemTypeList))]
        public string SearchScope { get; set; } =
            string.Join(",", new[] { SearchItemType.Movie, SearchItemType.Collection, SearchItemType.Series });

        [DisplayName("排除原始标题")]
        [Description("从搜索中排除 OriginalTitle 字段，默认关闭。")]
        public bool ExcludeOriginalTitleFromSearch { get; set; } = false;


        [DisplayName("启用深度删除")]
        [Description("删除媒体时，尝试级联删除 STRM 或软链接目标文件及相关文件和空目录。")]
        public bool EnableDeepDelete { get; set; } = false;

        [DisplayName("启用 NFO 增强")]
        [Description("增强 NFO 人物节点解析，导入使用 actor/director 等人物中的 thumb 图片地址。")]
        public bool EnableNfoMetadataEnhance { get; set; } = true;
    
        [DisplayName("接管系统新入库通知")]
        [Description("开启后插件接管 Emby 的 library.new 事件并屏蔽系统原生新入库通知，仅对已收藏/喜爱的剧集新入库集发送通知，用于配合MP插件——媒体服务器通知，通知新入库；关闭则插件使用 favorites.update 事件，不影响 Emby 原有的新入库通知。")]
        public bool TakeOverSystemLibraryNew { get; set; } = false;

        public void Initialize()
        {
            SearchItemTypeList.Clear();
            foreach (SearchItemType item in Enum.GetValues(typeof(SearchItemType)))
            {
                SearchItemTypeList.Add(new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = GetSearchItemTypeDisplayName(item),
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

            void AddGroup(string title, string description = null, params string[] propertyNames)
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

            AddGroup("增强搜索", "",
                nameof(EnhanceChineseSearch),
                nameof(SearchScope),
                nameof(ExcludeOriginalTitleFromSearch));

            AddGroup("NFO增强", "",
                nameof(EnableNfoMetadataEnhance));
            
            AddGroup("深度删除", "",
                nameof(EnableDeepDelete));
            
            AddGroup("通知", "",
                nameof(TakeOverSystemLibraryNew));

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

        private static string GetSearchItemTypeDisplayName(SearchItemType item)
        {
            switch (item)
            {
                case SearchItemType.Movie:
                    return "电影";
                case SearchItemType.Collection:
                    return "合集";
                case SearchItemType.Series:
                    return "剧集";
                case SearchItemType.Season:
                    return "季";
                case SearchItemType.Episode:
                    return "集";
                case SearchItemType.Person:
                    return "人物";
                case SearchItemType.LiveTv:
                    return "直播电视";
                case SearchItemType.Playlist:
                    return "播放列表";
                case SearchItemType.Video:
                    return "视频";
                default:
                    return item.ToString();
            }
        }
    }
}
