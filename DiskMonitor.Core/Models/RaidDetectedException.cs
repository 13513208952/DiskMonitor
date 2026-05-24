namespace DiskMonitor.Core.Models;

public sealed class RaidDetectedException : Exception
{
    public IReadOnlyList<string> AffectedVolumes { get; }

    public RaidDetectedException(IReadOnlyList<string> affectedVolumes)
        : base($"检测到 RAID 或跨盘卷，不支持此配置。受影响的卷：{string.Join(", ", affectedVolumes)}")
    {
        AffectedVolumes = affectedVolumes;
    }
}
