# DiskMonitor — 项目文档

## 项目概述

Windows 11 专业版磁盘 I/O 监控工具。以极低性能开销长期运行于后台，记录每个进程对每块物理磁盘的每日读写总量，供用户分析是否存在异常的"扫盘"行为。

**目标平台**：Windows 11 专业版  
**支持语言**：中文、英文  
**合法性定位**：仅使用公开文档化 Windows API，无内核驱动，无进程注入，无内存读取

---

## 解决方案结构

```
DiskMonitor.sln
├── DiskMonitor.Core          # 核心逻辑类库（ETW、聚合、SQLite）
├── DiskMonitor.Service       # Windows Service 宿主（薄壳）
├── DiskMonitor.Frontend      # WPF 桌面前端
└── DiskMonitor.DebugHost     # 控制台调试宿主（开发专用）
```

### 依赖关系

```
Service ──┐
Frontend ─┼──▶ Core
DebugHost ┘
```

---

## 技术栈

| 组件 | 技术 |
|------|------|
| 语言 / 运行时 | C# / .NET 9 |
| ETW 采集 | Microsoft.Diagnostics.Tracing.TraceEvent |
| 数据库 | SQLite（Microsoft.Data.Sqlite），WAL 模式 |
| 日志 | Serilog（文件 sink + Windows EventLog sink）|
| 前端框架 | WPF |
| 服务宿主 | Microsoft.Extensions.Hosting.WindowsServices |

---

## 监控目标与动机

扫盘行为有两类危害，两者都需要被量化：

1. **硬件损耗**：程序每日产生数十 GB I/O，远超正常水平，加速硬盘寿命消耗
2. **隐私越权**：程序安装在 C 盘，却大量读取 D 盘（用户私人资料盘）——暗示在扫描用户私人文件进行商业画像

仅看总量不足以区分这两类，必须同时记录**进程 × 盘符**的组合才能暴露越权访问模式。

---

## 核心架构决策

### 数据来源：FileIO ETW 事件（事件驱动，非轮询）

使用 `Microsoft-Windows-Kernel-FileSystem` Provider，**不使用** DiskIO Provider。

**不用 DiskIO 的原因**：Windows 写回缓存机制导致 DiskIO 写入事件归因到 System 进程，原始写入进程信息丢失。

**ETW 是事件驱动而非轮询**：内核在 I/O 发生时主动将事件写入共享内存缓冲区，消费者线程阻塞等待，无 I/O 时完全休眠，CPU 占用为零。每个事件处理仅需两次哈希表查找 + 一次整数累加（约 10–20 纳秒），即使每秒百万事件也仅占用 1–2% CPU。

**ETW 事件处理流程**：
```
进程调用 ReadFile() / WriteFile()
    ↓
内核发出 FileIo/Read 或 FileIo/Write 事件
    ↓ 事件含：ProcessId + FileObject指针 + IoSize
内存表：FileObject → 文件路径（由 FileIo/Create 事件建立）
    ↓
文件路径 → 卷标 → 物理磁盘（启动时建立卷映射表）
PID → 进程名 + 完整路径（进程事件维护）
    ↓
内存计数器[(进程名, 卷GUID)] += 字节数
```

**ETW FileIO 路径格式**：`FileIo/Create` 事件中的 `FileName` 字段使用 **NT 命名空间格式**（`\Device\HarddiskVolume3\Users\foo\bar.txt`），而非 Win32 路径（`C:\...`）。实现中通过启动时对每个卷调用 `QueryDosDevice("C:", ...)` 建立 `\Device\HarddiskVolumeN` → `VolumeInfo` 的反查表，`VolumeInfo.DevicePath` 字段存储该映射键。

**服务启动前已打开的文件**：缺少对应 FileIo/Create 事件，FileObject 无法映射路径，其 I/O 统计到 `[Unknown]` 条目，随时间推移占比迅速下降至可忽略。

**不采集**：文件路径、文件名、具体操作内容

### 磁盘与卷的标识策略

针对 U 盘、移动硬盘等插拔场景，单一标识符不可靠，同时记录四个字段：

