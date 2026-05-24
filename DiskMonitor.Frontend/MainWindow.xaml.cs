using DiskMonitor.Core.Database;
using DiskMonitor.Core.Models;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DiskMonitor.Frontend;

public partial class MainWindow : Window
{
    private const string ServiceName = "DiskMonitor";

    private readonly string _dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DiskMonitor", "diskmonitor.db");

    private DatabaseManager? _db;
    private IoRepository?    _repo;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _refreshing;
    private bool _suppressThemeChange;

    private readonly ObservableCollection<IoRecordVm> _todayItems   = [];
    private readonly ObservableCollection<IoRecordVm> _historyItems = [];

    // ── Init ────────────────────────────────────────────────────

    public MainWindow()
    {
        // Apply saved theme before InitializeComponent so DynamicResources resolve correctly
        var savedTheme = App.LoadSavedTheme();
        App.ApplyTheme(savedTheme);

        InitializeComponent();

        GridToday.ItemsSource   = _todayItems;
        GridHistory.ItemsSource = _historyItems;

        TxtDbPath.Text      = _dbPath;
        TxtTodayDate.Text   = $"今日 · {DateTime.Today:yyyy-MM-dd}";
        DpFrom.SelectedDate = DateTime.Today.AddDays(-6);
        DpTo.SelectedDate   = DateTime.Today;

        // Select saved theme in ComboBox without triggering SelectionChanged
        _suppressThemeChange = true;
        foreach (System.Windows.Controls.ComboBoxItem item in CmbTheme.Items)
        {
            if (item.Tag?.ToString() == savedTheme)
            {
                CmbTheme.SelectedItem = item;
                break;
            }
        }
        _suppressThemeChange = false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TryOpenDb();
        _ = RefreshAllAsync();

        _timer.Tick += async (_, _) =>
        {
            try { await RefreshAllAsync(); }
            catch { }
        };
        _timer.Start();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _db?.Dispose();
    }

    // ── Core refresh ────────────────────────────────────────────

    private async Task RefreshAllAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (_repo == null && File.Exists(_dbPath))
                TryOpenDb();

            await Task.Run(() =>
            {
                RefreshServiceStatusBg();
                if (_repo != null)
                {
                    LoadTodayDataBg();
                    RefreshHeartbeatBg();
                }
            });

