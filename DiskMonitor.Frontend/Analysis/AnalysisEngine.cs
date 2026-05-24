using DiskMonitor.Core.Models;
using DiskMonitor.Frontend;

namespace DiskMonitor.Frontend.Analysis;

public static class AnalysisEngine
{
    public static readonly IReadOnlySet<string> KnownSystemProcesses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[System]", "[Unknown]", "System",
            "svchost.exe", "lsass.exe", "services.exe", "wininit.exe",
            "csrss.exe", "smss.exe", "winlogon.exe", "spoolsv.exe",
            "MsMpEng.exe", "NisSrv.exe",
            "SearchIndexer.exe", "SearchHost.exe", "SearchProtocolHost.exe",
            "TiWorker.exe", "TrustedInstaller.exe", "WmiPrvSE.exe",
            "RuntimeBroker.exe", "SgrmBroker.exe",
            "wsappx.exe", "wuauclt.exe", "wusa.exe",
            "taskhostw.exe", "backgroundTaskHost.exe",
            "SecurityHealthService.exe", "SecurityHealthSystray.exe",
            "AggregatorHost.exe", "GameBarPresenceWriter.exe",
        };

    // ── Main entry point ─────────────────────────────────────────

    public static AnalysisResult Analyze(
        IReadOnlyList<IoRecord> allRecords,
        AnalysisConfig config,
        string checkDate)
    {
        if (allRecords.Count == 0)
            return new AnalysisResult();

        // Apply global exclusions, keep a full copy for system-fraction check
        var effective = Filter(allRecords, config);

        int totalDays = effective.Select(r => r.Date).Distinct().Count();
        bool enoughData = totalDays >= 30;

        var byVolume = effective
            .GroupBy(r => r.VolumeGuid)
            .ToDictionary(g => g.Key, g => g.ToList());

        var alerts    = new List<AlertEntry>();
        var statsList = new List<DiskStats>();

        foreach (var (guid, volRecs) in byVolume)
        {
            var meta = volRecs.First();

            var dailyMap = volRecs
                .GroupBy(r => r.Date)
                .ToDictionary(g => g.Key,
                    g => (Read: g.Sum(r => r.ReadBytes),
                          Write: g.Sum(r => r.WriteBytes)));

            var baselineDays = dailyMap
                .Where(kvp => kvp.Key != checkDate)
                .OrderBy(kvp => kvp.Key)
                .ToList();

            var ds = BuildStats(guid, meta, baselineDays, dailyMap);
            statsList.Add(ds);

            // Alert check
            if (!enoughData || baselineDays.Count < 30) continue;
            if (!dailyMap.TryGetValue(checkDate, out var today)) continue;

            long todayTotal = today.Read + today.Write;

            // Loose mode: suppress if system processes dominate today's I/O
            if (config.LooseMode)
            {
                long sysIo = allRecords
                    .Where(r => r.Date == checkDate && r.VolumeGuid == guid &&
                                KnownSystemProcesses.Contains(r.ProcessName))
                    .Sum(r => r.ReadBytes + r.WriteBytes);

                if (todayTotal > 0 && sysIo > todayTotal * 0.5)
                    continue;
            }

            // ── Total (always checked: auto or user-set) ──
            long totalThreshold = GetThreshold(config, guid, meta.DiskNumber, ds.FilteredMaxTotal, config.LooseMode);
            if (todayTotal > totalThreshold)
            {
                alerts.Add(new AlertEntry
                {
                    VolumeGuid       = guid,
                    DriveLetter      = meta.DriveLetter,
                    VolumeLabel      = meta.VolumeLabel,
                    DiskModel        = meta.DiskModel,
                    Date             = checkDate,
                    ActualTotalBytes = todayTotal,
                    ActualReadBytes  = today.Read,
                    ActualWriteBytes = today.Write,
                    NormalRangeMax   = ds.FilteredMaxTotal,
                    ThresholdBytes   = totalThreshold,
                    ExcessRatio      = totalThreshold > 0 ? (double)todayTotal / totalThreshold : 0,
                    ExceededType     = "total",
                });
            }

            // ── Read/Write: only when user explicitly configured ──
            if (TryGetExplicitThreshold(config, guid, meta.DiskNumber, "read", out long readThreshold)
                && today.Read > readThreshold)
            {
                alerts.Add(new AlertEntry
                {
                    VolumeGuid       = guid,
                    DriveLetter      = meta.DriveLetter,
                    VolumeLabel      = meta.VolumeLabel,
                    DiskModel        = meta.DiskModel,
                    Date             = checkDate,
                    ActualTotalBytes = todayTotal,
                    ActualReadBytes  = today.Read,
                    ActualWriteBytes = today.Write,
                    NormalRangeMax   = ds.FilteredMaxRead,
                    ThresholdBytes   = readThreshold,
                    ExcessRatio      = readThreshold > 0 ? (double)today.Read / readThreshold : 0,
                    ExceededType     = "read",
                });
            }

            if (TryGetExplicitThreshold(config, guid, meta.DiskNumber, "write", out long writeThreshold)
                && today.Write > writeThreshold)
            {
                alerts.Add(new AlertEntry
                {
                    VolumeGuid       = guid,
                    DriveLetter      = meta.DriveLetter,
                    VolumeLabel      = meta.VolumeLabel,
                    DiskModel        = meta.DiskModel,
                    Date             = checkDate,
                    ActualTotalBytes = todayTotal,
                    ActualReadBytes  = today.Read,
                    ActualWriteBytes = today.Write,
                    NormalRangeMax   = ds.FilteredMaxWrite,
                    ThresholdBytes   = writeThreshold,
                    ExcessRatio      = writeThreshold > 0 ? (double)today.Write / writeThreshold : 0,
                    ExceededType     = "write",
                });
            }
        }

        return new AnalysisResult
        {
            TotalDays  = totalDays,
            EnoughData = enoughData,
            Alerts     = alerts,
            DiskStats  = statsList,
        };
    }

    // Builds stats for a single volume's baseline days (excludes checkDate).
    private static DiskStats BuildStats(
        string guid,
        IoRecord meta,
        List<KeyValuePair<string, (long Read, long Write)>> baselineDays,
        Dictionary<string, (long Read, long Write)> dailyMap)
    {
        var totals = baselineDays.Select(d => d.Value.Read + d.Value.Write).OrderBy(v => v).ToList();
        var reads  = baselineDays.Select(d => d.Value.Read ).OrderBy(v => v).ToList();
        var writes = baselineDays.Select(d => d.Value.Write).OrderBy(v => v).ToList();

        var ft = RemoveOutliersIQR(totals);
        var fr = RemoveOutliersIQR(reads);
        var fw = RemoveOutliersIQR(writes);

        return new DiskStats
        {
            VolumeGuid    = guid,
            DriveLetter   = meta.DriveLetter,
            VolumeLabel   = meta.VolumeLabel,
            DiskModel     = meta.DiskModel,
            DiskNumber    = meta.DiskNumber,
            DataDays      = baselineDays.Count,

            RawMaxTotal      = totals.Count > 0 ? totals.Last()  : 0,
            FilteredMaxTotal = ft.Count > 0     ? ft.Last()      : 0,
            MeanTotal        = ft.Count > 0     ? ft.Average()   : 0,
            MedianTotal      = Median(ft),
            Top10PctTotal    = Percentile(totals, 90),
            Top5TotalDays    = baselineDays
                .OrderByDescending(d => d.Value.Read + d.Value.Write)
                .Take(5)
                .Select(d => (d.Key, d.Value.Read + d.Value.Write))
                .ToList(),

            RawMaxRead      = reads.Count > 0 ? reads.Last() : 0,
            FilteredMaxRead = fr.Count   > 0 ? fr.Last()    : 0,
            Top10PctRead    = Percentile(reads, 90),

            RawMaxWrite      = writes.Count > 0 ? writes.Last() : 0,
            FilteredMaxWrite = fw.Count    > 0 ? fw.Last()     : 0,
            Top10PctWrite    = Percentile(writes, 90),
        };
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static List<IoRecord> Filter(IReadOnlyList<IoRecord> records, AnalysisConfig cfg)
    {
        var result = records.Where(r =>
            !IsExcludedProcess(r.ProcessName, cfg)      &&
            !IsExcludedDrive(r, cfg)                     &&
            !IsExcludedDate(r.Date, r.VolumeGuid, cfg)
        ).ToList();

        if (cfg.IgnoreAllBefore != null)
            result = result.Where(r =>
                string.Compare(r.Date, cfg.IgnoreAllBefore, StringComparison.Ordinal) >= 0
            ).ToList();

        return result;
    }

    private static bool IsExcludedProcess(string name, AnalysisConfig cfg)
    {
        if (cfg.ExcludeSystemProcesses && KnownSystemProcesses.Contains(name)) return true;
        if (cfg.ExcludeExplorer && name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return true;
        return cfg.ExcludedProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsExcludedDrive(IoRecord r, AnalysisConfig cfg) =>
        cfg.ExcludedVolumes.Any(v => v.Guid == r.VolumeGuid) ||
        cfg.ExcludedDisks.Any(d => d.DiskNumber == r.DiskNumber);

    private static bool IsExcludedDate(string date, string guid, AnalysisConfig cfg) =>
        cfg.ExcludedDateRules.Any(rule => rule.AppliesToDate(date) && rule.AppliesToVolume(guid));

    private static long GetThreshold(AnalysisConfig cfg, string guid, int diskNum, long filteredMax, bool loose)
    {
        if (cfg.VolumeThresholds.TryGetValue(guid, out var vt) && vt.TotalBytes.HasValue)
            return vt.TotalBytes.Value;
        if (cfg.DiskThresholds.TryGetValue(diskNum.ToString(), out var dt) && dt.TotalBytes.HasValue)
            return dt.TotalBytes.Value;
        if (cfg.GlobalThreshold.TotalBytes.HasValue)
            return cfg.GlobalThreshold.TotalBytes.Value;
        return loose ? (long)(filteredMax * 1.15) : filteredMax;
    }

    // Returns true only if the user explicitly set a threshold for this type.
    // "type" is "read" or "write".
    private static bool TryGetExplicitThreshold(
        AnalysisConfig cfg, string guid, int diskNum, string type, out long threshold)
    {
        threshold = 0;
        Func<ThresholdEntry, long?> pick = type == "read"
            ? e => e.ReadBytes
            : e => e.WriteBytes;

        if (cfg.VolumeThresholds.TryGetValue(guid, out var vt) && pick(vt) is long vb)
            { threshold = vb; return true; }
        if (cfg.DiskThresholds.TryGetValue(diskNum.ToString(), out var dt) && pick(dt) is long db)
            { threshold = db; return true; }
        if (pick(cfg.GlobalThreshold) is long gb)
            { threshold = gb; return true; }
        return false;
    }

    internal static List<long> RemoveOutliersIQR(List<long> sorted)
    {
        if (sorted.Count < 4) return sorted;
        double q1 = sorted[sorted.Count / 4];
        double q3 = sorted[sorted.Count * 3 / 4];
        double iqr = q3 - q1;
        double lower = q1 - 1.5 * iqr;
        double upper = q3 + 1.5 * iqr;
        return sorted.Where(v => v >= lower && v <= upper).ToList();
    }

    internal static double Median(List<long> sorted)
    {
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    internal static long Percentile(List<long> sorted, int pct)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Ceiling(sorted.Count * pct / 100.0) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}

// ── Result models ─────────────────────────────────────────────────

public sealed class AnalysisResult
{
    public int            TotalDays  { get; init; }
    public bool           EnoughData { get; init; }
    public List<AlertEntry> Alerts   { get; init; } = [];
    public List<DiskStats>  DiskStats { get; init; } = [];
}

public sealed class AlertEntry
{
    public string VolumeGuid       { get; init; } = "";
    public string DriveLetter      { get; init; } = "";
    public string VolumeLabel      { get; init; } = "";
    public string DiskModel        { get; init; } = "";
    public string Date             { get; init; } = "";
    public long   ActualTotalBytes { get; init; }
    public long   ActualReadBytes  { get; init; }
    public long   ActualWriteBytes { get; init; }
    public long   NormalRangeMax   { get; init; }
    public long   ThresholdBytes   { get; init; }
    public double ExcessRatio      { get; init; }
    public string ExceededType     { get; init; } = "total"; // "total" | "read" | "write"

    public string DriveDisplay          => string.IsNullOrEmpty(VolumeLabel) ? DriveLetter : $"{DriveLetter} ({VolumeLabel})";
    public string ExceededTypeDisplay   => ExceededType switch { "read" => "读取", "write" => "写入", _ => "合计" };
    public string ActualDisplay    => IoRecordVm.Fmt(ActualTotalBytes);
    public string ReadDisplay      => IoRecordVm.Fmt(ActualReadBytes);
    public string WriteDisplay     => IoRecordVm.Fmt(ActualWriteBytes);
    public string ThresholdDisplay => IoRecordVm.Fmt(ThresholdBytes);
    public string ExcessDisplay    => $"+{(ExcessRatio - 1) * 100:F0}%";
}

public sealed class DiskStats
{
    public string VolumeGuid  { get; init; } = "";
    public string DriveLetter { get; init; } = "";
    public string VolumeLabel { get; init; } = "";
    public string DiskModel   { get; init; } = "";
    public int    DiskNumber  { get; init; }
    public int    DataDays    { get; init; }

    public long   RawMaxTotal      { get; init; }
    public long   FilteredMaxTotal { get; init; }
    public double MeanTotal        { get; init; }
    public double MedianTotal      { get; init; }
    public long   Top10PctTotal    { get; init; }
    public List<(string Date, long Total)> Top5TotalDays { get; init; } = [];

    public long RawMaxRead      { get; init; }
    public long FilteredMaxRead { get; init; }
    public long Top10PctRead    { get; init; }

    public long RawMaxWrite      { get; init; }
    public long FilteredMaxWrite { get; init; }
    public long Top10PctWrite    { get; init; }

    public string DriveDisplay        => string.IsNullOrEmpty(VolumeLabel) ? DriveLetter : $"{DriveLetter} ({VolumeLabel})";
    public string RawMaxDisplay       => IoRecordVm.Fmt(RawMaxTotal);
    public string FilteredMaxDisplay  => IoRecordVm.Fmt(FilteredMaxTotal);
    public string Top10PctDisplay     => IoRecordVm.Fmt(Top10PctTotal);
    public string Top5Display         => Top5TotalDays.Count == 0 ? "—"
        : string.Join("  |  ", Top5TotalDays.Select(d => $"{d.Date}: {IoRecordVm.Fmt(d.Total)}"));
    public string MeanDisplay         => IoRecordVm.Fmt((long)MeanTotal);
    public string MedianDisplay       => IoRecordVm.Fmt((long)MedianTotal);
}
