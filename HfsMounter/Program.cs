using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace HfsMounter;

using InstallState = HfsMounter.ServiceRuntime.InstallState;
using ServiceState = HfsMounter.ServiceRuntime.ServiceState;

/// <summary>
/// 命令行入口。同一个 exe 既是 Windows 服务（--service），也能当前台守护进程（--daemon）、
/// 装/卸服务（--install-service/--uninstall-service）、单次挂载（--mount，兼容旧用法）、
/// 自检（--selftest）以及用户会话里的盘符联动（--linker）。
/// </summary>
public static class Program
{
    private static DateTime _startedAt = DateTime.Now;

    private const string AppTitle = "HfsMounter — Windows 只读 HFS+/HFSX 挂载器（常驻服务版）";

    public static int Main(string[] args)
    {
        _startedAt = DateTime.Now;

        // 服务模式下没有控制台，设置编码会抛异常
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        try
        {
            return Run(args);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            try { Log.Error("未处理异常", ex); } catch { }
            try { Console.Error.WriteLine("错误: " + ex.Message); } catch { }
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        Options opt = Options.Parse(args);

        if (opt.DataDir != null) AppPaths.OverrideDataDir(opt.DataDir);

        // 先接管控制台，这样读配置阶段的告警也能被看到；读完配置再补上文件日志。
        if (opt.Verb is not ("help" or "version"))
        {
            try { Log.Initialize(null, LogLevel.Info, 14, captureConsole: true); } catch { }
        }

        AppConfig config;
        try
        {
            config = AppConfig.Load(opt.Config);
        }
        catch (Exception ex)
        {
            Log.Error("读取配置文件失败", ex);
            Console.Error.WriteLine($"读取配置文件失败: {ex.Message}");
            return 1;
        }

        if (opt.Poll is int poll && poll > 0) config.PollSeconds = poll;

        if (opt.Verb is not ("help" or "version"))
        {
            try { Log.Initialize(AppPaths.LogDir, config.MinLogLevel, config.LogRetainDays, captureConsole: true); }
            catch (Exception ex) { Console.Error.WriteLine($"警告: 日志初始化失败（{ex.Message}），继续运行。"); }
        }

        switch (opt.Verb)
        {
            case "service":
                return RunService(config);

            case "daemon":
                return RunForeground(config);

            case "install-service":
                return InstallService(config);

            case "uninstall-service":
                ServiceRuntime.StopServiceByName();
                return ServiceRuntime.Uninstall();

            case "start":
                return ServiceRuntime.StartServiceByName();

            case "stop":
                return ServiceRuntime.StopServiceByName();

            case "status":
                return PrintStatus(config);

            case "probe":
                return PrintProbe();

            case "list":
                return PrintList(opt);

            case "mount":
                return MountOnce(config, opt);

            case "linker":
                return RunLinker(config, opt);

            case "linker-install":
                return InstallLinker(config, opt);

            case "linker-uninstall":
                return Linker.Uninstall();

            case "selftest":
                return SelfTest.Run();

            case "version":
                Console.WriteLine($"{AppTitle}  {typeof(Program).Assembly.GetName().Version}");
                return 0;

            default:
                PrintUsage(opt.Verb == "help" ? 0 : 1);
                return opt.Verb == "help" ? 0 : 1;
        }
    }

    // ---------------------------------------------------------------- 服务

    /// <summary>由 SCM 启动：把工作线程交给 Supervisor，停止/关机请求经取消令牌传导。</summary>
    private static int RunService(AppConfig config)
    {
        using var supervisor = new Supervisor(config);
        return ServiceRuntime.RunAsService(token =>
        {
            supervisor.Run(token);
            return 0;
        });
    }

    /// <summary>前台常驻（调试用，或不想装服务时把它放进启动项）。</summary>
    private static int RunForeground(AppConfig config)
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            Log.Info("收到中断信号，正在卸载并退出…");
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            using var supervisor = new Supervisor(config);
            Log.Info($"{AppTitle} 前台模式启动，Ctrl+C 退出。");
            supervisor.Run(cts.Token);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static int InstallService(AppConfig config)
    {
        string exe = ExecutablePath();
        try
        {
            config.Save();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"警告: 无法写入配置文件 {AppPaths.ConfigFile}: {ex.Message}");
        }
        return ServiceRuntime.Install(exe, config);
    }

