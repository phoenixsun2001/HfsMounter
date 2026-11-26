using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace HfsMounter;

/// <summary>
/// 盘符联动：把服务挂载出来的目录映射成盘符（等效 subst），并可选随用户登录自启。
/// 之所以要在用户会话里做，是因为 WinFsp 的盘符挂载只在创建它的登录会话里可见，
/// 而常驻服务跑在会话 0。
/// </summary>
public static class Linker
{
    /// <summary>计划任务名（登录时运行，权限为普通用户，不弹 UAC）。</summary>
    public const string TaskName = "HfsMounter-DriveLetter";

    private const string ScriptFileName = "linker.cmd";

    private static bool _occupiedWarned;

    /// <summary>维护“目录已挂载 → 分配盘符；目录消失 → 收回盘符”的循环。</summary>
    public static int Run(string letter, string target, bool watch, bool open, bool hidden, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(letter))
            throw new ArgumentException("缺少盘符，例如: --linker X: C:\\MacDisk");
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("缺少挂载目录，例如: --linker X: C:\\MacDisk");

        string drive = Win32.NormalizeLetter(letter);
        target = Path.GetFullPath(target);

        if (hidden) Win32.HideConsole();
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        if (watch)
        {
            try { Log.Initialize(AppPaths.LogDir, LogLevel.Info, 7, captureConsole: false); } catch { }
        }

        Log.Info($"盘符联动: {drive} → {target}{(watch ? "（持续监视）" : "")}");
        if (!watch)
        {
            Sync(drive, target, open);
            return 0;
        }

        bool wasMapped = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                bool mapped = Sync(drive, target, open);
                if (mapped != wasMapped)
                {
                    if (mapped) Log.Info($"已映射盘符 {drive} → {target}");
                    else Log.Info($"挂载点已消失，收回盘符 {drive}");
                    wasMapped = mapped;
                }
            }
            catch (Exception ex)
            {
                Log.Error("同步盘符失败", ex);
            }
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
        }

        Unmap(drive);
        return 0;
    }

    /// <summary>创建登录自启任务（写一个 .cmd 再由计划任务调用，避免多层引号转义）。</summary>
    public static int Install(string letter, string target, bool open)
    {
        string drive = Win32.NormalizeLetter(letter);
        target = Path.GetFullPath(target);
        string exe = ExecutablePath();

        Directory.CreateDirectory(AppPaths.DataDir);
        string scriptPath = Path.Combine(AppPaths.DataDir, ScriptFileName);
        string script = $"""
            @echo off
            rem 由 HfsMounter --linker-install 生成：登录后在当前用户会话里把挂载目录映射成盘符
            start "" /min "{exe}" --linker {drive} "{target}" --watch --hidden{(open ? " --open" : "")}
            """;
        File.WriteAllText(scriptPath, script.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));

        int rc = RunSchTasks($"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL LIMITED /TR \"{scriptPath}\"");
        if (rc == 0)
        {
            Console.WriteLine($"已创建登录自启任务: {TaskName}");
            Console.WriteLine($"  启动脚本: {scriptPath}");
            Console.WriteLine($"  映射: {drive} → {target}");
        }
        else
        {
            Console.Error.WriteLine("创建计划任务失败（可能是权限或策略限制）。可以手动把下面这条命令放进“启动”文件夹:");
            Console.Error.WriteLine($"  \"{exe}\" --linker {drive} \"{target}\" --watch --hidden");
        }
        return rc;
    }

    /// <summary>删除登录自启任务，并收回盘符。</summary>
    public static int Uninstall()
    {
        int rc = RunSchTasks($"/Delete /F /TN \"{TaskName}\"");
        if (rc == 0) Console.WriteLine($"已删除计划任务 {TaskName}。");
        else Console.WriteLine($"计划任务 {TaskName} 不存在或删除失败（可能本来就没装）。");
        return 0;
    }

    // ---------------------------------------------------------------- 内部

    private static bool Sync(string drive, string target, bool open)
    {
        bool mounted = IsMounted(target);
        string? current = Win32.QueryDriveLetter(drive);

        if (mounted)
        {
            if (current == null)
            {
                Win32.MapDriveLetter(drive, target);
                _occupiedWarned = false;
                if (open) OpenExplorer(target);
            }
            else if (!SameTarget(current, target) && !_occupiedWarned)
            {
                _occupiedWarned = true;
                Log.Warn($"盘符 {drive} 已被映射到 {current}，不抢占，跳过");
            }
            return true;
        }

        if (current != null && SameTarget(current, target)) Unmap(drive);
        return false;
    }

    private static void Unmap(string drive)
    {
        string? current = Win32.QueryDriveLetter(drive);
        if (current == null) return;
        try
        {
            Win32.UnmapDriveLetter(drive);
            Log.Info($"已收回盘符 {drive}");
        }
        catch (Exception ex)
        {
            Log.Warn($"收回盘符 {drive} 失败: {ex.Message}");
        }
    }

    /// <summary>目录是否真的挂载着文件系统（残留的重解析点会让 Directory.Exists 也为真）。</summary>
    private static bool IsMounted(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            Directory.GetFileSystemEntries(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool SameTarget(string mapped, string target)
    {
        static string Normalize(string path)
        {
            string s = path.Trim();
            if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s[4..];
            s = s.TrimEnd('\\');
            return s;
        }
        return string.Equals(Normalize(mapped), Normalize(target), StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenExplorer(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开资源管理器失败: {ex.Message}");
        }
    }

    private static int RunSchTasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            int code = p.HasExited ? p.ExitCode : -1;
            foreach (string line in (stdout + stderr).Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) Console.WriteLine("  " + t);
            }
            return code;
        }
        catch (Exception ex)
        {
            Log.Error("调用 schtasks 失败", ex);
            return -1;
        }
    }

    private static string ExecutablePath()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) path = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("无法确定当前可执行文件路径");
        return path;
    }
}
