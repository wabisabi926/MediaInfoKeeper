using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Attributes;

namespace MediaInfoKeeper.Options
{
    public class IntroSkipOptions : EditableOptionsBase
    {
        public override string EditorTitle => "IntroSkip";

        public override string EditorDescription => "片头相关设置都放在这里，包括扫描、标记保护和播放行为打标。改完记得保存。";

        [DisplayName("启用 Strm 片头检测解锁")]
        [Description("开启后允许 .strm 参与 Emby 原生片头指纹探测。")]
        public bool UnlockIntroSkip { get; set; } = true;

        [DisplayName("入库时扫描片头")]
        [Description("新剧集入库时触发片头检测。")]
        public bool ScanIntroOnItemAdded { get; set; } = true;

        [DisplayName("收藏时扫描片头")]
        [Description("收藏时触发对应媒体片头检测。")]
        public bool ScanIntroOnFavorite { get; set; } = true;

        [DisplayName("启用播放行为打标")]
        [Description("根据播放行为自动标记片头/片尾。")]
        public bool EnableIntroSkip { get; set; } = false;

        [DisplayName("最大片头时长(秒)")]
        [Description("超过此时间不再认为是片头区间。")]
        [MinValue(10), MaxValue(600)]
        [Required]
        public int MaxIntroDurationSeconds { get; set; } = 180;

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

        [DisplayName("片头探测最大并发数")]
        [Description("限制 AudioFingerprint 片头探测同时运行的条目数，默认 1。调大可加快扫描，但会增加 CPU 和磁盘压力。")]
        [MinValue(1), MaxValue(10)]
        [Required]
        public int IntroDetectionMaxConcurrentCount { get; set; } = 1;
        
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

            AddGroup("扫描片头", "",
                nameof(UnlockIntroSkip),
                nameof(ScanIntroOnItemAdded),
                nameof(ScanIntroOnFavorite),
                nameof(IntroDetectionFingerprintMinutes),
                nameof(IntroDetectionMaxConcurrentCount),
                nameof(MarkerEnabledLibraryScope));

            AddGroup("播放行为打标",
                "最短剧情起始前: 优先视为前置剧情保护区；最短剧情起始到最大片头时长: 片头更可信；" +
                "超过最大片头时长: 不再判为片头；距离结束小于最大片尾时长: 可判为片尾。",
                nameof(EnableIntroSkip),
                nameof(MinOpeningPlotDurationSeconds),
                nameof(MaxIntroDurationSeconds),
                nameof(MaxCreditsDurationSeconds),
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