            RunLastRefresh.Text = DateTime.Now.ToString("HH:mm:ss");
        }
        finally { _refreshing = false; }
    }

    private void TryOpenDb()
    {
        try
        {
            if (!File.Exists(_dbPath)) return;
            _db?.Dispose();
            _db   = new DatabaseManager(_dbPath);
            _repo = new IoRepository(_db);
        }
        catch { _db = null; _repo = null; }
    }

    // ── Service status ──────────────────────────────────────────

    private void RefreshServiceStatusBg()
    {
        ServiceControllerStatus? status = null;
        bool installed = true;

        try
        {
            using var sc = new ServiceController(ServiceName);
            status = sc.Status;
        }
        catch (InvalidOperationException) { installed = false; }
        catch { installed = false; }

        Dispatcher.Invoke(() => UpdateServiceUi(installed ? status : null));
    }

    private void UpdateServiceUi(ServiceControllerStatus? status)
    {
        if (status == null)
        {
            StatusDot.Fill        = (Brush)FindResource("TxtMuted");
            TxtStatus.Text        = "未安装";
            TxtServiceDetail.Text = "服务未安装，请点击 [安装服务]";
            BtnStart.IsEnabled    = false;
            BtnStop.IsEnabled     = false;
            BtnInstall.IsEnabled  = true;
            BtnUninstall.IsEnabled = false;
            return;
        }

        BtnInstall.IsEnabled   = false;
        BtnUninstall.IsEnabled = true;

        switch (status)
        {
            case ServiceControllerStatus.Running:
                StatusDot.Fill     = (Brush)FindResource("Green");
                TxtStatus.Text     = "运行中";
                TxtServiceDetail.Text = "服务正常运行，正在采集 I/O 数据";
                BtnStart.IsEnabled = false;
                BtnStop.IsEnabled  = true;
                break;

            case ServiceControllerStatus.Stopped:
                StatusDot.Fill     = (Brush)FindResource("Red");
                TxtStatus.Text     = "已停止";
                TxtServiceDetail.Text = "服务已停止，点击 [启动服务] 开始采集";
                BtnStart.IsEnabled = true;
                BtnStop.IsEnabled  = false;
                break;

            case ServiceControllerStatus.StartPending:
            case ServiceControllerStatus.StopPending:
                StatusDot.Fill     = (Brush)FindResource("Orange");
                TxtStatus.Text     = status == ServiceControllerStatus.StartPending ? "启动中…" : "停止中…";
                TxtServiceDetail.Text = "操作中，请稍候";
                BtnStart.IsEnabled = false;
                BtnStop.IsEnabled  = false;
                break;

            default:
                StatusDot.Fill     = (Brush)FindResource("TxtMuted");
                TxtStatus.Text     = status.ToString();
                TxtServiceDetail.Text = "";
                BtnStart.IsEnabled = true;
                BtnStop.IsEnabled  = true;
                break;
        }
    }

    private void RefreshHeartbeatBg()
    {
        try
        {
            var hb = _repo?.GetLastHeartbeat();
            Dispatcher.Invoke(() =>
            {
                if (hb == null)
                {
                    TxtHeartbeat.Text = "心跳: —";
                    return;
                }
                var age = DateTime.UtcNow - hb.Value;
                TxtHeartbeat.Text = age.TotalMinutes < 2
                    ? "心跳: 正常"
                    : $"心跳: {(int)age.TotalMinutes} 分钟前";
            });
        }
        catch { }
    }

    // ── Data loading ────────────────────────────────────────────

    private void LoadTodayDataBg()
    {
        try
        {
            var today   = DateTime.Today.ToString("yyyy-MM-dd");
            var records = _repo!.QueryByDateRange(today, today);
            var vms     = records.Select(r => new IoRecordVm(r)).ToList();

            long totalRead  = vms.Sum(v => v.ReadBytes);
            long totalWrite = vms.Sum(v => v.WriteBytes);
            int  procCount  = vms
                .Where(v => v.ProcessName != "[System]" && v.ProcessName != "[Unknown]")
                .Select(v => v.ProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            Dispatcher.Invoke(() =>
            {
                _todayItems.Clear();
                foreach (var vm in vms) _todayItems.Add(vm);

                TxtTotalRead.Text     = IoRecordVm.Fmt(totalRead);
                TxtTotalReadSub.Text  = $"{vms.Count} 条记录";
                TxtTotalWrite.Text    = IoRecordVm.Fmt(totalWrite);
                TxtTotalWriteSub.Text = DateTime.Today.ToString("yyyy-MM-dd");
                TxtProcCount.Text     = procCount.ToString();
                TxtProcCountSub.Text  = $"排除系统进程后";
                RunRecordCount.Text   = vms.Count.ToString();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                TxtTotalRead.Text  = "读取失败";
                TxtTotalReadSub.Text = ex.Message.Length > 50 ? ex.Message[..50] + "…" : ex.Message;
            });
        }
    }

    // ── Button handlers ─────────────────────────────────────────

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => await RefreshAllAsync();

    private void CmbTheme_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressThemeChange) return;
        if (CmbTheme.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            item.Tag is string theme)
            App.ApplyTheme(theme);
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                Dispatcher.Invoke(() => _ = RefreshAllAsync());
            }
            catch (Exception ex) { ShowError("启动服务失败", ex.Message); }
        });
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定要停止 DiskMonitor 服务吗？\n停止后将不再记录 I/O 数据。",
                "停止服务", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(ServiceName);
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                Dispatcher.Invoke(() => _ = RefreshAllAsync());
            }
            catch (Exception ex) { ShowError("停止服务失败", ex.Message); }
        });
    }

    private void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        var exePath = FindServiceExe();
        if (exePath == null)
        {
            MessageBox.Show("找不到 DiskMonitor.Service.exe。\n请先编译项目。",
                "找不到可执行文件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        RunElevated("sc.exe",
            $"create {ServiceName} binPath=\"{exePath}\" start=auto " +
            $"DisplayName=\"DiskMonitor IO Monitor\"");
    }

    private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定要卸载 DiskMonitor 服务吗？\n历史数据库将保留。",
                "卸载服务", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        RunElevated("cmd.exe",
            $"/c sc.exe stop {ServiceName} 2>nul & sc.exe delete {ServiceName} 2>nul");
        await Task.Delay(6000);
        await RefreshAllAsync();
    }

    private void BtnQuery_Click(object sender, RoutedEventArgs e)
    {
        if (_repo == null) { ShowNoDb(); return; }

        var from = (DpFrom.SelectedDate ?? DateTime.Today.AddDays(-6)).ToString("yyyy-MM-dd");
        var to   = (DpTo.SelectedDate   ?? DateTime.Today).ToString("yyyy-MM-dd");

        try
        {
            var records = _repo.QueryByDateRange(from, to);
            _historyItems.Clear();
            foreach (var r in records) _historyItems.Add(new IoRecordVm(r));
            TxtHistoryCount.Text = $"{_historyItems.Count} 条记录";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"查询失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExportHistory_Click(object sender, RoutedEventArgs e)
        => ExportCsv(_historyItems);

    private void BtnExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_repo == null) { ShowNoDb(); return; }
        try
        {
            var all = _repo.QueryByDateRange("2020-01-01", DateTime.Today.ToString("yyyy-MM-dd"));
            ExportCsv(all.Select(r => new IoRecordVm(r)));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnFullUninstall_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "这将停止服务、注销服务注册、删除可执行文件。\n\n是否同时删除历史数据库？\n" +
            "（点击 [是] 删除数据库，[否] 保留数据库，[取消] 中止操作）",
            "完全卸载", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel) return;
        bool deleteDb = answer == MessageBoxResult.Yes;

        // 从注册表读取实际注册的服务二进制目录（在删除服务前读，否则读不到）
        string? serviceBinDir = GetServiceBinDir();

        _db?.Dispose();
        _db   = null;
        _repo = null;

        // 构造一条 elevated 命令完成所有需要管理员权限的操作：
        // 1) 停止服务   2) 删除服务注册   3) 删除 Event Log 源   4) 删除二进制目录
        var elevatedCmd = new StringBuilder();
        elevatedCmd.Append($"sc.exe stop {ServiceName} 2>nul");
        elevatedCmd.Append($" & sc.exe delete {ServiceName} 2>nul");
        elevatedCmd.Append($" & reg.exe delete \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\EventLog\\Application\\{ServiceName}\" /f 2>nul");
        if (!string.IsNullOrEmpty(serviceBinDir))
            elevatedCmd.Append($" & rmdir /s /q \"{serviceBinDir}\" 2>nul");

        RunElevated("cmd.exe", $"/c {elevatedCmd}");

        // 等待 elevated 进程完成（sc stop 本身是同步的，总共预留 8 秒）
        await Task.Delay(8000);

        // 清理 %ProgramData%\DiskMonitor\ （无需 elevation，Administrators 组有完全控制）
        var dataDir = Path.GetDirectoryName(_dbPath)!;
        try
        {
            if (deleteDb)
            {
                if (Directory.Exists(dataDir))
                    Directory.Delete(dataDir, recursive: true);
            }
            else
            {
                // 保留数据库，删除其余文件和子目录（如日志）
                if (Directory.Exists(dataDir))
                {
                    foreach (var f in Directory.GetFiles(dataDir))
                        if (!f.Equals(_dbPath, StringComparison.OrdinalIgnoreCase))
                            try { File.Delete(f); } catch { }
                    foreach (var d in Directory.GetDirectories(dataDir))
                        try { Directory.Delete(d, recursive: true); } catch { }
                }
            }
        }
        catch { }

        // 清理 %AppData%\DiskMonitor\ （主题设置等用户配置）
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskMonitor");
        try { if (Directory.Exists(appDataDir)) Directory.Delete(appDataDir, recursive: true); }
        catch { }

        MessageBox.Show("卸载完成。数据已清理，应用程序可安全关闭。",
            "完全卸载", MessageBoxButton.OK, MessageBoxImage.Information);

        await RefreshAllAsync();
    }

    // 从 SCM 注册表读取服务二进制所在目录（无论将来安装到哪都正确）
    private static string? GetServiceBinDir()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            if (key?.GetValue("ImagePath") is not string raw || string.IsNullOrWhiteSpace(raw))
                return null;
            // ImagePath 可能带引号，如 "C:\path\svc.exe" 或不带引号
            var exePath = raw.Trim().TrimStart('"').Split('"')[0].Trim();
            return Path.GetDirectoryName(exePath);
        }
        catch { return null; }
    }

    // ── Right-click context menu ────────────────────────────────

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Walk up the visual tree to find the DataGridRow under the cursor and select it
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is DataGridRow row)
            row.IsSelected = true;
    }

    private void MenuOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextMenuVm(sender);
        if (vm == null) return;

        if (string.IsNullOrEmpty(vm.ProcessPath))
        {
            MessageBox.Show("此条目没有关联的文件路径（系统或未知进程）。",
                "无路径", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dir = Path.GetDirectoryName(vm.ProcessPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show($"目录不存在（程序可能已卸载）：\n{dir ?? vm.ProcessPath}",
                "目录未找到", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var vm = GetContextMenuVm(sender);
        if (vm == null) return;

        var text = string.IsNullOrEmpty(vm.ProcessPath) ? vm.ProcessName : vm.ProcessPath;
        Clipboard.SetText(text);
    }

    private static IoRecordVm? GetContextMenuVm(object menuItemSender)
    {
        var menu = ((MenuItem)menuItemSender).Parent as ContextMenu;
        return (menu?.PlacementTarget as DataGrid)?.SelectedItem as IoRecordVm;
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static string? FindServiceExe()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1. 便携包标准布局：service\ 子目录
        var portable = Path.Combine(baseDir, "service", "DiskMonitor.Service.exe");
        if (File.Exists(portable)) return portable;

        // 2. 同级目录（平铺布局）
        var flat = Path.Combine(baseDir, "DiskMonitor.Service.exe");
        if (File.Exists(flat)) return flat;

        // 3. 开发布局：从 bin\Debug\net9.0-windows\ 上溯四层找解决方案根
        var root = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        foreach (var cfg in new[] { "Debug", "Release" })
        foreach (var tfm in new[] { "net9.0", "net9.0-windows" })
        {
            var p = Path.Combine(root, "DiskMonitor.Service", "bin", cfg, tfm,
                                 "DiskMonitor.Service.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void RunElevated(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = fileName,
                Arguments       = arguments,
                UseShellExecute = true,
                Verb            = "runas",
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"操作失败（可能需要管理员权限）:\n{ex.Message}",
                "服务管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void ExportCsv(IEnumerable<IoRecordVm> items)
    {
        var dlg = new SaveFileDialog
        {
            Filter     = "CSV 文件|*.csv",
            DefaultExt = ".csv",
            FileName   = $"diskmonitor_{DateTime.Today:yyyyMMdd}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("date,process_name,process_path,drive_letter," +
                          "volume_label,disk_model,read_bytes,write_bytes");

            foreach (var r in items)
                sb.AppendLine($"{r.Date},{Csv(r.ProcessName)},{Csv(r.ProcessPath)}," +
                              $"{r.DriveLetter},{Csv(r.VolumeLabel)},{Csv(r.DiskModel)}," +
                              $"{r.ReadBytes},{r.WriteBytes}");

            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show($"已导出至:\n{dlg.FileName}",
                "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败: {ex.Message}",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? '"' + s.Replace("\"", "\"\"") + '"'
            : s;

    private void ShowError(string title, string msg) =>
        Dispatcher.Invoke(() =>
            MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Error));

    private void ShowNoDb() =>
        MessageBox.Show($"数据库文件不存在:\n{_dbPath}\n\n请先启动服务以初始化数据库。",
            "数据库未就绪", MessageBoxButton.OK, MessageBoxImage.Information);
}

// ── ViewModel ───────────────────────────────────────────────────

public sealed class IoRecordVm
{
    public string ProcessName { get; }
    public string ProcessPath { get; }
    public string DriveLetter { get; }
    public string VolumeLabel { get; }
    public string DiskModel   { get; }
    public long   ReadBytes   { get; }
    public long   WriteBytes  { get; }
    public string Date        { get; }

    public long   TotalBytes   => ReadBytes + WriteBytes;
    public string ReadDisplay  => Fmt(ReadBytes);
    public string WriteDisplay => Fmt(WriteBytes);
    public string TotalDisplay => Fmt(TotalBytes);

    public IoRecordVm(IoRecord r)
    {
        ProcessName = r.ProcessName;
        ProcessPath = r.ProcessPath;
        DriveLetter = r.DriveLetter;
        VolumeLabel = r.VolumeLabel;
        DiskModel   = r.DiskModel;
        ReadBytes   = r.ReadBytes;
        WriteBytes  = r.WriteBytes;
        Date        = r.Date;
    }

    public static string Fmt(long b) => b switch
    {
        >= 1_073_741_824 => $"{b / 1_073_741_824.0:F2} GB",
        >= 1_048_576     => $"{b / 1_048_576.0:F1} MB",
        >= 1_024         => $"{b / 1_024.0:F0} KB",
        _                => $"{b} B",
    };
}
