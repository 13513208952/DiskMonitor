using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskMonitor.Frontend.Analysis;

public sealed class AnalysisConfig
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiskMonitor", "analysis_config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Top-level toggles ────────────────────────────────────────
    public bool Enabled   { get; set; } = true;
    public bool LooseMode { get; set; } = true;

    // ── Drive exclusions ─────────────────────────────────────────
    public List<VolumeExclusion> ExcludedVolumes { get; set; } = [];
    public List<DiskExclusion>   ExcludedDisks   { get; set; } = [];

    // ── Process exclusions ───────────────────────────────────────
    public bool         ExcludeSystemProcesses { get; set; } = false;
    public bool         ExcludeExplorer        { get; set; } = false;
    public List<string> ExcludedProcessNames   { get; set; } = [];

    // ── Date exclusions ──────────────────────────────────────────
    public List<DateExclusionRule> ExcludedDateRules { get; set; } = [];

    // Ignore all records strictly before this date ("YYYY-MM-DD")
    public string? IgnoreAllBefore { get; set; }

    // ── Custom thresholds ─────────────────────────────────────────
    // Key = VolumeGuid  or  DiskNumber.ToString()
    public Dictionary<string, ThresholdEntry> VolumeThresholds { get; set; } = [];
    public Dictionary<string, ThresholdEntry> DiskThresholds   { get; set; } = [];
    public ThresholdEntry                     GlobalThreshold  { get; set; } = new();
    public ShellWhitelistConfig               ShellWhitelist   { get; set; } = new();
    public SvcWhitelistConfig                 SvcWhitelist     { get; set; } = new();

    // ── Persistence ──────────────────────────────────────────────
    public static AnalysisConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AnalysisConfig>(json, JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }
}

public sealed class VolumeExclusion
{
    public string Guid        { get; set; } = "";
    public string DriveLetter { get; set; } = "";
    public string Label       { get; set; } = "";
}

public sealed class DiskExclusion
{
    public int    DiskNumber { get; set; }
    public string Model      { get; set; } = "";
}

public sealed class ThresholdEntry
{
    public long? ReadBytes  { get; set; }
    public long? WriteBytes { get; set; }
    public long? TotalBytes { get; set; }

    [JsonIgnore]
    public bool HasAny => ReadBytes.HasValue || WriteBytes.HasValue || TotalBytes.HasValue;
}

public sealed class ShellWhitelistConfig
{
    public List<string> VendorNames       { get; set; } = [];
    public List<string> FilePaths         { get; set; } = [];
    public List<string> Directories       { get; set; } = [];
    public bool         ExcludeSystemFolder { get; set; } = false;

    private static readonly string SysDir     = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public bool IsWhitelisted(ShellExtensionEntry e)
    {
        if (!string.IsNullOrEmpty(e.VendorName) &&
            VendorNames.Any(v => v.Equals(e.VendorName, StringComparison.OrdinalIgnoreCase))) return true;
        if (FilePaths.Any(f => f.Equals(e.FilePath, StringComparison.OrdinalIgnoreCase))) return true;
        if (Directories.Any(d => e.FilePath.StartsWith(d, StringComparison.OrdinalIgnoreCase))) return true;
        if (ExcludeSystemFolder &&
            (e.FilePath.StartsWith(SysDir,     StringComparison.OrdinalIgnoreCase) ||
             e.FilePath.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }
}

public sealed class SvcWhitelistConfig
{
    public List<string> VendorNames         { get; set; } = [];
    public List<string> ServiceNames        { get; set; } = [];
    public List<string> Directories         { get; set; } = [];
    public bool         ExcludeSystemFolder { get; set; } = false;

    private static readonly string SysDir     = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public bool IsWhitelisted(SvcHostServiceEntry e)
    {
        if (!string.IsNullOrEmpty(e.VendorName) &&
            VendorNames.Any(v => v.Equals(e.VendorName, StringComparison.OrdinalIgnoreCase))) return true;
        if (ServiceNames.Any(n => n.Equals(e.ServiceName, StringComparison.OrdinalIgnoreCase))) return true;
        if (!string.IsNullOrEmpty(e.ServiceDll))
        {
            if (Directories.Any(d => e.ServiceDll.StartsWith(d, StringComparison.OrdinalIgnoreCase))) return true;
            if (ExcludeSystemFolder &&
                (e.ServiceDll.StartsWith(SysDir,     StringComparison.OrdinalIgnoreCase) ||
                 e.ServiceDll.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }
}

public sealed class DateExclusionRule
{
    public string Id   { get; set; } = Guid.NewGuid().ToString("N")[..8];
    // "single" | "range" | "period_end"  (period_end: from this date onward — used for IgnorePeriodEnd)
    public string Type { get; set; } = "single";

    public string? Date  { get; set; }   // for "single"
    public string? Start { get; set; }   // for "range"
    public string? End   { get; set; }   // for "range"

    // null / empty = applies to all volumes
    public List<string>? VolumeGuids { get; set; }

    public string Description { get; set; } = "";

    public bool AppliesToVolume(string guid) =>
        VolumeGuids == null || VolumeGuids.Count == 0 || VolumeGuids.Contains(guid);

    public bool AppliesToDate(string date) => Type switch
    {
        "single" => date == Date,
        "range"  => string.Compare(date, Start, StringComparison.Ordinal) >= 0 &&
                    string.Compare(date, End,   StringComparison.Ordinal) <= 0,
        _        => false,
    };
}
