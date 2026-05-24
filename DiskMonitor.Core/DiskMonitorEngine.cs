using DiskMonitor.Core.Aggregation;
using DiskMonitor.Core.Database;
using DiskMonitor.Core.Etw;
using DiskMonitor.Core.Models;
using DiskMonitor.Core.Processes;
using DiskMonitor.Core.Volumes;
using System.Management;

namespace DiskMonitor.Core;

public sealed class DiskMonitorEngine : IDisposable
{
    private readonly DatabaseManager  _db;
    private readonly IoRepository     _repo;
    private readonly VolumeMapper     _volumeMapper;
    private readonly ProcessTracker   _processTracker;
    private readonly IoAggregator     _aggregator;
    private readonly EtwSessionManager _etw;

    private readonly Timer _heartbeatTimer;
    private ManagementEventWatcher? _volumeWatcher;
    private bool _disposed;

    public DiskMonitorEngine(string dbPath)
    {
        _db             = new DatabaseManager(dbPath);
        _repo           = new IoRepository(_db);
        _volumeMapper   = new VolumeMapper();
        _processTracker = new ProcessTracker();
        _aggregator     = new IoAggregator(_repo);
        _etw            = new EtwSessionManager(_aggregator, _processTracker, _volumeMapper);

        // 心跳定时器：每分钟写一次时间戳
        _heartbeatTimer = new Timer(_ => _repo.UpdateHeartbeat(),
            null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    // 启动监控，若检测到 RAID 则抛出 RaidDetectedException
    public void Start()
    {
        // 1. 卷映射（含 RAID 检测）
        _volumeMapper.Initialize();

        // 2. 持久化当前所有卷信息
        foreach (var vol in _volumeMapper.AllVolumes())
            _repo.UpsertVolumeSnapshot(vol);

        // 3. 卷热插拔处理
        _volumeMapper.VolumeArrived += vol =>
        {
            _repo.UpsertVolumeSnapshot(vol);
        };
        _volumeMapper.VolumeRemoved += guid =>
        {
            _aggregator.FlushVolume(guid);
        };

        // 4. 进程初始快照
        _processTracker.TakeInitialSnapshot();

        // 5. 启动 ETW 会话
        _etw.Start();

        // 6. WMI 热插拔监听（失败不影响主功能）
        StartVolumeWatcher();
    }

    private void StartVolumeWatcher()
    {
        try
        {
            _volumeWatcher = new ManagementEventWatcher(
                new WqlEventQuery(
                    "SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3"));
            _volumeWatcher.EventArrived += OnVolumeChangeEvent;
            _volumeWatcher.Start();
        }
        catch
        {
            // WMI 不可用时静默跳过，热插拔检测失效但不影响核心监控
            _volumeWatcher = null;
        }
    }

    private void OnVolumeChangeEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            int    eventType  = Convert.ToInt32(e.NewEvent["EventType"]);
            string driveName  = (string)e.NewEvent["DriveName"];   // "F:\"

            if (eventType == 2)                                     // DeviceArrival
                _volumeMapper.OnVolumeArrivedByLetter(driveName);
            else if (eventType == 3)                                // DeviceRemoval
                _volumeMapper.OnVolumeRemovedByLetter(driveName);
        }
        catch { /* 单次事件异常不中断服务 */ }
    }

    public void Stop()
    {
        _volumeWatcher?.Stop();
        _etw.Stop();
        _aggregator.FlushAll();
    }

    // 前端查询：指定日期范围
    public List<Models.IoRecord> QueryRecords(DateOnly from, DateOnly to)
        => _repo.QueryByDateRange(
            from.ToString("yyyy-MM-dd"),
            to.ToString("yyyy-MM-dd"));

    // 前端查询：服务最后心跳时间（判断服务是否存活）
    public DateTime? GetLastHeartbeat() => _repo.GetLastHeartbeat();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heartbeatTimer.Dispose();
        Stop();
        _volumeWatcher?.Dispose();
        _etw.Dispose();
        _aggregator.Dispose();
        _processTracker.Dispose();
        _volumeMapper.Dispose();
        _db.Dispose();
    }
}
