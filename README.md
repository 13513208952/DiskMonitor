# DiskMonitor



Windows 11 磁盘 I/O 监控工具。长期运行于后台，记录每个进程对每块物理磁盘的每日读写量。

---

## 免责声明

本项目使用 [GLWT（Good Luck With That）公共许可证](LICENSE)。

作者本人也**完全不清楚**这份代码能不能正常工作。虽然在我的电脑上它能用，但是毕竟是用AI写的，我从来无法保证它能不能在你的电脑上运行。
该软件针对的是windows 11 professional系列系统，只针对美式英语与简体中文，而且界面完全针对简体中文，以后也不会做其他系统其他语言的适配。
由于涉及到windows服务相关领域，该软件存在一些基本风险，包括但不限于程序闪退，电脑蓝屏，cpu吃满等等，作者没有能力进行完善的测试和分析。
如果出了任何问题**请不要来找作者**。你可以自己找人帮忙分析，用AI修复，或者直接不安装这个软件。我乐意帮忙，但是我爱莫能助。
祝你好运。



---

## 功能

- **查看磁盘读写量**：记录每个进程对每块磁盘每日的读取和写入字节数
- **事件驱动，零轮询**：基于 ETW (Event Tracing for Windows) FileIO 事件，无 I/O 时 CPU 占用为零
- **长期记录**：SQLite 数据库存储历史数据，支持日期范围查询与 CSV 导出
- **便携发行版**：自带 .NET 9 运行时，无需额外安装，解压即用
- **多主题 UI**：Darkly、Superhero、Cosmo、Flatly、Journal、Litera 六套主题
- **异常分析**：基于历史数据的 I/O 异常告警，支持自定义阈值与排除规则
- **插件检测**：扫描系统注册的 Shell 扩展 DLL，显示厂商、创建/修改时间及幽灵路径，支持白名单过滤
- **服务监控**：枚举所有 svchost 托管服务及其 ServiceDll 路径与厂商，支持白名单与隐藏微软组件

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

## 异常分析

「异常分析」标签页包含三个子导航，均为手动加载，不自动刷新。

### IO 监控

基于全量历史数据运行分析引擎，输出超出阈值的进程告警。支持：

- 全局 / 逻辑卷 / 物理磁盘三级阈值配置（单位 GB）
- 按日期或日期范围排除特定日的数据
- 排除系统进程、explorer.exe 或自定义进程名

### 插件检测

扫描 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved`，列出所有注册的第三方外壳扩展 DLL。显示列：DLL 名称 / 厂商 / 创建时间 / 最终修改时间 / 幽灵标记（文件已不存在）/ 路径。

分析区块：最新加入的 5 个 / 最近修改的 10 个 / explorer.exe 近 7 天读写柱状图 / 幽灵路径列表。

白名单支持按厂商、单文件、目录或一键排除系统文件夹，持久化保存。

### 服务监控

枚举所有以 svchost.exe 托管的 Windows 服务，通过注册表读取各服务的真实 ServiceDll 路径，并通过文件版本信息获取厂商名称。显示列：PID / 服务名 / 厂商 / 显示名称 / ServiceDll 路径。

支持实时刷新（1 秒轮询）、隐藏微软组件、白名单（按厂商 / 服务名 / 目录），以及 svchost.exe 近 7 天读写柱状图。

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
| svchost 托管服务的 I/O 无法细分到 DLL 级 | 服务监控 Tab 提供人工排查辅助 |
| Shell 扩展 DLL 注入 explorer 的 I/O 归因到 explorer.exe | 插件检测 Tab 提供辅助判断 |

## 许可证

[GLWT(Good Luck With That) Public License](LICENSE)
