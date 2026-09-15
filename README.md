# HfsMounter — 在 Windows 资源管理器里直接浏览 Mac 硬盘（HFS+/HFSX，只读）

**HfsMounter** 把苹果 HFS+/HFSX 格式的移动硬盘（含 **Time Machine 备份盘**）挂载成
Windows 资源管理器里真实可浏览的**盘符 / 目录**，全程**只读**，绝不改动硬盘上的数据。
无需购买 Paragon HFS+ / MacDrive 等商业软件。

> **HfsMounter** mounts Apple HFS+/HFSX external drives — including **Time Machine
> backup disks** — as a real drive letter / directory in Windows Explorer.
> Strictly **read-only**. Built with C# (.NET 8) on top of the open-source
> [WinFsp](https://winfsp.dev) user-mode file system framework; HFS+ on-disk format
> parsing references the open-source [hfsfuse](https://github.com/0x09/hfsfuse) project.

## 功能特性 / Features

- ✅ HFS+ / HFSX（大小写敏感）卷，日志式与非日志式均可 / journaled & non-journaled
- ✅ 资源管理器直接浏览，支持 Unicode 文件名（中文等）/ full Unicode names
- ✅ **Time Machine 备份盘完整支持**：文件硬链接（`iNode*`）、目录硬链接（`Latest`）
  / file & directory hard links — required for Time Machine volumes
- ✅ 大文件多段 extents（extents-overflow B-tree）/ multi-extent large files
- ✅ 只读语义强制：所有写操作在文件系统层被拒绝 / all writes denied at FS level
- ✅ 挂载为盘符（`--letter`）或全局可见目录（默认 `C:\MacDisk`）/ drive letter or directory mount
- ✅ 无驱动开发、无需内核签名：WinFsp 用户态文件系统 / no kernel driver signing needed

## 环境要求 / Requirements

- Windows 10 / 11（x64）
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WinFsp](https://winfsp.dev) 2.1（开源，`winget install WinFsp.WinFsp`）
- 读取物理磁盘需要管理员权限（挂载脚本会自动提权）

## 从源码构建 / Build from source

```powershell
git clone https://github.com/phoenixsun2001/HfsMounter.git
cd HfsMounter/HfsMounter
dotnet build            # 或 dotnet publish -c Release -o publish
# 把 WinFsp 原生库放到 exe 旁边（或确保其在 PATH 中）：
copy "C:\Program Files (x86)\WinFsp\bin\winfsp-x64.dll" <输出目录>\
```

## 使用方法 / Usage

1. 插上移动硬盘
2. 双击 **`scripts/挂载Mac硬盘.bat`**（放到桌面更方便），UAC 弹窗点"是"
3. 等几秒，资源管理器会自动打开：
   - **X: 盘** —— 盘符方式（当前登录会话）
   - **C:\MacDisk** —— 目录方式（所有程序、所有会话都可见）
4. 用完想卸载：运行 **`scripts/卸载Mac硬盘.bat`**（或直接重启电脑）
5. 再次使用：重新运行挂载脚本即可（重启后挂载失效）

### 交付脚本 / Helper scripts

`scripts/` 目录里有两个一键脚本（脚本内的程序路径需按实际发布路径修改）：

- `挂载Mac硬盘.bat` —— 提权挂载 → 等待就绪 → `subst X: C:\MacDisk` → 自动打开资源管理器
- `卸载Mac硬盘.bat` —— 移除 X: 盘符并结束挂载进程

### 命令行参数 / Command line

```
HfsMounter.exe                          # 挂载到 C:\MacDisk（默认）
HfsMounter.exe --letter Z:              # 挂载为盘符（仅提权会话可见）
HfsMounter.exe --device \\.\PHYSICALDRIVE2   # 指定磁盘（默认自动扫描 0-15）
HfsMounter.exe --list                   # 终端打印根目录自测，不挂载
```

## 技术实现 / How it works

参考开源项目 [hfsfuse](https://github.com/0x09/hfsfuse)（C 语言 HFS+ FUSE 驱动）的
格式解析逻辑，用 C# (.NET 8) 实现了 HFS+ 只读解析引擎：
GPT 分区定位 → 卷头 → 目录/Extents B-tree → 文件 fork 读取。
再通过 [WinFsp](https://winfsp.dev)（开源用户态文件系统框架）把解析引擎暴露成
Windows 盘符。之所以不用内核驱动方案：那需要微软的驱动签名，个人无法合法加载；
WinFsp 用户态方案效果相同且免签名。

## 代码结构 / Code layout

```
HfsMounter/
  Program.cs        入口：GPT 扫描、参数解析、挂载与自测 (--list)
  RawDisk.cs        裸盘对齐读取（线程安全）
  HfsVolume.cs      卷头解析、块读取、extents 溢出链
  BTree.cs          B-tree 节点缓存、定位/枚举/查找
  Catalog.cs        目录记录解析、路径遍历、硬链接解析
  HfsFileSystem.cs  WinFsp FileSystemBase 实现（只读语义）
```

格式解析参考了开源项目 hfsfuse（https://github.com/0x09/hfsfuse ，其中的 NetBSD libhfs，
BSD 许可）的 C 实现，本仓库为独立 C# 实现，不包含其源码。

## 已知限制 / Known limitations

对浏览/拷贝用户数据无影响：

- 不解析 decmpfs 透明压缩文件（macOS 系统文件偶用，用户数据极少用）
- 不解析资源分支（NTFS 命名流），文件本体不受影响
- 时间戳按 UTC 直转，可能与 Mac 本地显示差几小时
- 若 Mac 端未正常弹出且日志未回放，个别文件可能读取报错

## 许可 / License

Copyright (c) 2026 phoenixsun2001。本仓库代码尚未附带开源许可证（默认保留所有权利），
如需使用请先开 issue 沟通。运行时依赖的 [WinFsp](https://winfsp.dev) 为 GPLv3+
FLOSS 例外许可，由用户自行安装，本仓库不包含、也不再分发其任何组件。
