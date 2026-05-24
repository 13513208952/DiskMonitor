namespace DiskMonitor.Core.Models;

public sealed class ProcessInfo
{
    public required int    Pid       { get; init; }
    public required string Name      { get; init; }   // "chrome.exe"
    public required string Path      { get; init; }   // "C:\Program Files\..."
    public required DateTime StartTime { get; init; }

    public static readonly ProcessInfo System  = new() { Pid = 4,  Name = "[System]",  Path = "", StartTime = DateTime.MinValue };
    public static readonly ProcessInfo Unknown = new() { Pid = -1, Name = "[Unknown]", Path = "", StartTime = DateTime.MinValue };
}