| 字段 | 稳定性 | 用途 |
|------|--------|------|
| 盘符（C:、D:） | 不稳定，插拔可变 | 用户熟悉，供参考 |
| 卷标（用户命名） | 相对稳定 | 人类可读标识 |
| 卷 GUID | 永久稳定，插拔不变 | 跨会话唯一匹配键 |
| 物理磁盘编号 + 型号 | 不稳定，插拔可变 | 硬件损耗分析 |

卷到物理磁盘的映射通过 `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` 在服务启动时建立，并在检测到卷挂载/卸载事件时动态更新。

### RAID / 跨盘卷检测

服务启动时若发现任何卷跨越多块物理磁盘（`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` 返回多个 Extent），则：
- 服务**拒绝启动**
- 前端显示明确警告，列出问题卷的盘符和卷标
- 原因：无法准确将 I/O 归因到单一物理磁盘

### 移动存储设备处理

监听卷卸载事件（设备拔出）：在卸载前**立即强制将该卷的内存缓冲数据写入 SQLite**，防止未落库数据随设备消失而丢失。

### 聚合策略

内存中维护：`Dictionary<(进程名, 进程路径, 卷GUID), (读取字节累计, 写入字节累计)>`

每 5 分钟批量写入 SQLite，不是每事件写入。每日午夜滚动，清零当日计数器并落库日报。

### 进程追踪

同一 ETW 会话同时订阅进程创建/退出事件，维护 `PID → {名称, 完整路径, 启动时间}` 映射表。服务启动时通过 `QueryFullProcessImageName` 扫描已运行进程做初始化快照。

### 服务心跳

Service 每分钟向 SQLite 的 `service_status` 表写入时间戳。Frontend 轮询此表判断服务是否存活，无需额外 IPC 机制。

---

## 数据库结构（SQLite）

预计数据量：150–250 个唯一进程/天，5 年累计约 50–150 MB，SQLite 完全胜任。

```sql
-- 每日 I/O 聚合记录
CREATE TABLE daily_io (
    id           INTEGER PRIMARY KEY,
    date         TEXT NOT NULL,         -- YYYY-MM-DD
    process_name TEXT NOT NULL,         -- chrome.exe
    process_path TEXT NOT NULL,         -- C:\Program Files\...
    drive_letter TEXT NOT NULL,         -- C:（不稳定，仅参考）
    volume_label TEXT NOT NULL DEFAULT '',  -- 用户命名的卷标，可为空
    volume_guid  TEXT NOT NULL,         -- {xxxxxxxx-...}，跨插拔稳定
    disk_number  INTEGER NOT NULL,      -- 0, 1, 2...（不稳定，仅参考）
    disk_model   TEXT NOT NULL DEFAULT '',  -- Samsung SSD 870 EVO 1TB
    read_bytes   INTEGER NOT NULL DEFAULT 0,
    write_bytes  INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_daily_io_date        ON daily_io(date);
CREATE INDEX idx_daily_io_volume_guid ON daily_io(volume_guid);
CREATE INDEX idx_daily_io_process     ON daily_io(process_name);

-- 服务心跳
CREATE TABLE service_status (
    id         INTEGER PRIMARY KEY,
    updated_at TEXT NOT NULL            -- ISO 8601 timestamp
);

-- 进程历史（用于跨日查询时补全路径）
CREATE TABLE process_history (
    id         INTEGER PRIMARY KEY,
    pid        INTEGER NOT NULL,
    name       TEXT NOT NULL,
    path       TEXT NOT NULL,
    start_time TEXT NOT NULL,
    end_time   TEXT                     -- NULL 表示进程仍在运行
);

-- 卷信息快照（记录每次挂载时的卷元数据）
CREATE TABLE volume_snapshots (
    id           INTEGER PRIMARY KEY,
    volume_guid  TEXT NOT NULL,
    drive_letter TEXT NOT NULL,
    volume_label TEXT NOT NULL DEFAULT '',
    disk_number  INTEGER NOT NULL,
    disk_model   TEXT NOT NULL DEFAULT '',
    first_seen   TEXT NOT NULL,
    last_seen    TEXT NOT NULL
);
CREATE UNIQUE INDEX idx_volume_guid ON volume_snapshots(volume_guid);
```

