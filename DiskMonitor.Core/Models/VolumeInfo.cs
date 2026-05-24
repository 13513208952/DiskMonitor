namespace DiskMonitor.Core.Models;

public sealed class VolumeInfo
{
    public required string VolumeGuid    { get; init; }   // {xxxxxxxx-...}  永久稳定
    public required string DriveLetter   { get; init; }   // "C:"  可能变化
    public          string VolumeLabel   { get; init; } = "";
    public required int    DiskNumber    { get; init; }   // 可能变化
    public          string DiskModel     { get; init; } = "";
    public required bool   IsRemovable   { get; init; }

    // ETW NT 设备路径，例如 "\Device\HarddiskVolume3"（QueryDosDevice 返回值，无尾部反斜杠）
    public string DevicePath  { get; init; } = "";

    // Win32 路径前缀匹配用，例如 @"C:\"
    public string PathPrefix => DriveLetter + @"\";
}
