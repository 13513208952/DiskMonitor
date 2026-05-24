using DiskMonitor.Core.Aggregation;
using DiskMonitor.Core.Processes;
using DiskMonitor.Core.Volumes;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace DiskMonitor.Core.Etw;

public sealed class EtwSessionManager : IDisposable
{
    private const string SessionName = "DiskMonitor-FileIO";

    private readonly IoAggregator    _aggregator;
    private readonly ProcessTracker  _processTracker;
    private readonly VolumeMapper    _volumeMapper;

    private TraceEventSession? _session;
    private Thread?            _processingThread;
    private bool               _disposed;

    // FileObject（内核指针） → 文件路径前缀（确定所在卷）
    // 仅保留路径前缀（例如 "C:\"），不存储完整路径
    private readonly FileObjectCache _fileObjectCache = new();

    public EtwSessionManager(IoAggregator aggregator, ProcessTracker processTracker, VolumeMapper volumeMapper)
    {
        _aggregator     = aggregator;
        _processTracker = processTracker;
        _volumeMapper   = volumeMapper;
    }

    public void Start()
    {
        // 清理可能残留的同名会话
        TraceEventSession.GetActiveSession(SessionName)?.Dispose();

        _session = new TraceEventSession(SessionName)
        {
            BufferSizeMB = 64,
            CpuSampleIntervalMSec = 0,
        };

        _session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.FileIOInit |
            KernelTraceEventParser.Keywords.FileIO     |
            KernelTraceEventParser.Keywords.Process,
            KernelTraceEventParser.Keywords.None);

        var kernel = _session.Source.Kernel;

        // ── 进程生命周期 ──────────────────────────────────────
        kernel.ProcessStart += data =>
        {
            _processTracker.OnProcessStarted(
                data.ProcessID,
                data.ProcessName,
                data.ImageFileName,
                data.TimeStamp.ToUniversalTime());
        };

        kernel.ProcessStop += data =>
            _processTracker.OnProcessExited(data.ProcessID);

        // ── FileIO：文件打开 → 建立 FileObject 映射 ──────────
        kernel.FileIOCreate += data =>
        {
            if (data.FileName.Length < 3) return;
            // 只存前缀（例如 "C:\"），不存完整路径
            var prefix = ExtractVolumePrefix(data.FileName);
            if (prefix != null)
                _fileObjectCache.Set(data.FileObject, prefix);
        };

        // ── FileIO：读取 ──────────────────────────────────────
        kernel.FileIORead += data =>
            HandleIo(data.ProcessID, data.FileObject, (long)data.IoSize, isRead: true);

        // ── FileIO：写入 ──────────────────────────────────────
        kernel.FileIOWrite += data =>
            HandleIo(data.ProcessID, data.FileObject, (long)data.IoSize, isRead: false);

        // ── FileIO：文件关闭 → 清理映射 ──────────────────────
        kernel.FileIOClose += data =>
            _fileObjectCache.Remove(data.FileObject);

        kernel.FileIOCleanup += data =>
            _fileObjectCache.Remove(data.FileObject);

        // 在独立线程中阻塞处理事件
        _processingThread = new Thread(() => _session.Source.Process())
        {
            IsBackground = true,
            Name         = "DiskMonitor-ETW",
            Priority     = ThreadPriority.AboveNormal,
        };
        _processingThread.Start();
    }

    public void Stop()
    {
        _session?.Stop();
        _processingThread?.Join(TimeSpan.FromSeconds(5));
    }

    private void HandleIo(int pid, ulong fileObject, long bytes, bool isRead)
    {
        if (bytes <= 0) return;

        var prefix  = _fileObjectCache.Get(fileObject);
        var volume  = prefix != null ? _volumeMapper.ResolveByPrefix(prefix) : null;
        var process = _processTracker.Resolve(pid);

        _aggregator.Add(process, volume, bytes, isRead);
    }

    private static string? ExtractVolumePrefix(string filePath)
    {
        if (filePath.Length < 3) return null;

        // Win32 路径："C:\Users\..."
        if (filePath[1] == ':')
            return filePath.Substring(0, 3); // "C:\"

        // NT 设备路径："\Device\HarddiskVolume3\Users\..."（ETW FileIO 事件的实际格式）
        if (filePath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            int slash = filePath.IndexOf('\\', 8); // 跳过 "\Device\"，找设备名后的反斜杠
            if (slash > 8)
                return filePath.Substring(0, slash + 1); // "\Device\HarddiskVolume3\"
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _session?.Dispose();
        _session = null;
    }
}