    // ---------------------------------------------------------------- 诊断

    private static int PrintProbe()
    {
        Console.WriteLine(AppTitle);
        Console.WriteLine($"管理员权限: {(DiskScanner.IsElevated ? "是" : "否（读物理磁盘需要管理员权限）")}");
        bool winfsp = Supervisor.WinFspPresent(out string winfspDetail);
        Console.WriteLine($"WinFsp: {(winfsp ? "已安装" : "未安装")}  {winfspDetail}");

        DiskScanResult scan;
        try
        {
            scan = DiskScanner.Scan(verbose: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"扫描磁盘失败: {ex.Message}");
            return 1;
        }

        if (scan.AccessDenied)
            Console.WriteLine($"有磁盘无法访问（{scan.AccessDeniedSample}），通常是权限不足。");

        if (scan.Candidates.Count == 0)
        {
            Console.WriteLine("未发现 HFS+ 分区。");
            return 2;
        }

        Console.WriteLine($"发现 {scan.Candidates.Count} 个 HFS+ 分区:");
        foreach (HfsCandidate c in scan.Candidates)
        {
            Console.WriteLine($"  - {c.Describe()}");
            Console.WriteLine($"      偏移={c.Offset}  长度={HfsCandidate.FormatSize(c.Length)}  扇区={c.SectorSize}  卷ID={c.VolumeId}");
        }
        return 0;
    }

