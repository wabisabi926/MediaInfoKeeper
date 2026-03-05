using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Attributes;

namespace MediaInfoKeeper.Configuration
{
    public class MainPageOptions : EditableOptionsBase
    {
        public override string EditorTitle => "MediaInfo Keeper";

        public override string EditorDescription => "将媒体信息与章节保存为 JSON，并在需要时从 JSON 恢复。";

        [DisplayName("启用插件")]
        [Description("启用后优先从 JSON 恢复，提取后再写入 JSON。")]
        public bool PlugginEnabled { get; set; } = true;

        [DisplayName("入库时提取媒体信息")]
        [Description("入库时若 JSON 不存在或恢复失败，提取媒体信息并写入 JSON。")]
        public bool ExtractMediaInfoOnItemAdded { get; set; } = true;

        [DisplayName("收藏时提取媒体信息")]
        [Description("收藏时触发提取媒体信息，并写入 JSON。")]
        public bool ExtractMediaInfoOnFavorite { get; set; } = true;

        [DisplayName("条目移除时删除 JSON")]
        [Description("启用后，条目移除时删除已持久化的 JSON。")]
        public bool DeleteMediaInfoJsonOnRemove { get; set; } = false;

        [DisplayName("禁用 Emby 系统 ffprobe")]
        [Description("开启后阻止 Emby 自带 ffprobe 运行，仅插件内部允许调用。")]
        public bool DisableSystemFfprobe { get; set; } = true;

        [DisplayName("MediaInfo JSON 存储根目录")]
        [Description("建议配置不要留空,如果留空以后文件夹结构改变，不方便定位json恢复媒体信息。为空时，JSON 保存到媒体文件同目录。填写后会保存在填写的目录下， /your-path/FileNameWithoutExtension-mediainfo.json。")]
        [Editor(typeof(EditorFolderPicker), typeof(EditorBase))]
        public string MediaInfoJsonRootFolder { get; set; } = string.Empty;

        [DisplayName("扫描最多并发数")]
        [Description("设置媒体信息多线程提取的最大并发数，默认 2。")]
        [MinValue(1), MaxValue(20)]
        public int MaxConcurrentCount { get; set; } = 2;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayName("追更媒体库")]
        [Description("用于入库触发与删除 JSON 逻辑；留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string CatchupLibraries { get; set; } = string.Empty;

        [DisplayName("计划任务媒体库")]
        [Description("用于计划任务范围；留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string ScheduledTaskLibraries { get; set; } = string.Empty;

        [DisplayName("最近入库时间窗口（天）")]
        [Description("用于“刷新媒体元数据”计划任务，仅处理指定天数内入库的条目，0 表示不限制。")]
        [MinValue(0)]
        [MaxValue(3650)]
        public int RecentItemsDays { get; set; } = 3;

        [DisplayName("最近入库媒体筛选数量")]
        [Description("用于“提取媒体信息、扫描片头”计划任务，默认 100。")]
        [MinValue(1)]
        [MaxValue(100000000)]
        public int RecentItemsLimit { get; set; } = 100;

        [Browsable(false)]
        public List<EditorSelectOption> RefreshMetadataModeOptions { get; set; } = new List<EditorSelectOption>
        {
            new EditorSelectOption { Value = "Fill", Name = "补全缺失" },
            new EditorSelectOption { Value = "Replace", Name = "全部替换" }
        };

        [DisplayName("元数据刷新模式")]
        [Description("单选，选择“补全缺失”或“全部替换”元数据/图片。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(RefreshMetadataModeOptions))]
        public string RefreshMetadataMode { get; set; } = "Fill";

        [Browsable(false)]
        public List<EditorSelectOption> RefreshImageModeOptions { get; set; } = new List<EditorSelectOption>
        {
            new EditorSelectOption { Value = "Fill", Name = "补全缺失" },
            new EditorSelectOption { Value = "Replace", Name = "全部替换" }
        };

        [DisplayName("图片刷新模式")]
        [Description("单选，选择“补全缺失”或“全部替换”图片。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(RefreshImageModeOptions))]
        public string RefreshImageMode { get; set; } = "Fill";

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

            AddGroup("插件",
                nameof(PlugginEnabled));

            AddGroup("媒体信息",
                nameof(ExtractMediaInfoOnItemAdded),
                nameof(ExtractMediaInfoOnFavorite),
                nameof(DeleteMediaInfoJsonOnRemove),
                nameof(DisableSystemFfprobe),
                nameof(MediaInfoJsonRootFolder),
                nameof(MaxConcurrentCount));

            AddGroup("媒体库范围",
                nameof(CatchupLibraries),
                nameof(ScheduledTaskLibraries));

            AddGroup("计划任务",
                nameof(RecentItemsDays),
                nameof(RecentItemsLimit),
                nameof(RefreshMetadataMode),
                nameof(RefreshImageMode));

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
                groupedItems.Add(new EditorGroup("其他", remaining.ToArray(), $"group{groupIndex}", root.Id, null));
            }

            if (groupedItems.Count > 0)
            {
                root.EditorItems = groupedItems.ToArray();
            }

            return container;
        }
    }
}
