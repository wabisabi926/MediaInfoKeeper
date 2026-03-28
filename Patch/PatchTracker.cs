namespace MediaInfoKeeper.Patch
{
    public enum PatchHealth
    {
        Unknown = 0,
        Enabled = 1,
        Disabled = 2,
        Waiting = 3,
        Failed = 4
    }

    /// <summary>
    /// 保存单个补丁的启用方式、健康状态和备注信息。
    /// </summary>
    public sealed class PatchTracker
    {
        public PatchTracker(string name)
        {
            Name = name ?? "unknown";
        }

        public string Name { get; }

        public PatchApproach Approach { get; set; } = PatchApproach.Harmony;

        public bool IsEnabled { get; set; } = true;

        public PatchHealth Health { get; set; } = PatchHealth.Unknown;

        public string Notes { get; set; }
    }
}
