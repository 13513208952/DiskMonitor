using DiskMonitor.Core.Database;
using DiskMonitor.Core.Models;
using DiskMonitor.Core.Processes;
using DiskMonitor.Core.Volumes;

namespace DiskMonitor.Core.Aggregation;

public sealed class IoAggregator : IDisposable
{
    private readonly IoRepository _repo;

    // 聚合键：进程名 + 进程路径 + 卷GUID
    private readonly record struct AggKey(string ProcessName, string ProcessPath, string VolumeGuid);

    // 聚合值：可变读写计数
    private sealed class AggValue
    {
        public long ReadBytes;
        public long WriteBytes;
    }

    private Dictionary<AggKey, AggValue> _current = new();
    private readonly Lock _lock = new();

    // 当前日期（UTC），午夜滚动时使用
    private string _currentDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

    // 卷信息缓存（聚合键只存 GUID，落库时需要完整卷信息）
    private readonly Dictionary<string, VolumeInfo> _volumeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _volumeLock = new();

    private readonly Timer _flushTimer;
    private bool _disposed;

    public IoAggregator(IoRepository repo)
    {
        _repo = repo;
        // 每 5 分钟批量落库
        _flushTimer = new Timer(_ => FlushSafe(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public void Add(ProcessInfo process, VolumeInfo? volume, long bytes, bool isRead)
    {
        var volumeGuid = volume?.VolumeGuid ?? "[NoVolume]";
        var key = new AggKey(process.Name, process.Path, volumeGuid);

        // 缓存卷信息供落库时使用
        if (volume != null)
        {
            lock (_volumeLock) _volumeCache[volumeGuid] = volume;
        }

        lock (_lock)
        {
            // 午夜检测
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (today != _currentDate) RolloverUnlocked(today);

            if (!_current.TryGetValue(key, out var val))
            {
                val = new AggValue();
                _current[key] = val;
            }

            if (isRead) val.ReadBytes  += bytes;
            else         val.WriteBytes += bytes;
        }
    }

    // 卷卸载时立即强制落库该卷数据
    public void FlushVolume(string volumeGuid)
    {
        Dictionary<AggKey, AggValue> snapshot;
        lock (_lock)
        {
            snapshot = _current
                .Where(kv => kv.Key.VolumeGuid == volumeGuid)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var k in snapshot.Keys) _current.Remove(k);
        }
        PersistSnapshot(snapshot, _currentDate);
    }

    public void FlushAll() => FlushSafe();

    private void FlushSafe()
    {
        try { Flush(); }
        catch { /* 落库失败不中断服务，下次重试 */ }
    }

    private void Flush()
    {
        Dictionary<AggKey, AggValue> snapshot;
        string date;

        lock (_lock)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (today != _currentDate) RolloverUnlocked(today);

            if (_current.Count == 0) return;

            snapshot = _current;
            _current = new Dictionary<AggKey, AggValue>();
            date     = _currentDate;
        }

        PersistSnapshot(snapshot, date);
    }

    private void PersistSnapshot(Dictionary<AggKey, AggValue> snapshot, string date)
    {
        if (snapshot.Count == 0) return;

        var records = new List<IoRecord>(snapshot.Count);
        foreach (var (key, val) in snapshot)
        {
            _volumeCache.TryGetValue(key.VolumeGuid, out var vol);
            records.Add(new IoRecord
            {
                Date        = date,
                ProcessName = key.ProcessName,
                ProcessPath = key.ProcessPath,
                DriveLetter = vol?.DriveLetter ?? "",
                VolumeLabel = vol?.VolumeLabel ?? "",
                VolumeGuid  = key.VolumeGuid,
                DiskNumber  = vol?.DiskNumber  ?? -1,
                DiskModel   = vol?.DiskModel   ?? "",
                ReadBytes   = val.ReadBytes,
                WriteBytes  = val.WriteBytes,
            });
        }

        _repo.UpsertIoBatch(records);
    }

    // 午夜滚动：落库当日数据，重置计数器（调用时已持有 _lock）
    private void RolloverUnlocked(string newDate)
    {
        var old     = _current;
        var oldDate = _currentDate;   // 必须在覆写前捕获，Task.Run 是异步的
        _current     = new Dictionary<AggKey, AggValue>();
        _currentDate = newDate;

        // 在锁外持久化，避免长时间持锁
        Task.Run(() => PersistSnapshot(old, oldDate));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
        FlushSafe();
    }
}
