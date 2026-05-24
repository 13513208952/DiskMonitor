using DiskMonitor.Core.Models;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskMonitor.Core.Volumes;

public sealed class VolumeMapper : IDisposable
{
    // 卷GUID → VolumeInfo，运行时动态更新
    private readonly Dictionary<string, VolumeInfo> _byGuid      = new(StringComparer.OrdinalIgnoreCase);
    // Win32 路径前缀 → VolumeInfo，例如 "C:\" → ...
    private readonly Dictionary<string, VolumeInfo> _byPrefix    = new(StringComparer.OrdinalIgnoreCase);
    // NT 设备路径 → VolumeInfo，例如 "\Device\HarddiskVolume3" → ...（ETW 路径主要格式）
    private readonly Dictionary<string, VolumeInfo> _byDevicePath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public event Action<VolumeInfo>? VolumeArrived;
    public event Action<string>?     VolumeRemoved;   // 传入 volumeGuid

    // 启动时调用：枚举所有卷，检测 RAID，建立映射
    public void Initialize()
    {
        var volumes = EnumerateVolumes();  // 检测到 RAID 直接 throw RaidDetectedException

        lock (_lock)
        {
            foreach (var v in volumes)
                AddToMaps(v);
        }
    }

    // 由 ETW/EtwSessionManager 传入文件路径前缀：
    //   Win32 前缀：   "C:\"
    //   NT 设备前缀：  "\Device\HarddiskVolume3\"
    public VolumeInfo? ResolveByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return null;