---

## CSV 导出格式

```
date,process_name,process_path,drive_letter,volume_label,volume_guid,disk_number,disk_model,read_bytes,write_bytes
2026-05-24,chrome.exe,C:\Program Files\Google\Chrome\Application\chrome.exe,C:,System,{3f2504e0-4f89-11d3-9a0c-0305e82c3301},0,Samsung SSD 870 EVO 1TB,4831838208,1073741824
2026-05-24,SomeApp.exe,C:\Program Files\SomeApp\SomeApp.exe,D:,MyData,{7c3a9e1b-2d4f-4a8c-b123-456789abcdef},1,WD Blue 2TB,53687091200,0
2026-05-24,[System],,C:,System,{3f2504e0-4f89-11d3-9a0c-0305e82c3301},0,Samsung SSD 870 EVO 1TB,2147483648,536870912
2026-05-24,[Unknown],,C:,System,{3f2504e0-4f89-11d3-9a0c-0305e82c3301},0,Samsung SSD 870 EVO 1TB,104857600,0
```

列说明：
- `drive_letter`：盘符，插拔后可能变化，仅供参考
- `volume_label`：用户设置的卷标，可为空字符串
- `volume_guid`：永久稳定的卷唯一标识，跨插拔不变，是跨会话匹配同一存储设备的可靠键
- `disk_number`：物理磁盘编号，插拔后可能变化，仅供参考
- `disk_model`：物理磁盘型号，便于用户识别硬件
- `[System]`：无法归因到具体进程的 I/O
- `[Unknown]`：服务启动前已打开的文件产生的 I/O（FileObject 无法映射路径）

---

## 已知限制

| 限制 | 影响 | 处理方式 |
|------|------|----------|
| ETW 高负载丢事件 | 数据为下界，非精确值 | 已接受，量级判断不受影响 |
| 内核会话上限 8 个 | 极少情况下会话冲突 | 单会话多 Provider，加冲突恢复逻辑 |
| 部分 System I/O 无法归因 | 归入 `[System]` 条目 | 用户已关闭 BitLocker，AV 加白名单 |
| 服务启动前已打开文件的 I/O | 归入 `[Unknown]` 条目 | 随时间推移占比迅速下降，可接受 |
| RAID / 跨盘卷 | 无法归因物理磁盘 | 检测到即拒绝服务启动，前端警告 |

---

## 调试方法

### 日常开发（90% 场景）

以**管理员身份**运行 `DiskMonitor.DebugHost`，ETW 逻辑直接在控制台进程中运行，可使用 Visual Studio 完整调试（断点、Watch、即时窗口）。

### 服务生命周期测试

使用 Windows Sandbox（`.wsb` 配置文件）：
- 本机编译 → 二进制通过映射文件夹传入 Sandbox
- 启动脚本自动安装服务、运行、检查状态
- 日志写回映射文件夹供本机读取
- 关闭 Sandbox 完全清理，主机零影响

**注意**：Visual Studio 无法跨越 Sandbox 独立内核附加调试器。

### 服务内调试（必要时）

在 Service 启动代码中加入：
```csharp
#if DEBUG
    System.Diagnostics.Debugger.Launch();
#endif
```

---

## 服务管理命令

```powershell
# 安装
sc.exe create DiskMonitor binPath="C:\diskmonitor\bin\DiskMonitor.Service.exe" start=auto

# 启动 / 停止
sc.exe start DiskMonitor
sc.exe stop DiskMonitor

# 卸载
sc.exe delete DiskMonitor
```

---

## 可靠性机制

- **SCM 崩溃恢复**：服务崩溃自动重启，最多 3 次
- **SQLite WAL 模式**：崩溃不损坏历史数据
- **批量写入**：5 分钟一次，降低磁盘 I/O
- **完全卸载**：前端提供一键卸载，清除服务注册、注册表项、可执行文件（数据库询问用户是否保留）

