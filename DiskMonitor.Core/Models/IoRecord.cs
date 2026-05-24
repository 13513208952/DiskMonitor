namespace DiskMonitor.Core.Models;

public sealed class IoRecord
{
    public required string   Date         { get; init; }   // "YYYY-MM-DD"
    public required string   ProcessName  { get; init; }
    public required string   ProcessPath  { get; init; }
    public required string   DriveLetter  { get; init; }
    public required string   VolumeLabel  { get; init; }
    public required string   VolumeGuid   { get; init; }
    public required int      DiskNumber   { get; init; }
    public required string   DiskModel    { get; init; }
    public          long     ReadBytes    { get; set; }
    public          long     WriteBytes   { get; set; }
}
