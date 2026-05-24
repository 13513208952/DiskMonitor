using DiskMonitor.Core;
using DiskMonitor.Core.Models;

namespace DiskMonitor.Service;

public sealed class DiskMonitorWorker : BackgroundService
{
    private readonly ILogger<DiskMonitorWorker> _logger;
    private DiskMonitorEngine? _engine;

    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DiskMonitor", "diskmonitor.db");

    public DiskMonitorWorker(ILogger<DiskMonitorWorker> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ETW 事件循环必须在独立线程中阻塞运行，不能用 async/await
        var thread = new Thread(() => RunEngine(stoppingToken))
        {
            IsBackground = false,
            Name         = "DiskMonitor-Engine",
        };
        thread.Start();
        return Task.CompletedTask;
    }

    private void RunEngine(CancellationToken stoppingToken)
    {
        try
        {
            _engine = new DiskMonitorEngine(DbPath);
            _engine.Start();
            _logger.LogInformation("DiskMonitor 服务已启动，数据库：{DbPath}", DbPath);

            stoppingToken.WaitHandle.WaitOne();
        }
        catch (RaidDetectedException ex)
        {
            _logger.LogCritical("检测到不支持的存储配置（RAID/跨盘卷），服务停止。受影响的卷：{Volumes}",
                string.Join(", ", ex.AffectedVolumes));
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "DiskMonitor 服务发生致命错误");
            throw;   // 让 SCM 触发崩溃恢复
        }
        finally
        {
            _logger.LogInformation("DiskMonitor 服务正在停止...");
            _engine?.Dispose();
            _engine = null;
        }
    }

    public override void Dispose()
    {
        _engine?.Dispose();
        base.Dispose();
    }
}
