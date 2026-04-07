using System.ComponentModel;
using System.IO;
using System.Collections.Generic;
using System;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

namespace MediaInfoKeeper.Options
{
    public class MediaInfoOptions : EditableOptionsBase
    {
        public enum IsRemoteOverrideOption
        {
            EmbyDefault,
            ForceTrue,
            ForceFalse
        }

        public override string EditorTitle => "媒体信息";

        public override string EditorDescription => "媒体信息持久化、入库提取、JSON 路径和并发控制都放在这里。改完记得保存。";

        [DisplayName("入库时提取媒体信息")]
        [Description("入库时若 JSON 不存在或恢复失败，提取媒体信息并写入 JSON。")]
        public bool ExtractMediaInfoOnItemAdded { get; set; } = true;

        [DisplayName("条目移除时删除 JSON")]
        [Description("启用后，条目移除时删除已持久化的 JSON。")]
        public bool DeleteMediaInfoJsonOnRemove { get; set; } = false;

        [DisplayName("MediaInfo JSON 存储根目录")]
        [Description("默认使用 Emby的 /config/data/MediaInfoKeeper 子目录保存。视频等媒体保存在 /your-path/FileNameWithoutExtension-mediainfo.json；音频保存在 /your-path/music/FileNameWithoutExtension-mediainfo.json。若当前值为空，JSON 保存到媒体文件同目录。")]
        [EditFolderPicker]
        public string MediaInfoJsonRootFolder { get; set; } = GetDefaultMediaInfoJsonRootFolder();

        [DisplayName("扫描最多并发数")]
        [Description("设置插件刷新任务的最大并发数，媒体信息提取和元数据刷新共用此限制，默认 3。")]
        [MinValue(1), MaxValue(20)]
        public int MaxConcurrentCount { get; set; } = 3;

        [Browsable(false)]
        public List<EditorSelectOption> IsRemoteOverrideList { get; set; } = new List<EditorSelectOption>();

        [DisplayName("媒体源 IsRemote 策略")]
        [Description("仅对 .strm 条目生效。strm 内容是 http 时建议设为 true，用于解决部分情况下播放无法拖动进度条的情况。")]
        [Editor(typeof(EditorSelectSingle), typeof(EditorBase))]
        [SelectItemsSource(nameof(IsRemoteOverrideList))]
        public string IsRemoteOverride { get; set; } = IsRemoteOverrideOption.EmbyDefault.ToString();

        public void Initialize()
        {
            if (string.IsNullOrWhiteSpace(IsRemoteOverride))
            {
                IsRemoteOverride = IsRemoteOverrideOption.EmbyDefault.ToString();
            }

            IsRemoteOverrideList.Clear();
            foreach (IsRemoteOverrideOption item in Enum.GetValues(typeof(IsRemoteOverrideOption)))
            {
                IsRemoteOverrideList.Add(new EditorSelectOption
                {
                    Value = item.ToString(),
                    Name = item switch
                    {
                        IsRemoteOverrideOption.EmbyDefault => "由 Emby 判断",
                        IsRemoteOverrideOption.ForceTrue => "强制 true",
                        _ => "强制 false"
                    },
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

            root.EditorItems = new EditorBase[]
            {
                new EditorGroup("媒体信息", root.EditorItems, "group1", root.Id, null)
                {
                    Description = "插件会持续监听 .strm 文件内容变更，并阻止 Emby 系统 ffprobe/ffmpeg 运行；仅在插件内部需要提取媒体信息时按需放行。"
                }
            };

            return container;
        }

        internal static string GetDefaultMediaInfoJsonRootFolder()
        {
            try
            {
                var programDataPath = Plugin.Instance?.AppHost?.Resolve<IApplicationPaths>()?.ProgramDataPath;
                if (!string.IsNullOrWhiteSpace(programDataPath))
                {
                    return Path.Combine(programDataPath, "data", Plugin.PluginName);
                }
            }
            catch
            {
            }

            return Path.Combine("/config", "data", Plugin.PluginName);
        }
    }
}
