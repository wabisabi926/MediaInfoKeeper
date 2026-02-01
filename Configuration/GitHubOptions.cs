using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.GenericEdit;

namespace MediaInfoKeeper.Configuration
{
    public class GitHubOptions : EditableOptionsBase
    {
        public override string EditorTitle => "GitHub";

        [DisplayName("GitHub 访问令牌")]
        [Description("设置后使用 Token 获取 Releases，避免未认证请求的限流。")]
        public string GitHubToken { get; set; } = string.Empty;

        [DisplayName("项目地址")]
        [Description("项目初期，有许多不完善的地方，请及时关注更新。")] 
        public string ProjectUrl { get; set; } = "https://github.com/honue/MediaInfoKeeper";
        
        [DisplayName("当前版本")]
        [Description("计划任务可以更新插件。")]
        public string CurrentVersion { get; set; } = "未知";
        
        [DisplayName("最新版本")]
        [Description("从 GitHub Releases 获取最新版本号。")]
        public string LatestReleaseVersion { get; set; } = "加载中";

        [DisplayName("更新说明")]
        public string ReleaseHistoryBody { get; set; } = "加载中";

        public override IEditObjectContainer CreateEditContainer()
        {
            var container = (EditObjectContainer)base.CreateEditContainer();
            var root = container.EditorRoot;
            if (root?.EditorItems == null || root.EditorItems.Length == 0)
            {
                return container;
            }

            var items = new List<EditorBase>(root.EditorItems.Length);
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
            }

            root.EditorItems = items.ToArray();
            return container;
        }
    }
}