    private static int PrintStatus(AppConfig config)
    {
        Console.WriteLine(AppTitle);
        Console.WriteLine($"数据目录: {AppPaths.DataDir}");
        Console.WriteLine($"配置: 挂载根目录={config.MountRoot}  轮询={config.EffectivePollSeconds}s  " +
                          $"最多挂载={config.EffectiveMaxMounts}  拔盘容错={config.EffectiveRemovalGrace}次  联动盘符={config.LinkerLetter}");
        Console.WriteLine($"管理员权限: {(DiskScanner.IsElevated ? "是" : "否")}");

        bool winfsp = Supervisor.WinFspPresent(out string winfspDetail);
        Console.WriteLine($"WinFsp: {(winfsp ? "已安装" : "未安装")}  {winfspDetail}");

        ServiceState? state = ServiceRuntime.QueryService();
        Console.WriteLine($"服务 {ServiceRuntime.ServiceName}: {DescribeState(state)}");

        MountState mounts = MountState.Load();
        if (mounts.Paths.Count == 0)
        {
            Console.WriteLine("已记录的挂载点: 无");
        }
        else
        {
            Console.WriteLine("已记录的挂载点:");
            foreach (KeyValuePair<string, string> kv in mounts.Paths)
            {
                string flag = Directory.Exists(kv.Value) ? "存在" : "缺失";
                Console.WriteLine($"  - {kv.Value}  [{flag}]  ← {kv.Key}");
            }
        }

        try
        {
            DiskScanResult scan = DiskScanner.Scan(verbose: false);
            Console.WriteLine($"当前检测到的 HFS+ 分区: {scan.Candidates.Count}");
            foreach (HfsCandidate c in scan.Candidates)
                Console.WriteLine($"  - {c.Describe()}  →  卷ID={c.VolumeId}");
            if (scan.AccessDenied)
                Console.WriteLine($"  （有磁盘无法访问: {scan.AccessDeniedSample}，多半是权限不足）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"扫描磁盘失败: {ex.Message}");
        }

        IReadOnlyList<string> recent = Log.TailFile(200)
            .Where(line => !IsFromThisRun(line))
            .TakeLast(12)
            .ToList();
        if (recent.Count > 0)
        {
            Log.Raw("服务端最近的日志:");
            foreach (string line in recent) Log.Raw("  " + line);
        }
        Console.WriteLine($"完整日志: {Log.LogDirectory}");
        return 0;
    }

    /// <summary>判断一行日志是不是本次命令刚刚写下的（时间戳不早于进程启动）。</summary>
    private static bool IsFromThisRun(string line)
    {
        if (line.Length < 23) return true;
        return !DateTime.TryParse(line.AsSpan(0, 23), out DateTime stamp) || stamp >= _startedAt;
    }

    private static string DescribeState(ServiceState? state)
    {
        if (state == null) return "未安装（用 --install-service 安装）";
        return state.State switch
        {
            InstallState.Running => $"运行中（PID {state.ProcessId}）",
            InstallState.Stopped => "已安装，未运行",
            InstallState.StartPending => "正在启动",
            InstallState.StopPending => "正在停止",
            InstallState.NotInstalled => "未安装（用 --install-service 安装）",
            _ => $"其他状态 (raw=0x{state.RawState:X8})",
        };
    }

    // ---------------------------------------------------------------- 旧式单次挂载

    /// <summary>定位要挂载的分区：--device 指定磁盘，否则扫描第一块带 HFS+ 的磁盘。</summary>
    private static HfsCandidate? ResolveCandidate(Options opt, out string error)
    {
        error = "";
        if (opt.Device != null)
        {
            if (DiskScanner.TryProbe(opt.Device, out HfsCandidate? one, out error)) return one;
            return null;
        }

        DiskScanResult scan = DiskScanner.Scan(verbose: true);
        if (scan.Candidates.Count > 0) return scan.Candidates[0];

        error = scan.AccessDenied
            ? $"无法读取物理磁盘（{scan.AccessDeniedSample}），请以管理员身份运行"
            : "没有找到含 Apple HFS+ 分区的磁盘";
        return null;
    }

    private static int PrintList(Options opt)
    {
        HfsCandidate? candidate = ResolveCandidate(opt, out string error);
        if (candidate == null)
        {
            Log.Error(error);
            Console.Error.WriteLine("错误: " + error);
            return 1;
        }

        using var disk = new RawDisk(candidate.DiskPath, candidate.Offset, candidate.Length);
        var volume = new HfsVolume(disk);
        volume.Open();
        PrintTree(volume, 0);
        return 0;
    }

    private static int MountOnce(AppConfig config, Options opt)
    {
        HfsCandidate? candidate = ResolveCandidate(opt, out string error);
        if (candidate == null)
        {
            Log.Error(error);
            Console.Error.WriteLine("错误: " + error);
            return 1;
        }

        string mountDir = opt.MountDir ?? config.MountRoot;
        MountedVolume? mounted = MountedVolume.TryMount(candidate, mountDir, config, out error);
        if (mounted == null)
        {
            Log.Error($"挂载失败: {error}");
            Console.Error.WriteLine("错误: " + error);
            return 1;
        }

        Log.Info($"已挂载 {candidate.VolumeId} → {mounted.MountPath}");
        Console.WriteLine($"挂载成功: {mounted.MountPath}  （{candidate.Describe()}）");

        if (opt.Letter != null)
        {
            // 旧行为：映射成盘符（仅在当前登录会话可见）
            try
            {
                Win32.MapDriveLetter(opt.Letter, mounted.MountPath);
                Console.WriteLine($"已映射盘符 {Win32.NormalizeLetter(opt.Letter)} → {mounted.MountPath}");
            }
            catch (Exception ex)
            {
                Log.Warn($"映射盘符失败: {ex.Message}");
                Console.Error.WriteLine($"警告: 映射盘符失败: {ex.Message}");
            }
        }

        Console.WriteLine("按 Ctrl+C 或结束本进程来卸载。");
        using var done = new ManualResetEventSlim(false);
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            done.Set();
        };
        Console.CancelKeyPress += handler;
        try
        {
            done.Wait();
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            mounted.Dispose();
            Log.Info("已卸载。");
        }
        return 0;
    }

