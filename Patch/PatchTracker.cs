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