        lock (_lock)
        {
            // NT 设备路径：去尾部反斜杠后精确匹配（最常见路径）
            var deviceKey = prefix.TrimEnd('\\');
            if (_byDevicePath.TryGetValue(deviceKey, out var v1)) return v1;

            // Win32 路径：精确匹配 "C:\"
            if (_byPrefix.TryGetValue(prefix, out var v2)) return v2;
        }
        return null;
    }

    public IReadOnlyCollection<VolumeInfo> AllVolumes()
    {
        lock (_lock) return _byGuid.Values.ToList();
    }

    // 热插拔：外部（WMI/设备通知）调用此方法
    public void OnVolumeArrived(string volumeGuidPath)
    {
        var info = BuildVolumeInfo(volumeGuidPath, out _);
        if (info == null) return;

        lock (_lock) AddToMaps(info);
        VolumeArrived?.Invoke(info);
    }

    public void OnVolumeRemoved(string volumeGuid)
    {
        lock (_lock)
        {
            if (_byGuid.TryGetValue(volumeGuid, out var info))
            {
                _byGuid.Remove(volumeGuid);
                if (!string.IsNullOrEmpty(info.DriveLetter))
                    _byPrefix.Remove(info.PathPrefix);
                if (!string.IsNullOrEmpty(info.DevicePath))
                    _byDevicePath.Remove(info.DevicePath);
            }
        }
        VolumeRemoved?.Invoke(volumeGuid);
    }

    // ── 内部辅助：向三张表统一添加 ────────────────────────────
    private void AddToMaps(VolumeInfo v)
    {
        _byGuid[v.VolumeGuid] = v;
        if (!string.IsNullOrEmpty(v.DriveLetter))
            _byPrefix[v.PathPrefix] = v;
        if (!string.IsNullOrEmpty(v.DevicePath))
            _byDevicePath[v.DevicePath] = v;
    }

    // ── 私有实现 ──────────────────────────────────────────────

    private List<VolumeInfo> EnumerateVolumes()
    {
        var result      = new List<VolumeInfo>();
        var raidVolumes = new List<string>();

        var buf = new StringBuilder(260);
        var handle = NativeMethods.FindFirstVolume(buf, buf.Capacity);
        if (handle == NativeMethods.INVALID_HANDLE_VALUE) return result;

        try
        {
            do
            {
                var guidPath = buf.ToString();
                var info = BuildVolumeInfo(guidPath, out bool isRaid);
                if (isRaid)
                    raidVolumes.Add(guidPath);
                else if (info != null)
                    result.Add(info);
            }
            while (NativeMethods.FindNextVolume(handle, buf, buf.Capacity));
        }
        finally
        {
            NativeMethods.FindVolumeClose(handle);
        }

        if (raidVolumes.Count > 0)
        {
            // 将 GUID 路径转换为可读标识
            var readable = raidVolumes
                .Select(g => g.TrimEnd('\\'))
                .ToList();
            throw new RaidDetectedException(readable);
        }

        return result;
    }

    private static VolumeInfo? BuildVolumeInfo(string volumeGuidPath, out bool isRaid)
    {
        isRaid = false;

        // 去掉尾部反斜杠用于设备调用
        var volumeNoSlash = volumeGuidPath.TrimEnd('\\');

        // 获取盘符
        var letterBuf = new StringBuilder(512);
        uint returnLen = 0;
        if (!NativeMethods.GetVolumePathNamesForVolumeName(volumeGuidPath, letterBuf, (uint)letterBuf.Capacity, ref returnLen))
            return null;

        // GetVolumePathNamesForVolumeName 返回以双空字符结尾的多字符串
        var letters = ParseMultiString(letterBuf.ToString());
        // 只取第一个盘符（格式 "C:\"）
        var driveLetter = letters.FirstOrDefault(l => l.Length >= 2 && l[1] == ':')
                                 ?.TrimEnd('\\') ?? "";

        // 卷标
        var labelBuf = new StringBuilder(261);
        NativeMethods.GetVolumeInformation(volumeGuidPath, labelBuf, labelBuf.Capacity,
            out _, out _, out _, null, 0);
        var label = labelBuf.ToString();

        // 提取 GUID 部分 \\?\Volume{xxxx}\  →  {xxxx}
        var guid = ExtractGuid(volumeGuidPath);
        if (guid == null) return null;

        // 物理磁盘信息（IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS）
        using var hVolume = NativeMethods.CreateFile(
            volumeNoSlash,
            0,                                    // 仅查询，不需要读写权限
            FileShare.ReadWrite | FileShare.Delete,
            nint.Zero,
            FileMode.Open,
            FileAttributes.Normal,
            nint.Zero);

        if (hVolume.IsInvalid) return null;

        var extents = GetDiskExtents(hVolume, out isRaid);
        if (isRaid) return null;   // 调用方处理

        int  diskNumber = extents.Length > 0 ? extents[0].DiskNumber : -1;
        string diskModel = diskNumber >= 0 ? GetDiskModel(diskNumber) : "";

        // 判断是否可移动
        var driveType = string.IsNullOrEmpty(driveLetter)
            ? NativeMethods.DriveType.Unknown
            : NativeMethods.GetDriveType(driveLetter + @"\");
        bool isRemovable = driveType is NativeMethods.DriveType.Removable or NativeMethods.DriveType.CdRom;

        // 查询 NT 设备路径供 ETW 路径反查
        // 有盘符用 QueryDosDevice；无盘符（EFI/Recovery 等隐藏分区）用 IOCTL_MOUNTDEV_QUERY_DEVICE_NAME
        var devicePath = !string.IsNullOrEmpty(driveLetter)
            ? QueryDevicePath(driveLetter)
            : QueryMountdevName(hVolume);

        return new VolumeInfo
        {
            VolumeGuid  = guid,
            DriveLetter = driveLetter,
            VolumeLabel = label,
            DiskNumber  = diskNumber,
            DiskModel   = diskModel,
            IsRemovable = isRemovable,
            DevicePath  = devicePath,
        };
    }

    private static NativeMethods.DiskExtent[] GetDiskExtents(SafeFileHandle hVolume, out bool isRaid)
    {
        isRaid = false;
        // 先用小缓冲区，失败时扩大
        int bufSize = Marshal.SizeOf<NativeMethods.VolumeDiskExtents>();
        var buf = new byte[bufSize];

        bool ok = NativeMethods.DeviceIoControl(
            hVolume,
            NativeMethods.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
            nint.Zero, 0,
            buf, (uint)buf.Length,
            out uint bytesReturned,
            nint.Zero);

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 234 /*ERROR_MORE_DATA*/)
            {
                // 多个 extent，说明是 RAID/跨盘卷
                isRaid = true;
                return [];
            }
            return [];
        }

        int count = BitConverter.ToInt32(buf, 0);
        if (count > 1) { isRaid = true; return []; }
        if (count == 0) return [];

        var extents = new NativeMethods.DiskExtent[count];
        int offset  = 8; // NumberOfDiskExtents(4) + padding(4)
        for (int i = 0; i < count; i++)
        {
            extents[i] = new NativeMethods.DiskExtent
            {
                DiskNumber      = BitConverter.ToInt32(buf,  offset),
                StartingOffset  = BitConverter.ToInt64(buf,  offset + 8),
                ExtentLength    = BitConverter.ToInt64(buf,  offset + 16),
            };
            offset += 24;
        }
        return extents;
    }

    private static string GetDiskModel(int diskNumber)
    {
        try
        {
            var path = $@"\\.\PhysicalDrive{diskNumber}";
            using var h = NativeMethods.CreateFile(path, 0,
                FileShare.ReadWrite | FileShare.Delete,
                nint.Zero, FileMode.Open, FileAttributes.Normal, nint.Zero);

            if (h.IsInvalid) return "";

            // IOCTL_STORAGE_QUERY_PROPERTY → StorageDeviceProperty
            var query = new NativeMethods.StoragePropertyQuery
            {
                PropertyId = 0,  // StorageDeviceProperty
                QueryType  = 0,  // PropertyStandardQuery
            };
            int qSize = Marshal.SizeOf<NativeMethods.StoragePropertyQuery>();
            var qBuf  = new byte[qSize];
            MemoryMarshal.Write(qBuf, query);

            var outBuf = new byte[512];
            NativeMethods.DeviceIoControl(h,
                NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                qBuf, (uint)qBuf.Length,
                outBuf, (uint)outBuf.Length,
                out _, nint.Zero);

            // StorageDeviceDescriptor 结构中 ProductIdOffset 在偏移 16
            int productIdOffset = BitConverter.ToInt32(outBuf, 16);
            if (productIdOffset <= 0 || productIdOffset >= outBuf.Length) return "";

            int end = Array.IndexOf(outBuf, (byte)0, productIdOffset);
            return Encoding.ASCII.GetString(outBuf, productIdOffset,
                (end < 0 ? outBuf.Length : end) - productIdOffset).Trim();
        }
        catch { return ""; }
    }

    // 通过 IOCTL_MOUNTDEV_QUERY_DEVICE_NAME 查询卷的 NT 设备路径（适用于无盘符卷）
    // 返回如 "\Device\HarddiskVolume1"
    private static string QueryMountdevName(SafeFileHandle hVolume)
    {
        var buf = new byte[512];
        if (!NativeMethods.DeviceIoControl(hVolume,
                NativeMethods.IOCTL_MOUNTDEV_QUERY_DEVICE_NAME,
                nint.Zero, 0, buf, (uint)buf.Length,
                out _, nint.Zero))
            return "";

        // MOUNTDEV_NAME 结构：ushort NameLength + wchar Name[]
        int nameLen = BitConverter.ToUInt16(buf, 0);
        if (nameLen <= 0 || nameLen + 2 > buf.Length) return "";
        return Encoding.Unicode.GetString(buf, 2, nameLen);
    }

    private static string QueryDevicePath(string driveLetter)
    {
        if (string.IsNullOrEmpty(driveLetter)) return "";
        var sb = new StringBuilder(512);
        // lpDeviceName 不含反斜杠，例如 "C:"
        if (NativeMethods.QueryDosDevice(driveLetter, sb, (uint)sb.Capacity) == 0)
            return "";
        // QueryDosDevice 返回多字符串，取第一条（通常唯一）
        var raw = sb.ToString();
        int nullIdx = raw.IndexOf('\0');
        return nullIdx > 0 ? raw.Substring(0, nullIdx) : raw;
    }

    private static string? ExtractGuid(string volumeGuidPath)
    {
        // \\?\Volume{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}\
        var start = volumeGuidPath.IndexOf('{');
        var end   = volumeGuidPath.IndexOf('}');
        if (start < 0 || end < 0 || end <= start) return null;
        return volumeGuidPath.Substring(start, end - start + 1);
    }

    private static List<string> ParseMultiString(string s)
    {
        var result = new List<string>();
        int start  = 0;
        while (start < s.Length)
        {
            int end = s.IndexOf('\0', start);
            if (end < 0) end = s.Length;
            if (end > start) result.Add(s.Substring(start, end - start));
            else if (end == start) break;
            start = end + 1;
        }
        return result;
    }

    // WMI 卷到达事件入口：传入盘符（如 "F:\"），内部转换为 GUID 路径再处理
    public void OnVolumeArrivedByLetter(string driveName)
    {
        if (!driveName.EndsWith('\\')) driveName += '\\';
        var buf = new StringBuilder(50);
        if (NativeMethods.GetVolumeNameForVolumeMountPoint(driveName, buf, (uint)buf.Capacity))
            OnVolumeArrived(buf.ToString());
    }

    // WMI 卷移除事件入口：传入盘符（如 "F:\"），内部查出 GUID 再清理
    public void OnVolumeRemovedByLetter(string driveName)
    {
        if (!driveName.EndsWith('\\')) driveName += '\\';
        var vol = ResolveByPrefix(driveName);
        if (vol != null) OnVolumeRemoved(vol.VolumeGuid);
    }

    public void Dispose() { /* 无托管资源 */ }
}
