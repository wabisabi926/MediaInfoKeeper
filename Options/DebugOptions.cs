using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.GenericEdit;
using System;
using System.Collections.Generic;

namespace MediaInfoKeeper.Options
{
    public class DebugOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Debug";

        public override string EditorDescription => "调试用页面，主要控制 DLL 扫描范围。改完记得保存。";

        [DisplayName("DLL 白名单前缀")]
        [Description("按前缀匹配 DLL 文件名（分号或换行分隔）。留空表示不启用白名单。")]
        public string DllNameWhitelistPrefixes { get; set; } = string.Empty;

        [DisplayName("DLL 黑名单前缀")]
        [Description("按前缀匹配 DLL 文件名（分号或换行分隔）。默认：Microsoft.;System.;netstandard;mscorlib")]
        public string DllNameBlacklistPrefixes { get; set; } = "Microsoft.;System.;netstandard;mscorlib";

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

            AddGroup("调试", "",
                nameof(DllNameWhitelistPrefixes),
                nameof(DllNameBlacklistPrefixes));

            if (groupedItems.Count > 0)
            {
                root.EditorItems = groupedItems.ToArray();
            }

            return container;
        }
    }
}