    /// <summary>--list 用：打印根目录两层，确认能正确解析目录树。</summary>
    private static void PrintTree(HfsVolume volume, int depth)
    {
        Catalog cat = volume.Cat!;
        string indent = new(' ', depth * 3);
        foreach (CatalogEntry child in cat.EnumerateFolder(HfsVolume.CNID_ROOT_FOLDER))
        {
            if (cat.IsPrivateRootName(child.Name)) continue;
            if (child.RecType is CatalogEntry.RecFolderThread or CatalogEntry.RecFileThread) continue;

            bool isDirHardLink = cat.IsDirHardLink(child);
            bool isFileHardLink = cat.IsFileHardLink(child);
            string kind;
            ulong size = 0;

            if (child.IsFolder)
            {
                kind = "DIR ";
            }
            else if (isDirHardLink)
            {
                CatalogEntry? real = cat.ResolveDirHardLink(child.SpecialNid);
                kind = real != null && real.IsFolder ? "DHLP" : "BAD!";
            }
            else if (isFileHardLink)
            {
                CatalogEntry? real = cat.ResolveFileHardLink(child.SpecialNid);
                kind = "FHLP";
                if (real != null && real.IsFile) size = real.DataFork.LogicalSize;
            }
            else
            {
                kind = "FILE";
                size = child.DataFork.LogicalSize;
            }

            Console.WriteLine($"{indent}{kind} {child.Name}  ({size} bytes)");

            if (child.IsFolder && depth < 1)
            {
                PrintSubtree(volume, child.Cnid, depth + 1);
            }
            if (isDirHardLink && depth < 1)
            {
                CatalogEntry? real = cat.ResolveDirHardLink(child.SpecialNid);
                if (real != null && real.IsFolder) PrintSubtree(volume, real.Cnid, depth + 1);
            }
        }
    }

    private static void PrintSubtree(HfsVolume volume, uint cnid, int depth)
    {
        Catalog cat = volume.Cat!;
        string indent = new(' ', depth * 3);
        int shown = 0;
        foreach (CatalogEntry child in cat.EnumerateFolder(cnid))
        {
            if (shown++ >= 8)
            {
                Console.WriteLine($"{indent}... 更多条目省略");
                break;
            }
            string kind = child.IsFolder ? "DIR " : "FILE";
            ulong size = child.IsFile ? child.DataFork.LogicalSize : 0;
            Console.WriteLine($"{indent}{kind} {child.Name}  ({size} bytes)");
        }
    }

    // ---------------------------------------------------------------- 盘符联动

    private static int RunLinker(AppConfig config, Options opt)
    {
        string letter = opt.Letter ?? config.LinkerLetter;
        string target = opt.Target ?? opt.MountDir ?? config.MountRoot;

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return Linker.Run(letter, target, opt.Watch, opt.Open || config.AutoOpenExplorer, opt.Hidden, cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static int InstallLinker(AppConfig config, Options opt)
    {
        string letter = opt.Letter ?? config.LinkerLetter;
        string target = opt.Target ?? opt.MountDir ?? config.MountRoot;
        int rc = Linker.Install(letter, target, opt.Open || config.AutoOpenExplorer);
        if (rc == 0)
        {
            Console.WriteLine("已创建登录自启任务；退出并重新登录后，插入 Mac 磁盘会自动出现盘符。");
            Console.WriteLine("立即体验（当前会话）:");
            Console.WriteLine($"  HfsMounter.exe --linker {Win32.NormalizeLetter(letter)} \"{Path.GetFullPath(target)}\" --watch");
        }
        return rc;
    }

    // ---------------------------------------------------------------- 帮助

    private static string ExecutablePath()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) path = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("无法确定当前可执行文件路径");
        return path;
    }

    private static void PrintUsage(int exitCode)
    {
        string text = $"""
            {AppTitle}

            用法:
              HfsMounter.exe --install-service        安装并启动开机自启的常驻服务（需管理员）
              HfsMounter.exe --uninstall-service      停止并删除服务（需管理员）
              HfsMounter.exe --start | --stop         启动 / 停止服务
              HfsMounter.exe --status                 查看服务状态、检测到的磁盘、最近的日志
              HfsMounter.exe --daemon                 前台常驻（不装服务，Ctrl+C 退出）
              HfsMounter.exe --probe                  只扫描磁盘，报告可挂载的 HFS+ 分区（需管理员）
              HfsMounter.exe --selftest               内置自检（不需要管理员）

            兼容旧用法（单次挂载，进程退出即卸载）:
              HfsMounter.exe [--device \\.\PHYSICALDRIVE1] [--mount-dir C:\MacDisk] [--letter X:] [--list]

            可选参数:
              --config <路径>     指定配置文件（默认 {AppPaths.ConfigFile}）
              --data-dir <目录>   指定数据目录（配置/状态/日志都放这里）
              --poll <秒>         覆盖磁盘轮询间隔

            盘符联动（可选，把挂载目录映射成盘符，等效 subst）:
              HfsMounter.exe --linker X: C:\MacDisk --watch      持续维护盘符
              HfsMounter.exe --linker-install X: C:\MacDisk      创建登录自启入口（无管理员权限时写“启动”文件夹）
              HfsMounter.exe --linker-uninstall                  删除该入口

            说明:
              * 服务以 LocalSystem 运行，挂载点是目录（默认 {new AppConfig().MountRoot}），对所有用户可见；
                WinFsp 的盘符挂载只在创建它的会话里可见，所以盘符请用 --linker 在用户会话里创建。
              * 需要 WinFsp 2.1+（winget install WinFsp.WinFsp）与 .NET 8 运行时。
            """;
        Console.WriteLine(text);
    }
}

