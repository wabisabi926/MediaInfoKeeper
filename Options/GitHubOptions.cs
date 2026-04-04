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
    public class GitHubOptions : EditableOptionsBase
    {
        public enum UpdateChannelOption
        {
            Stable,
            Beta
        }

        public override string EditorTitle => "GitHub";

        public override string EditorDescription => "当前版本、最新发布说明，建议把 GitHub Token 配好，并配置更新版本的计划任务。改完记得保存。";

        [DisplayName("GitHub 访问令牌")]
        [Description("设置后使用 Token 获取 Releases，避免未认证请求的限流。")]
        public string GitHubToken { get; set; } = string.Empty;

        [Browsable(false)]
        public List<EditorSelectOption> UpdateChannelList { get; set; } = new List<EditorSelectOption>();

        [DisplayName("更新频道")]
        [Description("稳定版只跟随正式 Release；Beta 会优先跟随预发布版本。")]
        [Editor(typeof(EditorSelectSingle), typeof(EditorBase))]
        [SelectItemsSource(nameof(UpdateChannelList))]
        public string UpdateChannel { get; set; } = UpdateChannelOption.Stable.ToString();

        [DisplayName("项目地址")]
        [Description("项目初期，有许多不完善的地方，请及时关注更新。")]
        public string ProjectUrl { get; set; } = "https://github.com/honue/MediaInfoKeeper";

        [DisplayName("当前版本")]
        [Description("计划任务可以更新插件。")]
        public string CurrentVersion { get; set; } = "未知";

        [DisplayName("最新版本")]
        [Description("按当前更新频道从 GitHub Releases 获取最新版本号。")]
        public string LatestReleaseVersion { get; set; } = "加载中";

        [DisplayName("更新说明")]
        public string ReleaseHistoryBody { get; set; } = "加载中";

        public void Initialize()
        {
            if (string.IsNullOrWhiteSpace(UpdateChannel))
            {
                UpdateChannel = UpdateChannelOption.Stable.ToString();
            }

            UpdateChannelList.Clear();
            foreach (UpdateChannelOption item in Enum.GetValues(typeof(UpdateChannelOption)))
            {
                UpdateChannelList.Add(new EditorSelectOption
                {
                    Name = item == UpdateChannelOption.Stable ? "Stable" : "Beta",
                    Value = item.ToString(),
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

            var items = new List<EditorBase>(root.EditorItems.Length);
            var itemLookup = new Dictionary<string, EditorBase>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.EditorItems)
            {
                var key = item.Name ?? item.Id;
                if (item is EditorText text)
                {
                    if (string.Equals(key, nameof(ReleaseHistoryBody), StringComparison.OrdinalIgnoreCase))
                    {
                        text.IsReadOnly = true;
                        text.MultiLine = true;
                        text.LineCount = 12;
                        text.AllowEmpty = true;
                    }
                    else if (string.Equals(key, nameof(ProjectUrl), StringComparison.OrdinalIgnoreCase))
                    {
                        text.IsReadOnly = true;
                        text.AllowEmpty = true;
                    }
                    else if (string.Equals(key, nameof(LatestReleaseVersion), StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(key, nameof(CurrentVersion), StringComparison.OrdinalIgnoreCase))
                    {
                        text.IsReadOnly = true;
                        text.AllowEmpty = true;
                    }
                }

                items.Add(item);
                if (!string.IsNullOrEmpty(key) && !itemLookup.ContainsKey(key))
                {
                    itemLookup.Add(key, item);
                }
            }

            var groupedItems = new List<EditorBase>();
            var groupIndex = 0;

            void AddGroup(string title, string description, params string[] propertyNames)
            {
                var groupItems = new List<EditorBase>();
                foreach (var propertyName in propertyNames)
                {
                    if (itemLookup.TryGetValue(propertyName, out var item))
                    {
                        groupItems.Add(item);
                        itemLookup.Remove(propertyName);
                    }
                }

                if (groupItems.Count == 0)
                {
                    return;
                }

                groupIndex++;
                var group = new EditorGroup(title, groupItems.ToArray(), $"group{groupIndex}", root.Id, null)
                {
                    Description = description
                };
                groupedItems.Add(group);
            }

            AddGroup("GitHub", "",
                nameof(GitHubToken),
                nameof(UpdateChannel),
                nameof(ProjectUrl),
                nameof(CurrentVersion),
                nameof(LatestReleaseVersion),
                nameof(ReleaseHistoryBody));

            var remaining = new List<EditorBase>();
            foreach (var item in items)
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

            root.EditorItems = groupedItems.Count > 0 ? groupedItems.ToArray() : items.ToArray();
            return container;
        }
    }
}
