# DiskMonitor

Windows 11 磁盘 I/O 监控工具。以极低性能开销长期运行于后台，记录每个进程对每块物理磁盘的每日读写总量，帮助用户发现异常的"扫盘"行为。

## 动机

现代软件频繁大量读写用户文件进行商业分析，造成两类危害：

1. **硬件损耗**：I/O 量远超正常水平，加速硬盘寿命消耗
2. **隐私越权**：程序安装在 C 盘却大量读取 D 盘（用户私人资料盘），暗示未经授权扫描用户私人文件进行商业画像

DiskMonitor 通过长期记录**进程 × 盘符**维度的 I/O 数据，将异常行为以数据形式呈现。

## 特性

- **事件驱动，零轮询**：基于 ETW (Event Tracing for Windows) 的 FileIO 事件，内核主动推送，无 I/O 时 CPU 占用为零
- **精确归因**：记录到进程级别，区分每块磁盘，排除 DiskIO 事件"归因到 System"的干扰
- **低侵入性**：仅使用公开文档化 Windows API，无内核驱动，无进程注入，无内存读取，无网络上传
- **长期记录**：SQLite 数据库，WAL 模式，5 年数据约 50–150 MB
- **便携发行版**：无需安装 .NET 运行时，解压即用
- **多主题 UI**：Darkly、Superhero、Cosmo、Flatly、Journal、Litera 六套主题

## 系统要求

- Windows 11 专业版（需要管理员权限运行服务）
- x64 架构

## 使用方法

### 首次安装

1. 下载并解压发行版压缩包
2. 运行 `DiskMonitor.Frontend.exe`
3. 进入「设置」→「安装服务」（需要 UAC 提权）
4. 服务安装后自动随系统启动，开始采集数据

### 便携使用

- 前端可随时移除，**服务会继续在后台运行**
- 将新版前端复制到任意目录运行，可自动识别并接管已安装的旧服务
- 数据库位于 `%ProgramData%\DiskMonitor\diskmonitor.db`，与前端位置无关

### 卸载

进入「设置」→「完全卸载」，可选择是否保留历史数据库。

## 技术架构

```
DiskMonitor.sln
├── DiskMonitor.Core      # ETW 采集、聚合、SQLite（类库）
├── DiskMonitor.Service   # Windows Service 宿主
├── DiskMonitor.Frontend  # WPF 桌面前端
└── DiskMonitor.DebugHost # 控制台调试宿主
```

| 组件 | 技术 |
|------|------|
| 语言 / 运行时 | C# / .NET 9 |
| ETW 采集 | Microsoft.Diagnostics.Tracing.TraceEvent |
| 数据库 | SQLite (Microsoft.Data.Sqlite)，WAL 模式 |
| 前端 | WPF |
| 服务宿主 | Microsoft.Extensions.Hosting.WindowsServices |

## 已知限制

| 限制 | 影响 |
|------|------|
| ETW 高负载下可能丢少量事件 | 数据为下界，量级判断不受影响 |
| 服务启动前已打开文件的 I/O | 归入 `[Unknown]`，随时间下降 |
| RAID / 跨盘卷 | 检测到即拒绝启动并提示 |
| 防病毒软件 I/O | 建议将服务加入白名单 |

## 许可证

[GLWT(Good Luck With That) Public License](LICENSE)
