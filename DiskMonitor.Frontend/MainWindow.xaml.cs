using DiskMonitor.Core.Database;
using DiskMonitor.Core.Models;
using DiskMonitor.Frontend.Analysis;
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

    private readonly ObservableCollection<IoRecordVm>  _todayItems   = [];
    private readonly ObservableCollection<IoRecordVm>  _historyItems = [];
    private readonly ObservableCollection<AlertEntry>  _alertItems   = [];
    private readonly ObservableCollection<DiskStats>   _diskStatsItems = [];
    private AnalysisConfig _analysisConfig = AnalysisConfig.Load();
    private List<IoRecord> _allRecordsCache = [];
    private List<DiskStats> _lastDiskStats   = [];

    // Shell extension tab
    private List<ShellExtensionEntry> _allShellEntries = [];
    private readonly ObservableCollection<ShellExtensionEntry>  _shellItems          = [];
    private readonly ObservableCollection<ShellExtensionEntry>  _top5NewestItems     = [];
    private readonly ObservableCollection<ShellExtensionEntry>  _top10ModifiedItems  = [];
    private readonly ObservableCollection<ExplorerDayBucket>    _explorerDayItems    = [];
    private readonly ObservableCollection<ShellExtensionEntry>  _ghostItems          = [];
    private readonly ObservableCollection<ShellWhitelistEntryVm> _shellWhitelistItems = [];

    // Service monitor tab
    private List<SvcHostServiceEntry> _allSvcEntries = [];
    private readonly ObservableCollection<SvcHostServiceEntry>  _svcItems            = [];
    private readonly ObservableCollection<ExplorerDayBucket>    _svchostDayItems     = [];
    private readonly ObservableCollection<SvcWhitelistEntryVm>  _svcWhitelistItems   = [];
    private DispatcherTimer? _svcRefreshTimer;

    // ── Init ────────────────────────────────────────────────────

    public MainWindow()
    {
        // Apply saved theme before InitializeComponent so DynamicResources resolve correctly
        var savedTheme = App.LoadSavedTheme();
        App.ApplyTheme(savedTheme);

        InitializeComponent();

        GridToday.ItemsSource   = _todayItems;
        GridHistory.ItemsSource = _historyItems;
        GridAlerts.ItemsSource    = _alertItems;
        GridDiskStats.ItemsSource = _diskStatsItems;
        GridShellExt.ItemsSource        = _shellItems;
        GridTop5Newest.ItemsSource      = _top5NewestItems;
        GridTop10Modified.ItemsSource   = _top10ModifiedItems;
        ExplorerDayChart.ItemsSource    = _explorerDayItems;
        GridExplorerDays.ItemsSource    = _explorerDayItems;
        GridGhosts.ItemsSource          = _ghostItems;
        GridShellWhitelist.ItemsSource  = _shellWhitelistItems;
        GridSvcHostServices.ItemsSource = _svcItems;
        SvchostDayChart.ItemsSource     = _svchostDayItems;
        GridSvcWhitelist.ItemsSource    = _svcWhitelistItems;

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

        // Analysis UI state from saved config
        _suppressThemeChange = true;
        ChkAnalysisEnabled.IsChecked      = _analysisConfig.Enabled;
        ChkLooseMode.IsChecked            = _analysisConfig.LooseMode;
        ChkExcludeSysProc.IsChecked       = _analysisConfig.ExcludeSystemProcesses;
        ChkExcludeExplorer.IsChecked      = _analysisConfig.ExcludeExplorer;
        ChkExcludeSystemFolder.IsChecked    = _analysisConfig.ShellWhitelist.ExcludeSystemFolder;
        ChkSvcExcludeSystemFolder.IsChecked = _analysisConfig.SvcWhitelist.ExcludeSystemFolder;
        _suppressThemeChange = false;
        UpdateAnalysisStatus();
        RefreshShellWhitelistTags();
        RefreshSvcWhitelistTags();
        // Pre-populate vendor comboboxes from saved whitelist
        foreach (var v in _analysisConfig.ShellWhitelist.VendorNames)
            CmbWhitelistVendor.Items.Add(v);
        foreach (var v in _analysisConfig.SvcWhitelist.VendorNames)
            CmbSvcWhitelistVendor.Items.Add(v);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TryOpenDb();
        _ = RefreshAllAsync();
        _ = LoadAnalysisDropdownsAsync();

        _timer.Tick += async (_, _) =>
        {
            try { await RefreshAllAsync(); }
            catch { }
        };
        _timer.Start();

#if NSIS_BUILD
        _ = CheckAndPromptServiceInstallAsync();
#endif
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _svcRefreshTimer?.Stop();
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

#if NSIS_BUILD
    private async Task CheckAndPromptServiceInstallAsync()
    {
        bool installed;
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            installed = true;
        }
        catch (InvalidOperationException) { installed = false; }
        catch { return; }

        if (installed) return;

        var result = MessageBox.Show(
            "DiskMonitor 后台服务尚未安装。\n\n" +
            "服务负责在后台持续采集磁盘 I/O 数据，是本软件正常运行的必要组件。\n\n" +
            "点击「确定」将弹出管理员权限请求，确认后自动完成安装并启动服务。",
            "尚未安装服务",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.OK);

        if (result != MessageBoxResult.OK) return;

        var exePath = FindServiceExe();
        if (exePath == null)
        {
            MessageBox.Show("找不到 DiskMonitor.Service.exe，请重新安装程序。",
                "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/c sc.exe create {ServiceName} binPath= \"{exePath}\" " +
                                   $"start= auto DisplayName= \"DiskMonitor IO Monitor\" " +
                                   $"&& sc.exe start {ServiceName}",
                UseShellExecute = true,
                Verb            = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return; // 用户取消了 UAC，下次启动仍会提示
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动安装程序失败:\n{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await Task.Delay(4000);
        await RefreshAllAsync();
    }
#endif

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

    // ── Analysis ─────────────────────────────────────────────────

    private void UpdateAnalysisStatus()
    {
        if (!_analysisConfig.Enabled)
        {
            TxtAnalysisStatus.Text = "分析已禁用";
            BtnRunAnalysis.IsEnabled = false;
            return;
        }
        BtnRunAnalysis.IsEnabled = true;
        TxtAnalysisStatus.Text = _allRecordsCache.Count == 0
            ? "（尚未加载数据，点击「开始分析」）"
            : $"基于 {_allRecordsCache.Select(r => r.Date).Distinct().Count()} 天历史数据";
    }

    private async Task LoadAnalysisDropdownsAsync()
    {
        if (_repo == null) return;
        var records = await Task.Run(() =>
            _repo.QueryByDateRange("2000-01-01", DateTime.Today.ToString("yyyy-MM-dd")));

        var volumes = records
            .GroupBy(r => r.VolumeGuid)
            .Select(g => g.First())
            .OrderBy(r => r.DriveLetter)
            .ToList();

        var disks = records
            .GroupBy(r => r.DiskNumber)
            .Select(g => g.First())
            .OrderBy(r => r.DiskNumber)
            .ToList();

        var processes = records
            .Select(r => r.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n)
            .ToList();

        Dispatcher.Invoke(() =>
        {
            // Exclusion comboboxes (volume only)
            foreach (var cb in new[] { CmbExcludeVolume, CmbExcludeDateVolume })
            {
                cb.Items.Clear();
                cb.Items.Add(new ComboBoxItem { Content = "所有驱动器", Tag = "__all__" });
                foreach (var r in volumes)
                {
                    var label = string.IsNullOrEmpty(r.VolumeLabel) ? r.DriveLetter : $"{r.DriveLetter} ({r.VolumeLabel})";
                    cb.Items.Add(new ComboBoxItem { Content = label, Tag = r.VolumeGuid });
                }
                if (cb.Items.Count > 0) cb.SelectedIndex = 0;
            }

            // Threshold combobox: global + logical volumes + physical disks, with type prefixes
            CmbThresholdVolume.Items.Clear();
            CmbThresholdVolume.Items.Add(new ComboBoxItem { Content = "全局（所有未单独设置的驱动器）", Tag = "__global__" });
            foreach (var r in volumes)
            {
                var label = string.IsNullOrEmpty(r.VolumeLabel)
                    ? $"逻辑: {r.DriveLetter}"
                    : $"逻辑: {r.DriveLetter} ({r.VolumeLabel})";
                CmbThresholdVolume.Items.Add(new ComboBoxItem { Content = label, Tag = "v:" + r.VolumeGuid });
            }
            foreach (var r in disks)
            {
                var label = string.IsNullOrEmpty(r.DiskModel)
                    ? $"物理: 磁盘 {r.DiskNumber}"
                    : $"物理: 磁盘 {r.DiskNumber} — {r.DiskModel}";
                CmbThresholdVolume.Items.Add(new ComboBoxItem { Content = label, Tag = "d:" + r.DiskNumber.ToString() });
            }
            if (CmbThresholdVolume.Items.Count > 0) CmbThresholdVolume.SelectedIndex = 0;

            // Disk combobox
            CmbExcludeDisk.Items.Clear();
            CmbExcludeDisk.Items.Add(new ComboBoxItem { Content = "（不排除物理磁盘）", Tag = "__none__" });
            foreach (var r in disks)
            {
                var label = string.IsNullOrEmpty(r.DiskModel) ? $"磁盘 {r.DiskNumber}" : $"磁盘 {r.DiskNumber}: {r.DiskModel}";
                CmbExcludeDisk.Items.Add(new ComboBoxItem { Content = label, Tag = r.DiskNumber });
            }
            if (CmbExcludeDisk.Items.Count > 0) CmbExcludeDisk.SelectedIndex = 0;

            // Process combobox
            CmbExcludeProcess.Items.Clear();
            foreach (var p in processes)
                CmbExcludeProcess.Items.Add(p);

            RefreshExclusionLists();
            RefreshThresholdStatus();
            UpdateAnalysisStatus();
        });
    }

    private async void BtnRunAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (_repo == null) { ShowNoDb(); return; }
        BtnRunAnalysis.IsEnabled = false;
        TxtAnalysisStatus.Text = "分析中…";
        TxtAlertsOverlay.Text = "";

        try
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            _allRecordsCache = await Task.Run(() =>
                _repo.QueryByDateRange("2000-01-01", today));

            var result = await Task.Run(() =>
                AnalysisEngine.Analyze(_allRecordsCache, _analysisConfig, today));

            _alertItems.Clear();
            foreach (var a in result.Alerts) _alertItems.Add(a);

            _lastDiskStats = result.DiskStats;
            _diskStatsItems.Clear();
            foreach (var s in result.DiskStats.OrderBy(s => s.DriveLetter)) _diskStatsItems.Add(s);

            int days = result.TotalDays;
            if (!result.EnoughData)
            {
                TxtAlertsOverlay.Text = $"数据不足（当前 {days} 天，需至少 30 天）";
                TxtAnalysisStatus.Text = $"数据不足（{days} 天 / 30天）";
            }
            else if (_alertItems.Count == 0)
            {
                TxtAlertsOverlay.Text = "暂无异常告警";
                TxtAnalysisStatus.Text = $"基于 {days} 天数据 · 无异常";
            }
            else
            {
                TxtAlertsOverlay.Text = "";
                TxtAnalysisStatus.Text = $"基于 {days} 天数据 · {_alertItems.Count} 条告警";
            }

            // Refresh threshold combobox with latest volume info
            await LoadAnalysisDropdownsAsync();
        }
        catch (Exception ex)
        {
            TxtAlertsOverlay.Text = $"分析失败: {ex.Message}";
            TxtAnalysisStatus.Text = "分析失败";
        }
        finally
        {
            BtnRunAnalysis.IsEnabled = _analysisConfig.Enabled;
        }
    }

    private void AnalysisOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeChange) return;
        _analysisConfig.Enabled   = ChkAnalysisEnabled.IsChecked == true;
        _analysisConfig.LooseMode = ChkLooseMode.IsChecked == true;
        _analysisConfig.Save();
        UpdateAnalysisStatus();
    }

    private void BtnIgnoreAlertDay_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not AlertEntry alert) return;
        var rule = new DateExclusionRule
        {
            Type        = "single",
            Date        = alert.Date,
            VolumeGuids = [alert.VolumeGuid],
            Description = $"忽略 {alert.Date} {alert.DriveDisplay}",
        };
        _analysisConfig.ExcludedDateRules.Add(rule);
        _analysisConfig.Save();

        var item = _alertItems.FirstOrDefault(a => a.Date == alert.Date && a.VolumeGuid == alert.VolumeGuid);
        if (item != null) _alertItems.Remove(item);

        RefreshExclusionLists();
        if (_alertItems.Count == 0 && TxtAlertsOverlay.Text == "")
            TxtAlertsOverlay.Text = "暂无异常告警";
    }

    // ── Drive exclusions ─────────────────────────────────────────

    private void BtnAddDriveExclusion_Click(object sender, RoutedEventArgs e)
    {
        bool added = false;

        if (CmbExcludeVolume.SelectedItem is ComboBoxItem vi && vi.Tag is string guid && guid != "__all__")
        {
            if (!_analysisConfig.ExcludedVolumes.Any(v => v.Guid == guid))
            {
                var first = _allRecordsCache.FirstOrDefault(r => r.VolumeGuid == guid);
                _analysisConfig.ExcludedVolumes.Add(new VolumeExclusion
                {
                    Guid        = guid,
                    DriveLetter = first?.DriveLetter ?? "",
                    Label       = first?.VolumeLabel ?? "",
                });
                added = true;
            }
        }

        if (CmbExcludeDisk.SelectedItem is ComboBoxItem di && di.Tag is int diskNum)
        {
            if (!_analysisConfig.ExcludedDisks.Any(d => d.DiskNumber == diskNum))
            {
                var first = _allRecordsCache.FirstOrDefault(r => r.DiskNumber == diskNum);
                _analysisConfig.ExcludedDisks.Add(new DiskExclusion
                {
                    DiskNumber = diskNum,
                    Model      = first?.DiskModel ?? "",
                });
                added = true;
            }
        }

        if (added) { _analysisConfig.Save(); RefreshExclusionLists(); }
    }

    // ── Process exclusions ────────────────────────────────────────

    private void ProcessExclusionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeChange) return;
        _analysisConfig.ExcludeSystemProcesses = ChkExcludeSysProc.IsChecked == true;
        _analysisConfig.ExcludeExplorer        = ChkExcludeExplorer.IsChecked == true;
        _analysisConfig.Save();
    }

    private void BtnAddProcessExclusion_Click(object sender, RoutedEventArgs e)
    {
        var name = (CmbExcludeProcess.SelectedItem as string)
                   ?? CmbExcludeProcess.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!_analysisConfig.ExcludedProcessNames
                .Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            _analysisConfig.ExcludedProcessNames.Add(name);
            _analysisConfig.Save();
            RefreshExclusionLists();
        }
    }

    // ── Date exclusions ───────────────────────────────────────────

    private void BtnSetIgnorePeriod_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(TxtIgnoreHours.Text, out double hours) || hours <= 0)
        {
            MessageBox.Show("请输入有效的小时数（正数）。", "输入错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Convert hours → a "range" exclusion from (now - hours) to now
        var cutoff = DateTime.Today.AddHours(-hours);
        var rule = new DateExclusionRule
        {
            Type        = "range",
            Start       = cutoff.ToString("yyyy-MM-dd"),
            End         = DateTime.Today.ToString("yyyy-MM-dd"),
            Description = $"最近 {hours:F0} 小时内的数据（设置于 {DateTime.Now:MM-dd HH:mm}）",
        };
        _analysisConfig.ExcludedDateRules.Add(rule);
        _analysisConfig.Save();
        RefreshExclusionLists();
    }

    private void BtnIgnoreAllBefore_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (MessageBox.Show($"将所有 {today} 之前的历史记录排除在分析之外。\n\n这不会删除数据，仍可在历史标签页正常查看。\n确认吗？",
                "排除所有历史", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _analysisConfig.IgnoreAllBefore = today;
        _analysisConfig.Save();
        RefreshExclusionLists();
    }

    private void BtnAddDateExclusion_Click(object sender, RoutedEventArgs e)
    {
        var from = DpExcludeFrom.SelectedDate;
        var to   = DpExcludeTo.SelectedDate;
        if (from == null || to == null)
        {
            MessageBox.Show("请选择起止日期。", "日期未选择",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (from > to) { var tmp = from; from = to; to = tmp; }

        List<string>? guids = null;
        if (CmbExcludeDateVolume.SelectedItem is ComboBoxItem item &&
            item.Tag is string guid && guid != "__all__")
            guids = [guid];

        var driveLabel = guids == null ? "所有驱动器"
            : (CmbExcludeDateVolume.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

        var rule = new DateExclusionRule
        {
            Type        = from == to ? "single" : "range",
            Date        = from == to ? from.Value.ToString("yyyy-MM-dd") : null,
            Start       = from != to ? from.Value.ToString("yyyy-MM-dd") : null,
            End         = from != to ? to.Value.ToString("yyyy-MM-dd")   : null,
            VolumeGuids = guids,
            Description = from == to
                ? $"{from.Value:yyyy-MM-dd}  {driveLabel}"
                : $"{from.Value:yyyy-MM-dd} ~ {to.Value:yyyy-MM-dd}  {driveLabel}",
        };
        _analysisConfig.ExcludedDateRules.Add(rule);
        _analysisConfig.Save();
        RefreshExclusionLists();
    }

    // ── Settings: reference data + thresholds ─────────────────────

    private async void BtnRefreshStats_Click(object sender, RoutedEventArgs e)
    {
        if (_repo == null) { ShowNoDb(); return; }
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var records = await Task.Run(() =>
            _repo.QueryByDateRange("2000-01-01", today));
        _allRecordsCache = records;
        var result = await Task.Run(() =>
            AnalysisEngine.Analyze(records, _analysisConfig, today));
        _lastDiskStats = result.DiskStats;
        _diskStatsItems.Clear();
        foreach (var s in result.DiskStats.OrderBy(s => s.DriveLetter)) _diskStatsItems.Add(s);
        UpdateAnalysisStatus();
    }

    private void CmbThresholdVolume_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshThresholdStatus();

    private void BtnSaveThreshold_Click(object sender, RoutedEventArgs e)
    {
        if (CmbThresholdVolume.SelectedItem is not ComboBoxItem vi) return;
        string tag = vi.Tag?.ToString() ?? "__global__";

        if (!TryParseGB(TxtThresholdReadGB.Text,  out long? readBytes)  ||
            !TryParseGB(TxtThresholdWriteGB.Text, out long? writeBytes) ||
            !TryParseGB(TxtThresholdTotalGB.Text, out long? totalBytes))
        {
            MessageBox.Show("请输入有效的 GB 值（正数），或留空以使用自动阈值。",
                "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var entry = new ThresholdEntry { ReadBytes = readBytes, WriteBytes = writeBytes, TotalBytes = totalBytes };

        if      (tag == "__global__")        _analysisConfig.GlobalThreshold = entry;
        else if (tag.StartsWith("v:"))       _analysisConfig.VolumeThresholds[tag[2..]] = entry;
        else if (tag.StartsWith("d:"))       _analysisConfig.DiskThresholds[tag[2..]]   = entry;

        _analysisConfig.Save();
        RefreshThresholdStatus();
    }

    private void BtnClearThreshold_Click(object sender, RoutedEventArgs e)
    {
        if (CmbThresholdVolume.SelectedItem is not ComboBoxItem vi) return;
        string tag = vi.Tag?.ToString() ?? "__global__";

        if      (tag == "__global__")   _analysisConfig.GlobalThreshold = new ThresholdEntry();
        else if (tag.StartsWith("v:"))  _analysisConfig.VolumeThresholds.Remove(tag[2..]);
        else if (tag.StartsWith("d:"))  _analysisConfig.DiskThresholds.Remove(tag[2..]);

        _analysisConfig.Save();
        TxtThresholdReadGB.Text = TxtThresholdWriteGB.Text = TxtThresholdTotalGB.Text = "";
        RefreshThresholdStatus();
    }

    private void RefreshThresholdStatus()
    {
        if (CmbThresholdVolume.SelectedItem is not ComboBoxItem vi) return;
        string tag = vi.Tag?.ToString() ?? "__global__";

        ThresholdEntry? entry = tag switch
        {
            "__global__"                    => _analysisConfig.GlobalThreshold,
            var k when k.StartsWith("v:")   => _analysisConfig.VolumeThresholds.GetValueOrDefault(k[2..]),
            var k when k.StartsWith("d:")   => _analysisConfig.DiskThresholds.GetValueOrDefault(k[2..]),
            _                               => null,
        };

        TxtThresholdReadGB.Text  = entry?.ReadBytes  is long rb ? (rb / 1_073_741_824.0).ToString("F2") : "";
        TxtThresholdWriteGB.Text = entry?.WriteBytes is long wb ? (wb / 1_073_741_824.0).ToString("F2") : "";
        TxtThresholdTotalGB.Text = entry?.TotalBytes is long tb ? (tb / 1_073_741_824.0).ToString("F2") : "";

        if (entry?.HasAny == true)
        {
            var parts = new List<string>();
            if (entry.ReadBytes.HasValue)  parts.Add($"读取 {IoRecordVm.Fmt(entry.ReadBytes.Value)}");
            if (entry.WriteBytes.HasValue) parts.Add($"写入 {IoRecordVm.Fmt(entry.WriteBytes.Value)}");
            if (entry.TotalBytes.HasValue) parts.Add($"合计 {IoRecordVm.Fmt(entry.TotalBytes.Value)}");
            TxtThresholdStatus.Text = "当前: " + string.Join(" / ", parts);
        }
        else
        {
            TxtThresholdStatus.Text = "当前: 全部自动";
        }
    }

    private static bool TryParseGB(string text, out long? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.CurrentCulture, out double gb) || gb < 0)
            return false;
        bytes = (long)(gb * 1_073_741_824L);
        return true;
    }

    // ── Exclusion list rendering ──────────────────────────────────

    private void RefreshExclusionLists()
    {
        // Drive exclusions
        DriveExclusionList.Items.Clear();
        foreach (var v in _analysisConfig.ExcludedVolumes)
        {
            var label = string.IsNullOrEmpty(v.Label) ? v.DriveLetter : $"{v.DriveLetter} ({v.Label})";
            var captured = v;
            DriveExclusionList.Items.Add(MakeTag($"逻辑: {label}",
                () => { _analysisConfig.ExcludedVolumes.Remove(captured); _analysisConfig.Save(); RefreshExclusionLists(); }));
        }
        foreach (var d in _analysisConfig.ExcludedDisks)
        {
            var label = string.IsNullOrEmpty(d.Model) ? $"磁盘 {d.DiskNumber}" : $"磁盘 {d.DiskNumber}: {d.Model}";
            var captured = d;
            DriveExclusionList.Items.Add(MakeTag($"物理: {label}",
                () => { _analysisConfig.ExcludedDisks.Remove(captured); _analysisConfig.Save(); RefreshExclusionLists(); }));
        }

        // IgnoreAllBefore pseudo-tag
        if (_analysisConfig.IgnoreAllBefore != null)
        {
            DriveExclusionList.Items.Add(MakeTag($"全局: 排除 {_analysisConfig.IgnoreAllBefore} 前所有数据",
                () => { _analysisConfig.IgnoreAllBefore = null; _analysisConfig.Save(); RefreshExclusionLists(); }));
        }

        // Process exclusions
        ProcessExclusionList.Items.Clear();
        foreach (var p in _analysisConfig.ExcludedProcessNames.ToList())
        {
            var captured = p;
            ProcessExclusionList.Items.Add(MakeTag(captured,
                () => { _analysisConfig.ExcludedProcessNames.Remove(captured); _analysisConfig.Save(); RefreshExclusionLists(); }));
        }

        // Date exclusions
        DateExclusionList.Items.Clear();
        foreach (var r in _analysisConfig.ExcludedDateRules.ToList())
        {
            var captured = r;
            DateExclusionList.Items.Add(MakeTag(r.Description,
                () => { _analysisConfig.ExcludedDateRules.Remove(captured); _analysisConfig.Save(); RefreshExclusionLists(); }));
        }
    }

    private UIElement MakeTag(string text, Action onRemove)
    {
        Brush borderBrush = TryFindResource("BgBorder") as Brush ?? Brushes.Gray;
        Brush textBrush   = TryFindResource("TxtSecond") as Brush ?? Brushes.DimGray;
        Brush bgBrush     = TryFindResource("BgHover")  as Brush ?? Brushes.Transparent;

        var btn = new Button
        {
            Content         = "×",
            FontSize        = 13,
            FontWeight      = FontWeights.Bold,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(4, 0, 4, 0),
            Cursor          = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground      = textBrush,
        };
        btn.Click += (_, _) => onRemove();

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text              = text,
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = textBrush,
            Margin            = new Thickness(0, 0, 2, 0),
        });
        panel.Children.Add(btn);

        return new Border
        {
            Child           = panel,
            Background      = bgBrush,
            BorderBrush     = borderBrush,
            Margin          = new Thickness(0, 0, 6, 6),
            Padding         = new Thickness(10, 4, 6, 4),
            CornerRadius    = new CornerRadius(4),
            BorderThickness = new Thickness(1),
        };
    }

    // ── Sub-tab navigation ────────────────────────────────────────

    private void SubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelSubIo == null || PanelSubShell == null || PanelSubService == null) return;
        bool isIo      = RbSubIo?.IsChecked      == true;
        bool isShell   = RbSubShell?.IsChecked   == true;
        bool isService = RbSubService?.IsChecked == true;
        PanelSubIo.Visibility      = isIo      ? Visibility.Visible : Visibility.Collapsed;
        PanelSubShell.Visibility   = isShell   ? Visibility.Visible : Visibility.Collapsed;
        PanelSubService.Visibility = isService ? Visibility.Visible : Visibility.Collapsed;

        if (isShell   && _repo != null) _ = LoadExplorerTodayInfoAsync();
        if (isService && _repo != null) _ = LoadSvchostTodayInfoAsync();
    }

    // ── Plugin detection ──────────────────────────────────────────

    private async void BtnScanPlugins_Click(object sender, RoutedEventArgs e)
    {
        BtnScanPlugins.IsEnabled = false;
        TxtShellStatus.Text      = "扫描中…";
        TxtShellOverlay.Text     = "";

        try
        {
            _allShellEntries = await Task.Run(() => ShellExtensionScanner.Scan());

            // Rebuild vendor combobox from fresh scan data
            var vendors = _allShellEntries
                .Select(x => x.VendorName)
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v)
                .ToList();
            CmbWhitelistVendor.Items.Clear();
            foreach (var v in vendors) CmbWhitelistVendor.Items.Add(v);
            // Re-add saved whitelist vendors that may not appear in this scan
            foreach (var v in _analysisConfig.ShellWhitelist.VendorNames)
                if (!CmbWhitelistVendor.Items.Contains(v)) CmbWhitelistVendor.Items.Add(v);

            ApplyShellFilters();
            UpdateShellAnalysis();
            await LoadExplorerDayDataAsync();

            TxtShellStatus.Text = $"共 {_allShellEntries.Count} 条注册记录，显示 {_shellItems.Count} 条";
        }
        catch (Exception ex)
        {
            TxtShellStatus.Text  = $"扫描失败: {ex.Message}";
            TxtShellOverlay.Text = "扫描失败";
        }
        finally
        {
            BtnScanPlugins.IsEnabled = true;
        }
    }

    private void ApplyShellFilters()
    {
        bool hideMicrosoft = ChkHideMicrosoft.IsChecked == true;
        var wl = _analysisConfig.ShellWhitelist;

        _shellItems.Clear();
        foreach (var entry in _allShellEntries)
        {
            if (hideMicrosoft && entry.IsMicrosoft) continue;
            if (wl.IsWhitelisted(entry)) continue;
            _shellItems.Add(entry);
        }

        TxtShellOverlay.Text = _allShellEntries.Count > 0 && _shellItems.Count == 0
            ? "所有条目均已被过滤（微软组件 / 白名单）"
            : "";
    }

    private void UpdateShellAnalysis()
    {
        _top5NewestItems.Clear();
        foreach (var e in _allShellEntries
            .Where(e => e.FileCreationTime.HasValue && !e.IsGhost)
            .OrderByDescending(e => e.FileCreationTime)
            .Take(5))
            _top5NewestItems.Add(e);

        _top10ModifiedItems.Clear();
        foreach (var e in _allShellEntries
            .Where(e => e.LastModifiedTime.HasValue && !e.IsGhost)
            .OrderByDescending(e => e.LastModifiedTime)
            .Take(10))
            _top10ModifiedItems.Add(e);

        _ghostItems.Clear();
        foreach (var e in _allShellEntries.Where(e => e.IsGhost))
            _ghostItems.Add(e);

        bool hasGhosts = _ghostItems.Count > 0;
        TxtGhostSection.Visibility = hasGhosts ? Visibility.Visible : Visibility.Collapsed;
        GhostCard.Visibility       = hasGhosts ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadExplorerDayDataAsync()
    {
        if (_repo == null) return;
        var to   = DateTime.Today.ToString("yyyy-MM-dd");
        var from = DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd");
        var records = await Task.Run(() => _repo.QueryByDateRange(from, to));
        BuildDayBuckets(records, "explorer.exe", _explorerDayItems);
    }

    private static void BuildDayBuckets(
        IEnumerable<IoRecord> records, string processName,
        ObservableCollection<ExplorerDayBucket> target)
    {
        var list = records.ToList();
        var buckets = Enumerable.Range(0, 7).Select(i =>
        {
            var d = DateTime.Today.AddDays(-6 + i).ToString("yyyy-MM-dd");
            var dayRecs = list.Where(r =>
                r.Date == d &&
                r.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)).ToList();
            return new ExplorerDayBucket
            {
                Date       = d,
                ReadBytes  = dayRecs.Sum(r => r.ReadBytes),
                WriteBytes = dayRecs.Sum(r => r.WriteBytes),
            };
        }).ToList();

        long maxTotal = buckets.Max(b => b.TotalBytes);
        const double MaxBarH = 60.0;
        if (maxTotal > 0)
            foreach (var b in buckets)
            {
                b.ReadBarHeight  = b.ReadBytes  / (double)maxTotal * MaxBarH;
                b.WriteBarHeight = b.WriteBytes / (double)maxTotal * MaxBarH;
            }

        target.Clear();
        foreach (var b in buckets) target.Add(b);
    }

    private void ShellFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_allShellEntries.Count == 0) return;
        ApplyShellFilters();
        TxtShellStatus.Text = $"共 {_allShellEntries.Count} 条注册记录，显示 {_shellItems.Count} 条";
    }

    private async Task LoadExplorerTodayInfoAsync()
    {
        if (_repo == null) return;
        try
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var records = await Task.Run(() => _repo.QueryByDateRange(today, today));
            var expl = records
                .Where(r => r.ProcessName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                .ToList();
            long r = expl.Sum(x => x.ReadBytes);
            long w = expl.Sum(x => x.WriteBytes);
            TxtExplorerTodayInfo.Text = $"今日 explorer: 读 {IoRecordVm.Fmt(r)}  写 {IoRecordVm.Fmt(w)}";
        }
        catch { TxtExplorerTodayInfo.Text = ""; }
    }

    // ── Service monitor tab ───────────────────────────────────────

    private async Task LoadSvchostTodayInfoAsync()
    {
        if (_repo == null) return;
        try
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var records = await Task.Run(() => _repo.QueryByDateRange(today, today));
            var svc = records
                .Where(r => r.ProcessName.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
                .ToList();
            long r = svc.Sum(x => x.ReadBytes);
            long w = svc.Sum(x => x.WriteBytes);
            TxtSvchostTodayInfo.Text = $"今日 svchost: 读 {IoRecordVm.Fmt(r)}  写 {IoRecordVm.Fmt(w)}";
        }
        catch { TxtSvchostTodayInfo.Text = ""; }
    }

    private async void BtnLoadServices_Click(object sender, RoutedEventArgs e)
    {
        BtnLoadServices.IsEnabled = false;
        TxtServiceStatus.Text     = "加载中…";
        TxtServiceOverlay.Text    = "";

        try
        {
            _allSvcEntries = await Task.Run(() => SvcHostScanner.Scan());

            // Rebuild vendor combobox from fresh scan data
            var vendors = _allSvcEntries
                .Select(x => x.VendorName)
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v)
                .ToList();
            CmbSvcWhitelistVendor.Items.Clear();
            foreach (var v in vendors) CmbSvcWhitelistVendor.Items.Add(v);
            foreach (var v in _analysisConfig.SvcWhitelist.VendorNames)
                if (!CmbSvcWhitelistVendor.Items.Contains(v)) CmbSvcWhitelistVendor.Items.Add(v);

            ApplySvcFilters();
            TxtServiceStatus.Text = $"共 {_allSvcEntries.Count} 个 svchost 托管服务，显示 {_svcItems.Count} 条";

            await LoadSvchostTodayInfoAsync();
            await LoadSvchostChartAsync();
        }
        catch (Exception ex)
        {
            TxtServiceStatus.Text  = $"加载失败: {ex.Message}";
            TxtServiceOverlay.Text = "加载失败";
        }
        finally
        {
            BtnLoadServices.IsEnabled = true;
        }
    }

    private void BtnToggleSvcRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_svcRefreshTimer == null || !_svcRefreshTimer.IsEnabled)
        {
            _svcRefreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _svcRefreshTimer.Tick -= SvcRefreshTimer_Tick;
            _svcRefreshTimer.Tick += SvcRefreshTimer_Tick;
            _svcRefreshTimer.Start();
            BtnToggleSvcRefresh.Content = "停止刷新";
        }
        else
        {
            _svcRefreshTimer.Stop();
            BtnToggleSvcRefresh.Content = "开启实时刷新";
        }
    }

    private async void SvcRefreshTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            _allSvcEntries = await Task.Run(() => SvcHostScanner.Scan());
            ApplySvcFilters();
            TxtServiceStatus.Text = $"共 {_allSvcEntries.Count} 个（实时刷新中），显示 {_svcItems.Count} 条";
        }
        catch { }
    }

    private async Task LoadSvchostChartAsync()
    {
        if (_repo == null) return;
        var to   = DateTime.Today.ToString("yyyy-MM-dd");
        var from = DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd");
        var records = await Task.Run(() => _repo.QueryByDateRange(from, to));
        BuildDayBuckets(records, "svchost.exe", _svchostDayItems);
    }

    private void SvcHostOpenDir_Click(object sender, RoutedEventArgs e)
    {
        var entry = GetSvcHostEntry(sender);
        if (entry == null) return;
        if (string.IsNullOrEmpty(entry.ServiceDll))
        {
            MessageBox.Show("此服务没有关联的 ServiceDll 路径。",
                "无路径", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dir = Path.GetDirectoryName(entry.ServiceDll);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show($"目录不存在：\n{dir ?? entry.ServiceDll}",
                "目录未找到", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void SvcHostCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var entry = GetSvcHostEntry(sender);
        if (entry == null) return;
        Clipboard.SetText(string.IsNullOrEmpty(entry.ServiceDll) ? entry.ServiceName : entry.ServiceDll);
    }

    private static SvcHostServiceEntry? GetSvcHostEntry(object menuItemSender)
    {
        var menu = ((MenuItem)menuItemSender).Parent as ContextMenu;
        return (menu?.PlacementTarget as DataGrid)?.SelectedItem as SvcHostServiceEntry;
    }

    // ── Service monitor filters + whitelist ───────────────────────

    private void ApplySvcFilters()
    {
        bool hideMicrosoft = ChkHideMicrosoftSvc.IsChecked == true;
        var wl = _analysisConfig.SvcWhitelist;

        _svcItems.Clear();
        foreach (var entry in _allSvcEntries)
        {
            if (hideMicrosoft && entry.IsMicrosoft) continue;
            if (wl.IsWhitelisted(entry)) continue;
            _svcItems.Add(entry);
        }

        TxtServiceOverlay.Text = _allSvcEntries.Count > 0 && _svcItems.Count == 0
            ? "所有条目均已被过滤（微软组件 / 白名单）"
            : "";
    }

    private void SvcFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_allSvcEntries.Count == 0) return;
        ApplySvcFilters();
        TxtServiceStatus.Text = $"共 {_allSvcEntries.Count} 个 svchost 托管服务，显示 {_svcItems.Count} 条";
    }

    private void ChkSvcExcludeSystemFolder_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeChange) return;
        _analysisConfig.SvcWhitelist.ExcludeSystemFolder = ChkSvcExcludeSystemFolder.IsChecked == true;
        _analysisConfig.Save();
        RefreshSvcWhitelistTags();
        if (_allSvcEntries.Count > 0) ApplySvcFilters();
    }

    private void BtnSvcWhitelistVendor_Click(object sender, RoutedEventArgs e)
    {
        var vendor = (CmbSvcWhitelistVendor.SelectedItem as string) ?? CmbSvcWhitelistVendor.Text?.Trim();
        if (string.IsNullOrEmpty(vendor)) return;
        if (_analysisConfig.SvcWhitelist.VendorNames
                .Any(v => v.Equals(vendor, StringComparison.OrdinalIgnoreCase))) return;
        _analysisConfig.SvcWhitelist.VendorNames.Add(vendor);
        _analysisConfig.Save();
        RefreshSvcWhitelistTags();
        if (_allSvcEntries.Count > 0) ApplySvcFilters();
    }

    private void BtnSvcWhitelistService_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtSvcWhitelistService.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (_analysisConfig.SvcWhitelist.ServiceNames
                .Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        _analysisConfig.SvcWhitelist.ServiceNames.Add(name);
        _analysisConfig.Save();
        RefreshSvcWhitelistTags();
        if (_allSvcEntries.Count > 0) ApplySvcFilters();
    }

    private void BtnBrowseSvcWhitelistDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要加入白名单的目录" };
        if (dlg.ShowDialog() == true) TxtSvcWhitelistDir.Text = dlg.FolderName;
    }

    private void BtnSvcWhitelistDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = TxtSvcWhitelistDir.Text?.Trim();
        if (string.IsNullOrEmpty(dir)) return;
        if (_analysisConfig.SvcWhitelist.Directories
                .Any(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase))) return;
        _analysisConfig.SvcWhitelist.Directories.Add(dir);
        _analysisConfig.Save();
        RefreshSvcWhitelistTags();
        if (_allSvcEntries.Count > 0) ApplySvcFilters();
    }

    private void RefreshSvcWhitelistTags()
    {
        _svcWhitelistItems.Clear();
        var wl = _analysisConfig.SvcWhitelist;

        if (wl.ExcludeSystemFolder)
            _svcWhitelistItems.Add(new SvcWhitelistEntryVm
            {
                TypeDisplay = "系统文件夹",
                Value       = "%SystemRoot% 及 %SystemRoot%\\System32 内全部 DLL",
                OnRemove    = () =>
                {
                    wl.ExcludeSystemFolder = false;
                    _suppressThemeChange = true;
                    ChkSvcExcludeSystemFolder.IsChecked = false;
                    _suppressThemeChange = false;
                    _analysisConfig.Save();
                    RefreshSvcWhitelistTags();
                    if (_allSvcEntries.Count > 0) ApplySvcFilters();
                },
            });

        foreach (var v in wl.VendorNames.ToList())
        {
            var cap = v;
            _svcWhitelistItems.Add(new SvcWhitelistEntryVm
            {
                TypeDisplay = "厂商",
                Value       = cap,
                OnRemove    = () =>
                {
                    wl.VendorNames.Remove(cap);
                    _analysisConfig.Save();
                    RefreshSvcWhitelistTags();
                    if (_allSvcEntries.Count > 0) ApplySvcFilters();
                },
            });
        }

        foreach (var n in wl.ServiceNames.ToList())
        {
            var cap = n;
            _svcWhitelistItems.Add(new SvcWhitelistEntryVm
            {
                TypeDisplay = "服务名",
                Value       = cap,
                OnRemove    = () =>
                {
                    wl.ServiceNames.Remove(cap);
                    _analysisConfig.Save();
                    RefreshSvcWhitelistTags();
                    if (_allSvcEntries.Count > 0) ApplySvcFilters();
                },
            });
        }

        foreach (var d in wl.Directories.ToList())
        {
            var cap = d;
            _svcWhitelistItems.Add(new SvcWhitelistEntryVm
            {
                TypeDisplay = "目录",
                Value       = cap,
                OnRemove    = () =>
                {
                    wl.Directories.Remove(cap);
                    _analysisConfig.Save();
                    RefreshSvcWhitelistTags();
                    if (_allSvcEntries.Count > 0) ApplySvcFilters();
                },
            });
        }
    }

    private void BtnRemoveSvcWhitelistEntry_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is SvcWhitelistEntryVm vm)
            vm.OnRemove();
    }

    private void SvcHostWhitelistVendor_Click(object sender, RoutedEventArgs e)
    {
        var entry = GetSvcHostEntry(sender);
        if (entry == null || string.IsNullOrEmpty(entry.VendorName)) return;
        if (_analysisConfig.SvcWhitelist.VendorNames
                .Any(v => v.Equals(entry.VendorName, StringComparison.OrdinalIgnoreCase))) return;
        _analysisConfig.SvcWhitelist.VendorNames.Add(entry.VendorName);
        _analysisConfig.Save();
        RefreshSvcWhitelistTags();
        if (_allSvcEntries.Count > 0) ApplySvcFilters();
    }

    private void SvcHostWhitelistService_Click(object sender, RoutedEventArgs e)
    {
        var entry = GetSvcHostEntry(sender);
        if (entry == null) return;
        if (_analysisConfig.SvcWhitelist.ServiceNames
                .Any(n => n.Equals(entry.ServiceName, StringComparison.OrdinalIgnoreCase))) return;
        _analysisConfig.SvcWhitelist.ServiceNames.Add(entry.ServiceName);
        _analysisConfig.Save();
        RefreshSvcWhitelistTags();
        if (_allSvcEntries.Count > 0) ApplySvcFilters();
    }

    // ── Whitelist handlers ────────────────────────────────────────

    private void ChkExcludeSystemFolder_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeChange) return;
        _analysisConfig.ShellWhitelist.ExcludeSystemFolder = ChkExcludeSystemFolder.IsChecked == true;
        _analysisConfig.Save();
        RefreshShellWhitelistTags();
        if (_allShellEntries.Count > 0) ApplyShellFilters();
    }

    private void BtnWhitelistVendor_Click(object sender, RoutedEventArgs e)
    {
        var vendor = (CmbWhitelistVendor.SelectedItem as string) ?? CmbWhitelistVendor.Text?.Trim();
        if (string.IsNullOrEmpty(vendor)) return;
        if (_analysisConfig.ShellWhitelist.VendorNames
                .Any(v => v.Equals(vendor, StringComparison.OrdinalIgnoreCase))) return;
        _analysisConfig.ShellWhitelist.VendorNames.Add(vendor);
        _analysisConfig.Save();
        RefreshShellWhitelistTags();
        if (_allShellEntries.Count > 0) ApplyShellFilters();
    }

    private void BtnBrowseWhitelistFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "DLL 文件|*.dll|所有文件|*.*", Title = "选择要加入白名单的 DLL" };
        if (dlg.ShowDialog() == true) TxtWhitelistFile.Text = dlg.FileName;
    }

    private void BtnWhitelistFile_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtWhitelistFile.Text?.Trim();
        if (string.IsNullOrEmpty(path)) return;
        if (_analysisConfig.ShellWhitelist.FilePaths
                .Any(f => f.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        _analysisConfig.ShellWhitelist.FilePaths.Add(path);
        _analysisConfig.Save();
        RefreshShellWhitelistTags();
        if (_allShellEntries.Count > 0) ApplyShellFilters();
    }

    private void BtnBrowseWhitelistDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要加入白名单的目录" };
        if (dlg.ShowDialog() == true) TxtWhitelistDir.Text = dlg.FolderName;
    }

    private void BtnWhitelistDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = TxtWhitelistDir.Text?.Trim();
        if (string.IsNullOrEmpty(dir)) return;
        if (_analysisConfig.ShellWhitelist.Directories
                .Any(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase))) return;
        _analysisConfig.ShellWhitelist.Directories.Add(dir);
        _analysisConfig.Save();
        RefreshShellWhitelistTags();
        if (_allShellEntries.Count > 0) ApplyShellFilters();
    }

    private void RefreshShellWhitelistTags()
    {
        _shellWhitelistItems.Clear();
        var wl = _analysisConfig.ShellWhitelist;

        if (wl.ExcludeSystemFolder)
            _shellWhitelistItems.Add(new ShellWhitelistEntryVm
            {
                TypeDisplay = "系统文件夹",
                Value       = "%SystemRoot% 及 %SystemRoot%\\System32 内全部 DLL",
                OnRemove    = () =>
                {
                    wl.ExcludeSystemFolder = false;
                    _suppressThemeChange = true;
                    ChkExcludeSystemFolder.IsChecked = false;
                    _suppressThemeChange = false;
                    _analysisConfig.Save();
                    RefreshShellWhitelistTags();
                    if (_allShellEntries.Count > 0) ApplyShellFilters();
                },
            });

        foreach (var v in wl.VendorNames.ToList())
        {
            var cap = v;
            _shellWhitelistItems.Add(new ShellWhitelistEntryVm
            {
                TypeDisplay = "厂商",
                Value       = cap,
                OnRemove    = () =>
                {
                    wl.VendorNames.Remove(cap);
                    _analysisConfig.Save();
                    RefreshShellWhitelistTags();
                    if (_allShellEntries.Count > 0) ApplyShellFilters();
                },
            });
        }

        foreach (var f in wl.FilePaths.ToList())
        {
            var cap = f;
            _shellWhitelistItems.Add(new ShellWhitelistEntryVm
            {
                TypeDisplay = "DLL 文件",
                Value       = cap,
                OnRemove    = () =>
                {
                    wl.FilePaths.Remove(cap);
                    _analysisConfig.Save();
                    RefreshShellWhitelistTags();
                    if (_allShellEntries.Count > 0) ApplyShellFilters();
                },
            });
        }

        foreach (var d in wl.Directories.ToList())
        {
            var cap = d;
            _shellWhitelistItems.Add(new ShellWhitelistEntryVm
            {
                TypeDisplay = "目录",
                Value       = cap,
                OnRemove    = () =>
                {
                    wl.Directories.Remove(cap);
                    _analysisConfig.Save();
                    RefreshShellWhitelistTags();
                    if (_allShellEntries.Count > 0) ApplyShellFilters();
                },
            });
        }
    }

    private void BtnRemoveWhitelistEntry_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is ShellWhitelistEntryVm vm)
            vm.OnRemove();
    }

    // ── Shell extension context menu ──────────────────────────────

    private void ShellExtOpenDir_Click(object sender, RoutedEventArgs e)
    {
        var entry = GetShellContextEntry(sender);
        if (entry == null) return;
        if (string.IsNullOrEmpty(entry.FilePath))
        {
            MessageBox.Show("此条目没有关联的文件路径。", "无路径",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dir = Path.GetDirectoryName(entry.FilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show($"目录不存在（文件可能已删除）：\n{dir ?? entry.FilePath}",
                "目录未找到", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void ShellExtCopyPath_Click(object sender, RoutedEventArgs e)
    {
        var entry = GetShellContextEntry(sender);
        if (entry == null) return;
        Clipboard.SetText(string.IsNullOrEmpty(entry.FilePath) ? entry.Clsid : entry.FilePath);
    }

    private void ShellExtWhitelistFile_Click(object sender, RoutedEventArgs e)
    {
        var entry = GetShellContextEntry(sender);
        if (entry == null || string.IsNullOrEmpty(entry.FilePath)) return;
        if (_analysisConfig.ShellWhitelist.FilePaths
                .Any(f => f.Equals(entry.FilePath, StringComparison.OrdinalIgnoreCase))) return;
        _analysisConfig.ShellWhitelist.FilePaths.Add(entry.FilePath);
        _analysisConfig.Save();
        RefreshShellWhitelistTags();
        ApplyShellFilters();
    }

    private void ShellExtWhitelistVendor_Click(object sender, RoutedEventArgs e)
    {
        var entry = GetShellContextEntry(sender);
        if (entry == null || string.IsNullOrEmpty(entry.VendorName)) return;
        if (_analysisConfig.ShellWhitelist.VendorNames
                .Any(v => v.Equals(entry.VendorName, StringComparison.OrdinalIgnoreCase))) return;
        _analysisConfig.ShellWhitelist.VendorNames.Add(entry.VendorName);
        _analysisConfig.Save();
        RefreshShellWhitelistTags();
        ApplyShellFilters();
    }

    private static ShellExtensionEntry? GetShellContextEntry(object menuItemSender)
    {
        var menu = ((MenuItem)menuItemSender).Parent as ContextMenu;
        return (menu?.PlacementTarget as DataGrid)?.SelectedItem as ShellExtensionEntry;
    }
}

// ── Svc Whitelist VM ────────────────────────────────────────────

public sealed class SvcWhitelistEntryVm
{
    public string TypeDisplay { get; init; } = "";
    public string Value       { get; init; } = "";
    public Action OnRemove    { get; init; } = () => {};
}

// ── Shell Whitelist VM ──────────────────────────────────────────

public sealed class ShellWhitelistEntryVm
{
    public string TypeDisplay { get; init; } = "";
    public string Value       { get; init; } = "";
    public Action OnRemove    { get; init; } = () => {};
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
