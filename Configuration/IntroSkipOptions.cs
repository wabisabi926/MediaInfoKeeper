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
    public class IntroSkipOptions : EditableOptionsBase
    {
        public override string EditorTitle => "IntroSkip";

        [DisplayName("启用 Strm 片头检测解锁")]
        [Description("开启后允许 .strm 参与 Emby 原生片头指纹探测。")]
        public bool UnlockIntroSkip { get; set; } = true;

        [DisplayName("入库时扫描片头")]
        [Description("新剧集入库时触发片头检测。")]
        public bool ScanIntroOnItemAdded { get; set; } = true;

        [DisplayName("收藏时扫描片头")]
        [Description("收藏时触发对应媒体片头检测。")]
        public bool ScanIntroOnFavorite { get; set; } = true;
        
        [DisplayName("保护片头标记")]
        [Description("刷新元数据时保护已存在的片头/片尾标记不被清空。")]
        public bool ProtectIntroMarkers { get; set; } = true;

        [DisplayName("启用播放行为打标")]
        [Description("根据播放行为自动标记片头/片尾。")]
        public bool EnableIntroSkip { get; set; } = false;

        [DisplayName("最大片头时长(秒)")]
        [Description("超过此时间不再认为是片头区间。")]
        [MinValue(10), MaxValue(600)]
        [Required]
        public int MaxIntroDurationSeconds { get; set; } = 150;

        [DisplayName("最大片尾时长(秒)")]
        [Description("距结尾小于该时长时可标记片尾。")]
        [MinValue(10), MaxValue(600)]
        [Required]
        public int MaxCreditsDurationSeconds { get; set; } = 360;

        [DisplayName("最短剧情起始(秒)")]
        [Description("用于避免把前置剧情误判为片头。")]
        [MinValue(30), MaxValue(120)]
        [Required]
        public int MinOpeningPlotDurationSeconds { get; set; } = 60;

        [DisplayName("片头指纹分钟数")]
        [Description("范围 2-20，默认 10。将同步到媒体库的 IntroDetectionFingerprintLength。")]
        [MinValue(2), MaxValue(20)]
        [Required]
        public int IntroDetectionFingerprintMinutes { get; set; } = 10;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayName("片头检测库范围")]
        [Description("留空表示所有开启片头检测的剧集库。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string MarkerEnabledLibraryScope { get; set; } = string.Empty;

        [DisplayName("打标库范围")]
        [Description("用于播放行为打标，留空表示所有剧集库。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string LibraryScope { get; set; } = string.Empty;

        [DisplayName("用户范围")]
        [Description("允许触发打标的用户 ID，逗号或分号分隔；留空表示所有用户。")]
        public string UserScope { get; set; } = string.Empty;

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

            AddGroup("扫描片头",
                nameof(UnlockIntroSkip),
                nameof(ScanIntroOnItemAdded),
                nameof(ScanIntroOnFavorite),
                nameof(ProtectIntroMarkers));

            AddGroup("播放行为打标",
                nameof(EnableIntroSkip),
                nameof(MaxIntroDurationSeconds),
                nameof(MaxCreditsDurationSeconds),
                nameof(MinOpeningPlotDurationSeconds),
                nameof(IntroDetectionFingerprintMinutes),
                nameof(MarkerEnabledLibraryScope),
                nameof(LibraryScope),
                nameof(UserScope));

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
