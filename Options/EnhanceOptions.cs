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
        public override string EditorTitle => "增强功能";

        public override string EditorDescription => "增强功能页，搜索、Web 调整、深度删除、通知、人物显示、NFO、合集、媒体库和日志这些都在这里。改完记得保存。";

        [DisplayName("启用增强搜索")]
        [Description("支持中文模糊搜索与拼音搜索，默认关闭。\n\n卸载插件前，请先关闭本功能并保存配置，再移除插件；不要直接在插件页卸载或手动删除 dll，否则可能导致数据库无法读写，并出现以下错误：\n\nSQLitePCL.pretty.SQLiteException: Error: no such tokenizer: simple\n\n如果插件版本更新，出现上述错误，本页面点击一次“保存配置”恢复正常。")]
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
        [Description("从搜索中排除 OriginalTitle 字段")]
        public bool ExcludeOriginalTitleFromSearch { get; set; } = false;
        
        [DisplayName("启用深度删除")]
        [Description("删除媒体时，尝试级联删除 STRM 或软链接目标文件及相关文件和空目录。")]
        public bool EnableDeepDelete { get; set; } = false;
        
        [DisplayName("通知系统增强")]
        [Description("提供媒体深度删除通知，喜爱更新通知和片头片尾打标更新通知。")]
        public bool EnableNotificationEnhance { get; set; } = true;
        
        [DisplayName("接管系统新入库通知")]
        [Description("开启后插件接管 Emby 的 library.new 事件并屏蔽系统原生新入库通知，仅对已收藏/喜爱的剧集新入库集发送通知；关闭则插件使用 favorites.update 事件，不影响 Emby 原有的新入库通知。")]
        public bool TakeOverSystemLibraryNew { get; set; } = false;
        
        [DisplayName("缺失封面使用背景图")]
        [Description("当 episode 没有自己的封面图时，优先提供 series 的背景图，16:9 优化显示。")]
        public bool EnableEpisodeBackdropFallback { get; set; } = true;
        
        [DisplayName("启用 NFO 增强")]
        [Description("增强 NFO 人物节点解析，导入使用 actor/director 等人物中的 thumb 图片地址。")]
        public bool EnableNfoMetadataEnhance { get; set; } = true;

        [DisplayName("按偏好隐藏演职人员")]
        [Description("按偏好隐藏电影剧集页面的演职人员，非删除，仍可搜索。")]
        public bool HidePersonNoImage { get; set; } = false;
        
        [DisplayName("拼音首字母排序")]
        [Description("自动把中文标题的 SortName 转成拼音首字母，并清理 A-Z 前缀分组。每次Emby启动时，会处理增量item的SortName。")]
        public bool EnablePinyinSortName { get; set; } = false;

        [Browsable(false)]
        public DateTimeOffset? PinyinSortNameLastProcessedAt { get; set; } = null;

        public enum HidePersonOption
        {
            NoImage,
            ActorOnly
        }

        [Browsable(false)]
        public List<EditorSelectOption> HidePersonOptionList { get; set; } = new List<EditorSelectOption>();

        [DisplayName("隐藏偏好")]
        [Description("可组合选择：无图、仅演员。勾选“仅演员”后，只保留演员和客串演员。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(HidePersonOptionList))]
        [VisibleCondition(nameof(HidePersonNoImage), SimpleCondition.IsTrue)]
        public string HidePersonPreference { get; set; } = string.Empty;

        [DisplayName("禁止自动合集")]
        [Description("阻止 Emby 自动创建 BoxSets 合集库，并在用户视图中过滤该入口。")]
        public bool NoBoxsetsAutoCreation { get; set; } = false;

        [DisplayName("统一媒体库顺序")]
        [Description("让所有用户的媒体库顺序跟随首个管理员的 OrderedViews 配置。")]
        public bool EnforceLibraryOrder { get; set; } = false;

        [DisplayName("日志来源黑名单")]
        [Description("按 logger.Name 匹配需要屏蔽的系统日志来源，支持逗号、分号或换行分隔。支持精确匹配；对于带动态后缀的来源可填写前缀，如 SessionsService-。")]
        public string SystemLogNameBlacklist { get; set; } = "HttpClient;TheMovieDb;SessionsService-;PlaystateService-;MediaInfoService-";

        [DisplayName("日志显示详细网络请求")]
        [Description("控制是否输出详细网络请求日志，例如 HTTP 方法和最终请求地址。默认开启。")]
        public bool EnableDetailedNetworkRequestLogging { get; set; } = true;

        [DisplayName("系统日志倒序显示")]
        [Description("将 /System/Logs 下日志接口的返回内容改为最新日志在前，不影响磁盘上的原始日志文件。")]
        public bool EnableSystemLogReverse { get; set; } = false;

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

            HidePersonOptionList.Clear();
            foreach (HidePersonOption item in Enum.GetValues(typeof(HidePersonOption)))
            {
                HidePersonOptionList.Add(new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = GetHidePersonOptionDisplayName(item),
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

            AddGroup("增强搜索", "",
                nameof(EnhanceChineseSearch),
                nameof(SearchScope),
                nameof(ExcludeOriginalTitleFromSearch));

            AddGroup("深度删除", "",
                nameof(EnableDeepDelete));
            
            AddGroup("通知", "", 
                nameof(EnableNotificationEnhance),
                nameof(TakeOverSystemLibraryNew));
            
            AddGroup("UI功能", "",
                nameof(EnableEpisodeBackdropFallback),
                nameof(HidePersonNoImage),
                nameof(HidePersonPreference),
                nameof(EnablePinyinSortName),
                nameof(EnableNfoMetadataEnhance),
                nameof(NoBoxsetsAutoCreation),
                nameof(EnforceLibraryOrder));

            AddGroup("日志", "",
                nameof(EnableDetailedNetworkRequestLogging),
                nameof(EnableSystemLogReverse),
                nameof(SystemLogNameBlacklist));
            
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

        private static string GetHidePersonOptionDisplayName(HidePersonOption item)
        {
            switch (item)
            {
                case HidePersonOption.NoImage:
                    return "隐藏无图演职人员";
                case HidePersonOption.ActorOnly:
                    return "隐藏导演编剧，仅显示演员";
                default:
                    return item.ToString();
            }
        }
    }
}
