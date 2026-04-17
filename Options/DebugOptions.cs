using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.GenericEdit;
using System;
using System.Collections.Generic;

namespace MediaInfoKeeper.Options
{
    public class DebugOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Debug";

        public override string EditorDescription =>
            "调试用页面，主要控制 DLL 扫描范围。改完记得保存。\n\n" +
            "需要管理员鉴权，返回指定 internalId 条目的当前库内媒体流状态，以及 xxx-mediainfo.json、xxx-cover.jpg 的路径和内容，方便排查恢复问题。";

        [DisplayName("MediaInfo 调试接口")]
        [Description("只读，可直接复制。")]
        public string DebugMediaInfoUrl { get; set; } =
            "http://192.168.33.100:8091/MediaInfoKeeper/Items/DebugMediaInfo?InternalId=161483&api_key=123";

        [DisplayName("DLL 白名单前缀")]
        [Description("按前缀匹配 DLL 文件名（分号或换行分隔）。留空表示不启用白名单。")]
        public string DllNameWhitelistPrefixes { get; set; } = string.Empty;

        [DisplayName("DLL 黑名单前缀")]
        [Description("按前缀匹配 DLL 文件名（分号或换行分隔）。默认：Microsoft.;System.;netstandard;mscorlib")]
        public string DllNameBlacklistPrefixes { get; set; } = "Microsoft.;System.;netstandard;mscorlib";

        [DisplayName("启用 ffprocess 拦截")]
        [Description("关闭后不再拦截 Emby 自身的 ffprobe/ffmpeg 调用。默认开启，仅建议在调试时临时关闭。")]
        public bool EnableFfProcessGuard { get; set; } = true;

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
                if (item is EditorText text &&
                    string.Equals(key, nameof(DebugMediaInfoUrl), StringComparison.OrdinalIgnoreCase))
                {
                    text.IsReadOnly = true;
                    text.AllowEmpty = false;
                }

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

            AddGroup("调试", "",
                nameof(DebugMediaInfoUrl),
                nameof(DllNameWhitelistPrefixes),
                nameof(DllNameBlacklistPrefixes),
                nameof(EnableFfProcessGuard));

            if (groupedItems.Count > 0)
            {
                root.EditorItems = groupedItems.ToArray();
            }

            return container;
        }
    }
}
