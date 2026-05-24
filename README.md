# DiskMonitor

> 随缘监控，能跑就行，出了事别找我，我也不知道该怎么办。

Windows 11 磁盘 I/O 监控工具。长期运行于后台，记录每个进程对每块物理磁盘的每日读写量。

---

## 免责声明

本项目使用 [GLWT（Good Luck With That）公共许可证](LICENSE)。

作者本人也**完全不清楚**这份代码能不能正常工作。它可能跑得好好的，也可能什么都监控不到，这两种以外没有第三种可能。

如果出了任何问题——数据丢失、磁盘冒烟、电脑消失——**请不要来找作者**。作者已经用尽了自己全部的技术储备来写这个项目，实在是爱莫能助。使用即代表你已充分理解"自负风险"的深刻含义，并做好了心理准备。

> 佛系监控，读写几何，皆是玄学。

---

## 功能

- **查看磁盘读写量**：记录每个进程对每块磁盘每日的读取和写入字节数
- **事件驱动，零轮询**：基于 ETW (Event Tracing for Windows) FileIO 事件，无 I/O 时 CPU 占用为零
- **长期记录**：SQLite 数据库存储历史数据，支持日期范围查询与 CSV 导出
- **便携发行版**：自带 .NET 9 运行时，无需额外安装，解压即用
- **多主题 UI**：Darkly、Superhero、Cosmo、Flatly、Journal、Litera 六套主题

## 系统要求

- Windows 11 专业版，x64
- 安装服务需要管理员权限

## 使用方法

### 首次安装

1. 下载并解压发行版压缩包
2. 运行 `DiskMonitor.Frontend.exe`
3. 进入「设置」→「安装服务」（需要 UAC 提权）
4. 服务安装后自动随系统启动，开始记录数据

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

## 许可证

[GLWT(Good Luck With That) Public License](LICENSE) — 祝你好运，真的。
