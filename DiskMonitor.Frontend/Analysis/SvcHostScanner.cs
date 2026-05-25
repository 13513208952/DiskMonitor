using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace DiskMonitor.Frontend.Analysis;

public sealed class SvcHostServiceEntry
{
    public int    Pid         { get; init; }
    public string ServiceName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ServiceDll  { get; init; } = "";
    public string VendorName  { get; init; } = "";
    public bool   IsMicrosoft { get; init; }

    public string PidDisplay => Pid > 0 ? Pid.ToString() : "—";
    public string DllDisplay => string.IsNullOrEmpty(ServiceDll) ? "（未找到）" : ServiceDll;
}

public static class SvcHostScanner
{
    public static List<SvcHostServiceEntry> Scan()
    {
        var pidMap = BuildPidMap();
        var result = new List<SvcHostServiceEntry>();

        using var servicesKey = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services", writable: false);
        if (servicesKey == null) return result;

        foreach (var name in servicesKey.GetSubKeyNames())
        {
            try
            {
                using var svcKey = servicesKey.OpenSubKey(name, writable: false);
                if (svcKey == null) continue;

                var imagePath = svcKey.GetValue("ImagePath") as string ?? "";
                var expanded  = Environment.ExpandEnvironmentVariables(imagePath);
                if (!expanded.Contains("svchost.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Use ServiceController for properly resolved display name (handles MUI resources)
                string displayName;
                try
                {
                    using var sc = new ServiceController(name);
                    displayName = sc.DisplayName;
                }
                catch
                {
                    var raw = svcKey.GetValue("DisplayName") as string ?? name;
                    displayName = raw.StartsWith('@') ? name : raw;
                }

                // ServiceDll path from Parameters subkey
                string serviceDll = "";
                try
                {
                    using var paramsKey = svcKey.OpenSubKey("Parameters", writable: false);
                    if (paramsKey?.GetValue("ServiceDll") is string raw)
                        serviceDll = Environment.ExpandEnvironmentVariables(raw.Trim('"'));
                }
                catch { }

                pidMap.TryGetValue(name, out int pid);

                string vendorName = "";
                bool   isMicrosoft = false;
                if (!string.IsNullOrEmpty(serviceDll) && File.Exists(serviceDll))
                {
                    try
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(serviceDll);
                        vendorName  = fvi.CompanyName?.Trim() ?? "";
                        isMicrosoft = IsMicrosoftVendor(vendorName);
                    }
                    catch { }
                }

                result.Add(new SvcHostServiceEntry
                {
                    Pid         = pid,
                    ServiceName = name,
                    DisplayName = displayName,
                    ServiceDll  = serviceDll,
                    VendorName  = vendorName,
                    IsMicrosoft = isMicrosoft,
                });
            }
            catch { }
        }

        return [.. result.OrderBy(e => e.ServiceName, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsMicrosoftVendor(string vendor) =>
        !string.IsNullOrEmpty(vendor) &&
        vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, int> BuildPidMap()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        nint hScm = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (hScm == nint.Zero) return map;

        try
        {
            uint bytesNeeded = 0, returned = 0, resumeHandle = 0;
            EnumServicesStatusEx(hScm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                nint.Zero, 0, out bytesNeeded, out returned, ref resumeHandle, null);

            if (bytesNeeded == 0) return map;

            nint buf = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                resumeHandle = 0;
                if (EnumServicesStatusEx(hScm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                    buf, bytesNeeded, out _, out returned, ref resumeHandle, null))
                {
                    int sz = Marshal.SizeOf<EnumServiceStatusProcess>();
                    for (uint i = 0; i < returned; i++)
                    {
                        var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(buf + (int)(i * sz));
                        if (entry.ServiceName != null && entry.StatusProcess.ProcessId > 0)
                            map[entry.ServiceName] = (int)entry.StatusProcess.ProcessId;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseServiceHandle(hScm); }

        return map;
    }

    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SC_ENUM_PROCESS_INFO         = 0;
    private const uint SERVICE_WIN32                = 0x00000030;
    private const uint SERVICE_STATE_ALL            = 0x00000003;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(
        string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusEx(
        nint hSCManager, uint infoLevel, uint dwServiceType, uint dwServiceState,
        nint lpServices, uint cbBufSize,
        out uint pcbBytesNeeded, out uint lpServicesReturned,
        ref uint lpResumeHandle, string? pszGroupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(nint hSCObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumServiceStatusProcess
    {
        public string ServiceName;
        public string DisplayName;
        public ServiceStatusProcess StatusProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }
}