/// <summary>极简命令行解析：同时支持 --verb 风格与旧的位置参数写法。</summary>
internal sealed class Options
{
    public string? Verb { get; private set; }
    public string? Device { get; set; }
    public string? Letter { get; set; }
    public string? MountDir { get; set; }
    public string? Target { get; set; }
    public string? Config { get; set; }
    public string? DataDir { get; set; }
    public bool ListOnly { get; set; }
    public bool Watch { get; set; }
    public bool Open { get; set; }
    public bool Hidden { get; set; }
    public int? Poll { get; set; }
    public List<string> Positional { get; } = new();

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"参数 {arg} 缺少取值");
                return args[++i];
            }

            switch (arg)
            {
                case "--service": o.Verb = "service"; break;
                case "--daemon":
                case "--foreground": o.Verb = "daemon"; break;
                case "--install-service": o.Verb = "install-service"; break;
                case "--uninstall-service": o.Verb = "uninstall-service"; break;
                case "--start": o.Verb = "start"; break;
                case "--stop": o.Verb = "stop"; break;
                case "--status": o.Verb = "status"; break;
                case "--probe":
                case "--scan": o.Verb = "probe"; break;
                case "--selftest": o.Verb = "selftest"; break;
                case "--mount": o.Verb = "mount"; break;
                case "--linker": o.Verb = "linker"; break;
                case "--linker-install": o.Verb = "linker-install"; break;
                case "--linker-uninstall": o.Verb = "linker-uninstall"; break;
                case "--list": o.ListOnly = true; o.Verb ??= "list"; break;

                case "--device": o.Device = Next(); break;
                case "--letter": o.Letter = Next(); break;
                case "--mount-dir": o.MountDir = Next(); break;
                case "--config": o.Config = Next(); break;
                case "--data-dir": o.DataDir = Next(); break;
                case "--poll": o.Poll = int.Parse(Next()); break;
                case "--target": o.Target = Next(); break;
                case "--watch": o.Watch = true; break;
                case "--open": o.Open = true; break;
                case "--hidden": o.Hidden = true; break;

                case "-h":
                case "--help":
                case "-?":
                case "/?": o.Verb = "help"; break;
                case "--version": o.Verb = "version"; break;

                default:
                    if (arg.StartsWith('-')) throw new ArgumentException($"未知参数: {arg}（用 --help 查看用法）");
                    o.Positional.Add(arg);
                    break;
            }
        }

        if (o.Verb is "linker" or "linker-install")
        {
            // --linker X: C:\MacDisk
            if (o.Positional.Count > 0) o.Letter ??= o.Positional[0];
            if (o.Positional.Count > 1) o.Target ??= o.Positional[1];
        }
        else if (o.Verb == null && o.Positional.Count > 0)
        {
            // 旧用法: HfsMounter.exe \\.\PHYSICALDRIVE1 X:
            o.Device ??= o.Positional[0];
            if (o.Positional.Count > 1) o.Letter ??= o.Positional[1];
        }

        if (o.Verb == null)
        {
            if (o.Device != null || o.Letter != null || o.MountDir != null || o.ListOnly)
                o.Verb = o.ListOnly ? "list" : "mount";
            else
                o.Verb = "help";
        }

        return o;
    }
}
