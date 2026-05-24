using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskMonitor.Core.Volumes;

internal static class NativeMethods
{
    public static readonly nint INVALID_HANDLE_VALUE = new(-1);

    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS  = 0x00560000;
    public const uint IOCTL_STORAGE_QUERY_PROPERTY          = 0x002D1400;
    // CTL_CODE(0x4D, 2, METHOD_BUFFERED, FILE_ANY_ACCESS) — 返回 \Device\HarddiskVolumeN
    public const uint IOCTL_MOUNTDEV_QUERY_DEVICE_NAME      = 0x004D0008;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint FindFirstVolume(StringBuilder lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextVolume(nint hFindVolume, StringBuilder lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindVolumeClose(nint hFindVolume);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathNamesForVolumeName(
        string lpszVolumeName,
        StringBuilder lpszVolumePathNames,
        uint cchBufferLength,
        ref uint lpcchReturnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        nint lpSecurityAttributes,
        FileMode dwCreationDisposition,
        FileAttributes dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        [In] byte[] lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern DriveType GetDriveType(string lpRootPathName);

    // 返回设备路径，例如 "C:" → "\Device\HarddiskVolume3\0"（多字符串）
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint QueryDosDevice(string? lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);

    // 盘符 → 卷 GUID 路径，例如 "F:\" → "\\?\Volume{xxx}\"
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint, StringBuilder lpszVolumeName, uint cchBufferLength);

    public enum DriveType : uint
    {
        Unknown   = 0,
        NoRootDir = 1,
        Removable = 2,
        Fixed     = 3,
        Remote    = 4,
        CdRom     = 5,
        RamDisk   = 6,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DiskExtent
    {
        public int  DiskNumber;
        public int  _padding;
        public long StartingOffset;
        public long ExtentLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VolumeDiskExtents
    {
        public int       NumberOfDiskExtents;
        public int       _padding;
        public DiskExtent FirstExtent;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StoragePropertyQuery
    {
        public int  PropertyId;
        public int  QueryType;
        public byte AdditionalParameters;
    }
}
