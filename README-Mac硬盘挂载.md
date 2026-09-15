# Mac 硬盘只读挂载工具（HfsMounter）

让 Windows 资源管理器直接浏览苹果 HFS+/HFSX 格式的移动硬盘（含 Time Machine 备份盘）。
**全程只读**，不会改动硬盘上的任何数据。

## 交付脚本

`scripts/` 目录里有两个一键脚本（需放到桌面或任意位置运行，脚本内的程序路径需按实际发布路径修改）：

- `挂载Mac硬盘.bat` —— 提权挂载 → 等待就绪 → `subst X: C:\MacDisk` → 自动打开资源管理器
- `卸载Mac硬盘.bat` —— 移除 X: 盘符并结束挂载进程

## 使用方法

1. 插上移动硬盘
2. 双击桌面 **`挂载Mac硬盘.bat`**，在 UAC 弹窗点"是"
3. 等几秒，资源管理器会自动打开：
   - **X: 盘** —— 盘符方式（当前登录会话）
   - **C:\MacDisk** —— 目录方式（所有程序、所有会话都可见）
4. 用完想卸载：双击桌面 **`卸载Mac硬盘.bat`**（或直接重启电脑）
5. 再次使用：重新运行挂载脚本即可（重启后挂载失效）

## 技术实现

- 参考开源项目 [hfsfuse](https://github.com/0x09/hfsfuse)（C 语言 HFS+ FUSE 驱动）的格式解析逻辑，
  用 C# (.NET 8) 实现了 HFS+ 只读解析引擎：
  GPT 分区定位 → 卷头 → 目录/Extents B-tree → 文件 fork 读取
- 通过 [WinFsp](https://winfsp.dev)（开源用户态文件系统框架，GPLv3）把解析引擎暴露成 Windows 盘符
- 支持特性：日志式/非日志式卷、大小写敏感 (HFSX)、Unicode 文件名、
  文件/目录硬链接（Time Machine 备份必需）、大文件多段 extents
- 已知限制（对浏览/拷贝用户数据无影响）：
  - 不解析 decmpfs 透明压缩文件（macOS 系统文件偶用，用户数据极少用）
  - 不解析资源分支（NTFS 命名流），文件本体不受影响
  - 时间戳按 UTC 直转，可能与 Mac 本地显示差几小时
  - 若 Mac 端未正常弹出且日志未回放，个别文件可能读取报错

## 代码结构

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

命令行直接运行（可选参数）：

```
HfsMounter.exe                          # 挂载到 C:\MacDisk（默认）
HfsMounter.exe --letter Z:              # 挂载为盘符（仅提权会话可见）
HfsMounter.exe --device \\.\PHYSICALDRIVE2   # 指定磁盘
HfsMounter.exe --list                   # 终端打印根目录自测，不挂载
```
