using DiskMonitor.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(cfg =>
    cfg.ServiceName = "DiskMonitor");

builder.Services.AddHostedService<DiskMonitorWorker>();

builder.Logging.AddEventLog(cfg =>
{
    cfg.SourceName = "DiskMonitor";
    cfg.LogName    = "Application";
});

var host = builder.Build();
host.Run();
