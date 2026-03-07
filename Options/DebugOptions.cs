using System.ComponentModel;
using Emby.Web.GenericEdit;

namespace MediaInfoKeeper.Options
{
    public class DebugOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Debug";

        public override string EditorDescription => "仅用于 DEBUG 构建的开发调试配置。";

        [DisplayName("DLL 白名单前缀")]
        [Description("按前缀匹配 DLL 文件名（分号或换行分隔）。留空表示不启用白名单。")]
        public string DllNameWhitelistPrefixes { get; set; } = string.Empty;

        [DisplayName("DLL 黑名单前缀")]
        [Description("按前缀匹配 DLL 文件名（分号或换行分隔）。默认：Microsoft.;System.;netstandard;mscorlib")]
        public string DllNameBlacklistPrefixes { get; set; } = "Microsoft.;System.;netstandard;mscorlib";
    }
}