---

## 开发环境

- OS：Windows 11 Pro
- SDK：.NET 9
- Shell：管理员 PowerShell（Claude Code 运行环境）
- Git：2.52.0
- IDE：VS Code（可用）+ Visual Studio Enterprise 2026（可用）

---

## 实现注意事项（避免重复踩坑）

- **LibraryImport + StringBuilder 不兼容**：`LibraryImport` 属性不支持 `StringBuilder` 参数（SYSLIB1051），全部改用传统 `DllImport`
- **ETW FileIO 路径是 NT 格式**：`FileIo/Create` 的 `FileName` 是 `\Device\HarddiskVolumeN\...`，不是 `C:\...`。通过 `QueryDosDevice` 建立设备路径反查表解决。不处理此问题会导致 100% I/O 归入 [Unknown]
- **午夜滚动的闭包陷阱**：`Task.Run` 中如果捕获可变字段引用而非局部变量，可能拿到已更新的新值。`IoAggregator.RolloverUnlocked` 中必须先 `var oldDate = _currentDate` 再覆写字段
- **WindowsServices 包版本**：`Microsoft.Extensions.Hosting.WindowsServices` 必须固定为 `9.0.16`，最新版（10.x）会与 .NET 9 项目产生降级冲突
- **Microsoft.Data.Sqlite 多语句**：`ExecuteNonQuery` 会执行分号分隔的所有语句，PRAGMA 批处理和 DELETE+INSERT 组合均正确执行
- **VolumeDiskExtents 缓冲区大小**：传 `Marshal.SizeOf<VolumeDiskExtents>()` = 32 字节，刚好容纳 1 个 Extent。返回 `ERROR_MORE_DATA(234)` 即表示有 2+ Extents = RAID
- **WPF FindResource 崩溃陷阱**：`FindResource("Green")` 等找不到键时抛 `ResourceReferenceKeyNotFoundException`。若在 `async void` 定时器回调中抛出，会 crash 整个进程（表现为"运行一段时间后自动关闭"）。状态点颜色资源（Green/Red/Orange）需定义在 App.xaml 静态资源中，不可只在主题文件里
- **DataGrid 字符串排序**：`TotalDisplay`/`ReadDisplay`/`WriteDisplay` 是格式化字符串，直接排序按字典序（"4.50 GB" < "456.7 MB"）。必须在 `DataGridTextColumn` 上加 `SortMemberPath="TotalBytes"` 等指向原始 `long` 字段
- **explorer.exe /select 极慢**：用 `explorer.exe /select,"path"` 打开文件位置会触发 COM/DDE 进程间握手，可能阻塞数秒。应改为 `Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true })` 直接打开目录
- **完全卸载必须读注册表路径**：卸载前通过 `HKLM\SYSTEM\CurrentControlSet\Services\DiskMonitor\ImagePath` 读取实际注册的二进制路径，才能正确删除可执行文件目录（不受安装路径变化影响）。还需清理 Event Log 源：`reg delete HKLM\...\EventLog\Application\DiskMonitor /f`

---

## 便携包发布

### 构建便携包

```powershell
.\publish.ps1
```

输出到 `publish\DiskMonitor\`，结构：
```
publish\DiskMonitor\
├── DiskMonitor.Frontend.exe   # 双击运行
├── (前端依赖，含完整 .NET 9 运行时)
└── service\
    ├── DiskMonitor.Service.exe
    └── (服务依赖，含完整 .NET 9 运行时)
```

- 前端查找服务 exe 顺序：`service\` 子目录 → 同级目录 → 开发布局
- 两个子项目各自 self-contained，运行时不共享（避免 DLL 冲突）

### GitHub 仓库

`https://github.com/13513208952/DiskMonitor`

发布新版本：
```powershell
.\publish.ps1
7z a DiskMonitor-vX.Y.Z-win-x64.7z .\publish\DiskMonitor\
gh release create vX.Y.Z DiskMonitor-vX.Y.Z-win-x64.7z --title "vX.Y.Z" --notes "变更说明"
```

