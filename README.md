# HfsMounter

Windows 上以**只读**方式挂载 Mac 磁盘（HFS+ / HFSX）的小工具，基于 .NET 8 与 [WinFsp](https://winfsp.dev/) 用户态文件系统。

> **本分支相对上游的主要变化：从“手动双击脚本”改成了开机自启的常驻服务。**
> 服务随 Windows 启动、常驻后台轮询磁盘，插上 Mac 磁盘后自动挂载，拔盘自动卸载，全程不需要再手动运行任何脚本。

## 特性

- 只读挂载 HFS+ / HFSX：所有写操作（创建、删除、重命名、改大小、改属性、改 ACL）在文件系统层直接返回“拒绝访问”，不会动到 Mac 盘上的数据。
- **自动挂载**：服务后台按间隔扫描物理磁盘，发现含 Apple HFS+ 分区的 GPT 磁盘就挂上来：第一个卷挂在挂载根目录（默认 `C:\MacDisk`），其余依次为 `C:\MacDisk2`、`C:\MacDisk3`……
- **拔盘自动卸载**：连续多次健康检查失败即判定磁盘移除，卸载并清理挂载点。
- **稳定身份**：用 GPT 磁盘 GUID + 分区 GUID 识别卷，重新插拔不会挂错、不会重复挂载，挂载目录在重启后保持稳定。
- 支持 Time Machine 硬链接（`iNode*` / `dir_*`）、Extents Overflow、Unicode 文件名、日志式与非日志式卷。
- 单文件多用途：同一个 exe 既是服务，也能当前台守护进程、单次挂载、扫描诊断、自检。
- 全程可观测：滚动日志（按天、可配置保留天数）、`--status` 一次看全状态、内置 `--selftest` 免管理员自检。

## 环境要求

| 依赖 | 说明 |
| --- | --- |
| Windows 10 / 11 x64 | 服务模式依赖 SCM，目录挂载依赖 WinFsp |
| [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) | 框架依赖发布；用 `-SelfContained` 发布可免安装 |
| [WinFsp 2.1+](https://winfsp.dev/rel/) | 推荐用官方 MSI：`winfsp-2.1.25156.msi`（`winget install WinFsp.WinFsp` 在部分机器上会装成 2.2 的 SxS 布局但缺少启动器服务，见下） |
| 管理员权限 | 读物理磁盘 `\\.\PHYSICALDRIVEn` 需要；服务本身以 LocalSystem 运行，所以装完就不需要再提权 |

> WinFsp 2.x 采用 SxS 目录布局：DLL 位于 `%ProgramFiles(x86)%\WinFsp\SxS\sxs.<时间戳>\bin`（`bin` 是指向它的 junction），
> 不一定会出现在 `System32`。本服务会读注册表 `HKLM\SOFTWARE\WinFsp` 的 `SxsDir`/`InstallDir` 并按需 `LoadLibraryW`，
> 所以 `WinFsp: 已安装  loader=...` 这一行显示的是实际加载到的路径。

## 快速开始

```powershell
# 1. 编译发布（普通 PowerShell 即可）
cd src\scripts
.\build.ps1

# 2. 安装为开机自启服务（需要管理员；会自动检查/尝试安装 WinFsp）
.\install-service.ps1

# 可选：给自己的登录会话再映射一个盘符（默认取配置里的 linkerLetter，如 X:）
.\install-service.ps1 -LinkerLetter X:
```

也可以直接双击 `scripts\build.bat`、`scripts\install-service.bat`（会弹 UAC）。

装完之后：

- 插上 Mac 磁盘 → 几秒内自动出现在挂载根目录，无需任何操作。
- 打开资源管理器访问 `C:\MacDisk`（第二个磁盘会是 `C:\MacDisk2`，依此类推）。
- 服务随开机自启；崩溃会被 SCM 按 5s / 15s / 60s 退避重启。

如果 `winget install WinFsp.WinFsp` 装完 `--status` 仍报未安装，改用官方 MSI（无需卸载旧目录，MSI 会接管）：

```powershell
curl.exe -sSL -o "$env:TEMP\winfsp-2.1.25156.msi" `
  https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi
msiexec /i "$env:TEMP\winfsp-2.1.25156.msi" /qn /norestart
```

（`Invoke-WebRequest` 访问 GitHub 在部分网络下会报 `Authentication failed`，`curl.exe` 正常。）

## 命令一览

```text
HfsMounter.exe --install-service     安装并启动开机自启的常驻服务（需管理员）
HfsMounter.exe --uninstall-service   停止并删除服务（需管理员）
HfsMounter.exe --start | --stop      启动 / 停止服务
HfsMounter.exe --status              服务状态、检测到的磁盘、挂载点、最近日志
HfsMounter.exe --daemon              前台常驻（不装服务，便于调试，Ctrl+C 退出）
HfsMounter.exe --probe               只扫描磁盘，报告可挂载的 HFS+ 分区（需管理员）
HfsMounter.exe --selftest            内置自检（不需要管理员、不碰真实磁盘）
HfsMounter.exe --version             版本号
```

兼容旧用法（单次挂载，进程退出即卸载，适合临时看一眼）：

```text
HfsMounter.exe                              挂载到 C:\MacDisk
HfsMounter.exe --device \\.\PHYSICALDRIVE1  指定磁盘
HfsMounter.exe --mount-dir D:\Mac           指定挂载目录
HfsMounter.exe --letter Z:                  直接挂成盘符（仅当前会话可见）
HfsMounter.exe --list                       打印目录树自测，不挂载
```

盘符联动（把挂载目录映射成盘符，等效 `subst`，跨重启保持）：

```text
HfsMounter.exe --linker X: C:\MacDisk --watch   前台持续维护映射
HfsMounter.exe --linker-install X: C:\MacDisk   创建登录自启入口（计划任务 HfsMounter-DriveLetter）
HfsMounter.exe --linker-uninstall               删除自启入口
```

`--linker-install` 会先尝试创建计划任务；**没有管理员权限时自动退回**当前用户的“启动”文件夹
（`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\HfsMounter 盘符联动.cmd`），
效果相同，只是仅对该用户生效。映射在挂载目录就绪后才出现，拔盘后自动收回，不会留一个空的 `X:`。
不想要自动打开资源管理器就把配置里的 `autoOpenExplorer` 设为 `false` 后再重新 `--linker-install`。

## 配置

配置文件默认在 `%ProgramData%\HfsMounter\config.json`，首次运行自动生成。改完重启服务生效（`--stop` / `--start`）。

```json
{
  "mountRoot": "C:\\MacDisk",
  "pollSeconds": 3,
  "maxMounts": 4,
  "removalGracePolls": 2,
  "linkerLetter": "X:",
  "autoOpenExplorer": true,
  "volumeLabel": null,
  "minLogLevel": "Info",
  "logRetainDays": 14
}
```

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `mountRoot` | `C:\MacDisk` | 第一个卷的挂载目录；后续卷用 `<mountRoot>2`、`<mountRoot>3`……（最多到 26） |
| `pollSeconds` | `3` | 磁盘轮询间隔（秒），最小 1 |
| `maxMounts` | `4` | 同时挂载的卷上限 |
| `removalGracePolls` | `2` | 连续多少次健康检查失败才判定拔盘，避免瞬时 IO 抖动误卸载 |
| `linkerLetter` | `X:` | 盘符联动使用的盘符（仅 `--linker*` 相关命令使用） |
| `autoOpenExplorer` | `true` | 盘符联动时是否自动打开资源管理器 |
| `volumeLabel` | `null` | 覆盖卷标，`null` 表示用卷上记录的标签 |
| `minLogLevel` | `Info` | `Debug` / `Info` / `Warn` / `Error` |
| `logRetainDays` | `14` | 日志保留天数，启动时自动清理过期文件 |

数据目录（配置、`state.json`、日志）优先用 `%ProgramData%\HfsMounter`；若当前账户不可写则依次回退到 `%LOCALAPPDATA%\HfsMounter`、`%TEMP%\HfsMounter`、`<当前目录>\.hfsmounter`。也可以显式指定：`--data-dir D:\HfsMounterData`。

## 日志

- 位置：`%ProgramData%\HfsMounter\logs\hfsmounter-yyyyMMdd.log`
- 格式：`2026-01-01 12:00:00.000 [I] 消息`，服务进程与命令行工具写同一个目录。
- `--status` 末尾会回显服务端最近 12 行日志（不含本次命令自身的输出）。
- 服务跑在 LocalSystem 下，没有控制台；所有 `Console.WriteLine`（包括底层解析代码的提示）都会被重定向进日志文件。

## 工作原理

1. 服务每 `pollSeconds` 秒枚举 `\\.\PHYSICALDRIVE0..31`，读取 GPT，比对分区类型 GUID `48465300-0000-11AA-AA11-00306543ECAC`（Apple HFS+）。
2. 读分区头 1024 偏移处的 HFS+ 卷头，校验签名/块大小，拿到块大小、总块数、是否 HFSX、是否日志式。
3. 新卷 → 依次尝试「上次用过的目录 → `mountRoot` → `mountRoot2`…」，建目录（`RawDisk` + `HfsVolume` + `HfsFileSystem`），先 `Preflight` 探测挂载点是否可用，再 `Mount`（`Synchronized = true`，规避 BTree 缓存的并发问题）。
4. 已挂载卷 → 每轮读一次卷头做健康检查；磁盘从枚举结果里消失、或连续 `removalGracePolls` 次读失败 → `Unmount` 并清理挂载点。
5. 挂载点记录在 `state.json`，重启后优先复用同一目录，资源管理器里的路径不会跳。

## 目录结构

```text
src/
  HfsMounter/
    Program.cs        命令行/服务入口、动词分发、诊断输出
    ServiceRuntime.cs SCM 交互：安装、卸载、启动、停止、运行服务、状态查询
    Supervisor.cs     常驻主循环：轮询、挂载、健康检查、卸载
    DiskScanner.cs    物理磁盘枚举 + GPT 解析 + 候选卷筛选
    MountedVolume.cs  单个挂载卷的生命周期
    Gpt.cs            GPT 分区表解析 + CRC32
    HfsProbe.cs       HFS+ 卷头解析
    AppConfig.cs      配置、数据目录、挂载状态持久化
    Log.cs            文件日志 + 控制台重定向
    Interop.cs        DefineDosDevice 盘符映射等 Win32 封装
    Linker.cs         用户会话盘符联动（含登录自启任务）
    SelfTest.cs       合成镜像自检
    RawDisk.cs        裸盘按扇区对齐读取
    HfsVolume.cs      卷头、Fork、Extents
    BTree.cs          B 树节点解析与定位
    Catalog.cs        目录记录、硬链接解析
    HfsFileSystem.cs  WinFsp 只读文件系统实现
  scripts/
    build.ps1 / build.bat
    install-service.ps1 / install-service.bat
    uninstall-service.ps1 / uninstall-service.bat
    status.ps1 / status.bat
```

## 卸载

```powershell
# 管理员
cd src\scripts
.\uninstall-service.ps1              # 停服务、删服务、删盘符任务
.\uninstall-service.ps1 -RemoveFiles # 顺带删掉 Program Files\HfsMounter
```

`%ProgramData%\HfsMounter`（配置与日志）会保留，需要时手动删除。

## 故障排查

| 现象 | 排查 |
| --- | --- |
| 插盘没反应 | `.\status.ps1` 看“当前检测到的 HFS+ 分区”是否为 0；为 0 时再看是否提示“有磁盘无法访问” |
| `--probe` 说权限不足 | 服务模式以 LocalSystem 运行没有此问题；手动执行 `--probe` 需管理员终端 |
| `WinFsp: 未安装` | `winget install WinFsp.WinFsp`，然后重启服务 |
| 服务的日志里说找不到 `winfsp-x64.dll` | WinFsp 2.x 用 SxS 目录布局（`Program Files (x86)\WinFsp\SxS\sxs.*\bin`）。服务会依次尝试程序目录、System32、注册表里的 `SxsDir`/`InstallDir`，正常无需手工拷贝；若全部失败，重新运行 WinFsp 安装包并重启 |
| 服务装了但没挂载 | 看日志里 `挂载失败` / `挂载点 …` 相关行；确认 `mountRoot` 目录可写、没被别的程序占用 |
| 日志里出现 `挂载点 C:\MacDisk 已被占用` | 该目录已存在且非空（或有别的程序在用），服务会自动改用 `C:\MacDisk2`、`C:\MacDisk3`……；也可以手工删掉这个空目录或改 `mountRoot` |
| 挂载目录里是空的 | 磁盘可能是 APFS；本工具只支持 HFS+/HFSX |
| 盘符 X: 没出现 | 盘符联动是每用户入口：`HfsMounter.exe --linker-install X: C:\MacDisk`（无管理员权限会自动写“启动”文件夹），或重新登录 |

> 目录挂载点由 WinFsp 自己创建（内部用 `FspCreateDirectoryFileW` + reparse point），程序不会预先 `mkdir`；
> 服务启动或重启时会先清掉上次异常退出残留的 reparse point / 空目录，再挂载。

## 实测记录

以下结果来自 Windows 11（10.0.26200）+ .NET 8 + WinFsp 2.1.25156 + 一块 1 TB USB 机械盘（GPT，第二个分区为 HFS+/HFSX 日志式）：

- 服务 `sc qc HfsMounter`：`START_TYPE = AUTO_START`（`DelayedAutoStart=0`，非延迟启动），账户 `LocalSystem`；
  **整机重启后无需登录、无需任何交互即可自动挂载**。
- 服务启动到挂载完成 0.5 秒内；插盘后在下一次轮询（默认 3 秒）内自动挂载到 `C:\MacDisk`，卷标取自卷上的名字（本例 `seagate`）。
- 目录列举、文件读取、Time Machine 快照浏览（`Backups.backupdb/<机器名>/<时间戳>` 的目录硬链接与文件硬链接）均正常，
  3 个快照列举耗时 73 ms；写入/删除被拒绝（`Access to the path ... is denied`）。
- 强杀服务进程后由 SCM 在约 5 秒内自动拉起，18 秒内重新挂载（旧挂载点的残留 reparse point 被自动清理），期间目录访问零阻塞。
- 拔盘/卸载逻辑：连续 `removalGracePolls` 次健康检查失败才卸载，避免 USB 瞬时抖动误伤。
- 盘符联动：登录后 `X:` 自动出现并指向 `C:\MacDisk`，拔盘时自动收回；无管理员权限时通过「启动」文件夹实现，不弹 UAC。

## 与上游版本的差异

- 新增 Windows 服务模式（`--service` / `--install-service`），登录前即随系统启动（`AUTO_START`），失败由 SCM 按 5s / 15s / 60s 退避重启。
- 新增自动检测与自动挂载、拔盘自动卸载、健康检查、挂载点稳定性（`state.json`）。
- 新增日志框架、配置系统、`--status` / `--probe` / `--version` 诊断命令。
- 新增 `--selftest`：用合成 GPT/HFS 镜像验证解析链路，不需要管理员、不接触真实磁盘。
- 新增盘符联动 `--linker*`，保留旧脚本“映射成 X: 并自动打开”的体验；登录自启用计划任务，无管理员权限时自动回退到「启动」文件夹。
- 旧的两个 bat（写死作者本机路径）已被 `scripts\*.bat` + `*.ps1` 取代。
- 修正 `HfsFileSystem` 的卷标硬编码，卷标可通过配置覆盖。

## 已知限制

- 只读；不支持写入。
- 不解析 `decmpfs` 压缩文件（macOS 上标记为“压缩”的文件读出来是压缩后的原始数据）。
- 不解析资源分支（resource fork）、不解析扩展属性。
- 时间戳按 HFS+ 的 UTC 纪元直接换算，不处理本地时区展示差异。
- Mac 未正常弹出、日志式卷的 journal 未回放时，个别文件可能读到过期内容。
- APFS 卷不支持。

## 许可

沿用上游的 MIT 许可。
