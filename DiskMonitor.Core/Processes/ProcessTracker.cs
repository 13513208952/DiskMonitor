using DiskMonitor.Core.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskMonitor.Core.Processes;

public sealed class ProcessTracker : IDisposable
{
    private readonly ConcurrentDictionary<int, ProcessInfo> _map = new();
    private bool _disposed;

    // 服务启动时调用：扫描当前所有运行进程
    public void TakeInitialSnapshot()
    {
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var path = QueryFullPath(proc.Id);
                var info = new ProcessInfo
                {
                    Pid       = proc.Id,
                    Name      = proc.ProcessName + ".exe",
                    Path      = path ?? "",
                    StartTime = TryGetStartTime(proc),
                };
                _map[proc.Id] = info;
            }
            catch { /* 无权限的系统进程跳过 */ }
            finally { proc.Dispose(); }
        }

        // 确保 System 进程存在
        _map[4] = ProcessInfo.System;
    }

    // 由 ETW 进程创建事件调用
    public void OnProcessStarted(int pid, string name, string imagePath, DateTime startTime)
    {
        _map[pid] = new ProcessInfo
        {
            Pid       = pid,
            Name      = name,
            Path      = imagePath,
            StartTime = startTime,
        };
    }

    // 由 ETW 进程退出事件调用
    public void OnProcessExited(int pid)
    {
        _map.TryRemove(pid, out _);
    }

    public ProcessInfo Resolve(int pid)
    {
        if (_map.TryGetValue(pid, out var info)) return info;

        // 未命中：尝试实时查询（短暂进程可能还活着）
        var path = QueryFullPath(pid);
        if (path != null)
        {
            var name = Path.GetFileName(path);
            var fresh = new ProcessInfo
            {
                Pid       = pid,
                Name      = name,
                Path      = path,
                StartTime = DateTime.UtcNow,
            };
            _map.TryAdd(pid, fresh);
            return fresh;
        }

        return ProcessInfo.Unknown;
    }

    private static string? QueryFullPath(int pid)
    {
        nint hProc = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == nint.Zero) return null;

        try
        {
            var sb   = new StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(hProc, 0, sb, ref len)
                ? sb.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(hProc);
        }
    }

    private static DateTime TryGetStartTime(Process proc)
    {
        try { return proc.StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _map.Clear();
    }

    private static class NativeMethods
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint OpenProcess(uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(
            nint hProcess, uint dwFlags,
            StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);
    }
}
