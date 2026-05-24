using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DiskMonitor.Frontend.Analysis;

public sealed class ShellExtensionEntry
{
    public string    Clsid             { get; init; } = "";
    public string    Description       { get; init; } = "";
    public string    FilePath          { get; init; } = "";
    public string    DllName           => string.IsNullOrEmpty(FilePath) ? "—" : Path.GetFileName(FilePath);
    public string    VendorName        { get; init; } = "";
    public bool      IsMicrosoft       { get; init; }
    public bool      IsGhost           { get; init; }
    public DateTime? FileCreationTime  { get; init; }
    public DateTime? FileLastWriteTime { get; init; }
    public DateTime? RegKeyWriteTime   { get; init; }

    public DateTime? LastModifiedTime =>
        FileLastWriteTime.HasValue && RegKeyWriteTime.HasValue
            ? (FileLastWriteTime > RegKeyWriteTime ? FileLastWriteTime : RegKeyWriteTime)
            : FileLastWriteTime ?? RegKeyWriteTime;

    public string CreationDisplay     => FileCreationTime?.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string LastModifiedDisplay => LastModifiedTime?.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string GhostLabel          => IsGhost ? "⚠ 路径无效" : "";
    public string PathDisplay         => string.IsNullOrEmpty(FilePath) ? "—" : FilePath;
}

public sealed class ExplorerDayBucket
{
    public string Date           { get; init; } = "";
    public long   ReadBytes      { get; init; }
    public long   WriteBytes     { get; init; }
    public long   TotalBytes     => ReadBytes + WriteBytes;
    public double ReadBarHeight  { get; set; }
    public double WriteBarHeight { get; set; }
    public string DayLabel       => Date.Length >= 10 ? Date[5..] : Date;
    public string Tooltip        => $"{Date}  读 {IoRecordVm.Fmt(ReadBytes)}  写 {IoRecordVm.Fmt(WriteBytes)}";
    public string ReadDisplay    => IoRecordVm.Fmt(ReadBytes);
    public string WriteDisplay   => IoRecordVm.Fmt(WriteBytes);
    public string TotalDisplay   => IoRecordVm.Fmt(TotalBytes);
}

public static class ShellExtensionScanner
{
    private const string ApprovedKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(
        SafeRegistryHandle hKey,
        StringBuilder? lpClass, ref uint lpcClass,
        nint lpReserved,
        out uint lpcSubKeys, out uint lpcMaxSubKeyLen, out uint lpcMaxClassLen,
        out uint lpcValues,  out uint lpcMaxValueNameLen, out uint lpcMaxValueLen,
        out uint lpcSecurityDescriptor,
        out long lpftLastWriteTime);

    public static List<ShellExtensionEntry> Scan()
    {
        var result = new List<ShellExtensionEntry>();

        using var approvedKey = Registry.LocalMachine.OpenSubKey(ApprovedKey, writable: false);
        if (approvedKey == null) return result;

        foreach (var valueName in approvedKey.GetValueNames())
        {
            if (!valueName.StartsWith('{')) continue;

            string   clsid       = valueName;
            string   description = approvedKey.GetValue(valueName) as string ?? "";
            string?  rawPath     = GetClsidDllPath(clsid);
            string   dllPath     = "";
            bool     isGhost     = false;
            string   vendor      = "";
            bool     isMicrosoft = false;
            DateTime? fileCreation  = null;
            DateTime? fileLastWrite = null;
            DateTime? regWrite      = GetClsidKeyWriteTime(clsid);

            if (!string.IsNullOrEmpty(rawPath))
            {
                string expanded = ExpandAndClean(rawPath);
                if (File.Exists(expanded))
                {
                    var fi      = new FileInfo(expanded);
                    fileCreation  = fi.CreationTime;
                    fileLastWrite = fi.LastWriteTime;
                    try
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(expanded);
                        vendor  = fvi.CompanyName?.Trim() ?? "";
                        isMicrosoft = IsMicrosoftVendor(vendor);
                    }
                    catch { }
                }
                else
                {
                    isGhost = true;
                }
                dllPath = expanded;
            }

            result.Add(new ShellExtensionEntry
            {
                Clsid             = clsid,
                Description       = description,
                FilePath          = dllPath,
                VendorName        = vendor,
                IsMicrosoft       = isMicrosoft,
                IsGhost           = isGhost,
                FileCreationTime  = fileCreation,
                FileLastWriteTime = fileLastWrite,
                RegKeyWriteTime   = regWrite,
            });
        }

        return result;
    }

    private static string ExpandAndClean(string raw)
    {
        string s = Environment.ExpandEnvironmentVariables(raw).Trim('"').Trim();
        // Strip trailing ordinal reference: "foo.dll,-123"
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out _))
            s = s[..comma].Trim();
        return s;
    }

    private static string? GetClsidDllPath(string clsid)
    {
        string keyPath = $@"SOFTWARE\Classes\CLSID\{clsid}\InProcServer32";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, false)
                     ?? Registry.CurrentUser.OpenSubKey(keyPath, false);
        return key?.GetValue(null) as string;
    }

    private static DateTime? GetClsidKeyWriteTime(string clsid)
    {
        string keyPath = $@"SOFTWARE\Classes\CLSID\{clsid}\InProcServer32";
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, false)
                     ?? Registry.CurrentUser.OpenSubKey(keyPath, false);
        return key == null ? null : GetKeyLastWriteTime(key);
    }

    private static DateTime? GetKeyLastWriteTime(RegistryKey key)
    {
        try
        {
            uint cls = 0;
            int  rc  = RegQueryInfoKey(key.Handle, null, ref cls,
                nint.Zero, out _, out _, out _, out _, out _, out _, out _,
                out long ft);
            return rc == 0 ? DateTime.FromFileTime(ft) : null;
        }
        catch { return null; }
    }

    public static bool IsMicrosoftVendor(string vendor) =>
        !string.IsNullOrEmpty(vendor) &&
        (vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
         vendor.Contains("Windows", StringComparison.OrdinalIgnoreCase));
}