---

## 前端功能清单（v1.0.0）

- **今日 Tab**：实时 I/O 统计、3 张统计卡、DataGrid（进程/盘符/卷标/磁盘型号/读写/合计）
- **历史 Tab**：日期范围查询、CSV 导出
- **设置 Tab**：主题切换（6 套）、服务安装/启停/卸载、完全卸载
- **右键菜单**：打开文件所在目录（`UseShellExecute` 直开目录）、复制进程路径
- **DataGrid 排序**：所有列可点击正/倒序，读写/合计按字节数排序（非字符串）
- **主题持久化**：保存到 `%AppData%\DiskMonitor\theme.txt`
- **服务状态轮询**：10 秒定时器，定时器回调有 try/catch 防崩溃

---

## 技术边界与后续路线图

### 归因精度的永久天花板

以下盲区在不使用内核驱动的前提下**无法根本解决**，属于架构性约束：

| 盲区 | 原因 | 归入 |
|------|------|------|
| 内存映射文件读写 | 走页错误 → Memory Manager，不生成 FileIO 事件 | `[System]` |
| Shell 扩展 DLL 注入 explorer/dllhost | I/O 归因到宿主进程 | `explorer.exe` / `dllhost.exe` |
| svchost 托管服务 DLL | 多服务共享同一进程，无法区分到代码级 | `svchost.exe` |
| 内核驱动代劳的 I/O | Ring 0 执行，归因到 SYSTEM 上下文 | `[System]` |

内核驱动方案超出当前协作模型的可靠边界（见下文），不予实施。

### 为什么不做内核驱动

内核驱动出错直接蓝屏，崩溃转储需要 WinDbg + 内核调试经验才能解读。用户无法将蓝屏信息转化为可定位问题的描述，反馈回路断裂，无法迭代修复。这是协作条件的限制，不是技术知识的限制。

### 可行的后续增强方向

#### 归因能力提升（用户态局部解）

**svchost 服务关联**
查询 SCM 建立 `svchost PID → 服务名列表` 映射，将"svchost.exe"细化为"是这N个服务之一"。
实现位置：Core 层新增 `ServiceTracker` 类，纯用户态。

**Shell 扩展注册表扫描**
读取 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved`，
在前端单独展示所有第三方外壳扩展 DLL 及厂商，让用户知道"谁的 DLL 常驻在 explorer.exe 里"。

**DLL 加载事件 ETW 关联**
在现有 ETW 会话中追加订阅 `Microsoft-Windows-Kernel-Process` provider 的 `ImageLoad` 事件，
记录 DLL 加载时间点，与 FileIO 时间线交叉分析，发现"加载 VendorX.dll 后30秒内大量 I/O"的关联。

**网络流量关联**
订阅 ETW 网络 provider，检测高 I/O 时段是否紧跟网络上传，判断数据是否被外送。

#### 分析与呈现层

- 趋势图表：按时间轴展示各进程 I/O 变化，异常峰值可视化
- 异常告警：进程 I/O 超过历史均值 N 倍时推送系统托盘通知
- 厂商归因：根据 exe 路径推断所属公司，按公司维度聚合 I/O
- 进程树视图：基于已有 Process/Start ETW 事件数据，展示进程父子关系
- 定时摘要：每日/每周 I/O 报告

#### 工程质量

- 正式安装程序（MSIX / WiX）替代手动 `sc.exe`
- 多语言支持（WPF 资源文件 i18n，优先中/英）
- Windows 版本适配（跟进 ETW API 变更）

### 隐藏 vs 监控的不对称性（背景）

在 Windows 上，有效隐藏扫盘的技术门槛极低（公开 API + 普通安装权限 + 任何开发者），
而有效监控并完整归因的成本呈断崖式跳升（内核驱动 + EV 签名 + 微软审批 + 持续维护）。
这一不对称嵌入在 Windows 扩展性架构的历史设计中，被商业生态利益结构进一步强化。
DiskMonitor 的价值在于在用户态极限处维持可见性，为舆论和监管行动提供证据基础。
