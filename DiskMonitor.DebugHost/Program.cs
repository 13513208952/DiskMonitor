using DiskMonitor.Core;
using DiskMonitor.Core.Models;

// 必须以管理员权限运行（ETW 内核会话需要）
Console.Title = "DiskMonitor DebugHost";
Console.OutputEncoding = System.Text.Encoding.UTF8;

var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "DiskMonitor", "debug.db");

Console.WriteLine($"[DebugHost] 数据库路径：{dbPath}");
Console.WriteLine("[DebugHost] 正在初始化...");

try
{
    using var engine = new DiskMonitorEngine(dbPath);
    engine.Start();
    Console.WriteLine("[DebugHost] 监控已启动。按 Q 停止，按 S 立即查看今日统计。");

    var cts = new CancellationTokenSource();

    // 每 30 秒自动打印一次统计
    _ = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cts.Token).ContinueWith(_ => { });
            if (!cts.Token.IsCancellationRequested)
                PrintStats(engine);
        }
    });

    if (Console.IsInputRedirected)
    {
        // 非交互模式（管道/自动化测试）：等待数据积累后打印统计再退出
        Thread.Sleep(TimeSpan.FromSeconds(8));
        PrintStats(engine);
    }
    else
    {
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Q) break;
            if (key.Key == ConsoleKey.S) PrintStats(engine);
        }
    }

    cts.Cancel();
    Console.WriteLine("[DebugHost] 正在停止并落库...");
    engine.Stop();
    Console.WriteLine("[DebugHost] 已停止。");
    PrintStats(engine);   // 落库后查询，数据完整
}
catch (RaidDetectedException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[错误] 检测到不支持的存储配置：{ex.Message}");
    Console.ResetColor();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[错误] {ex}");
    Console.ResetColor();
}

Console.WriteLine("按任意键退出...");
if (!Console.IsInputRedirected) Console.ReadKey();

static void PrintStats(DiskMonitorEngine engine)
{
    var today   = DateOnly.FromDateTime(DateTime.UtcNow);
    var records = engine.QueryRecords(today, today);

    Console.WriteLine();
    Console.WriteLine($"── 今日统计（{today:yyyy-MM-dd}）共 {records.Count} 条 ──");

    const long MB = 1024 * 1024;
    var top = records
        .OrderByDescending(r => r.ReadBytes + r.WriteBytes)
        .Take(20);

    Console.WriteLine($"{"进程",-30} {"盘符",-5} {"读取 MB",10} {"写入 MB",10}");
    Console.WriteLine(new string('-', 60));
    foreach (var r in top)
    {
        var name = r.ProcessName.Length > 28
            ? r.ProcessName[..28] + ".."
            : r.ProcessName;
        Console.WriteLine($"{name,-30} {r.DriveLetter,-5} {r.ReadBytes / MB,10:N0} {r.WriteBytes / MB,10:N0}");
    }
    Console.WriteLine();
}
