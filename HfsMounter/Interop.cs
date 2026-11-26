using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HfsMounter;

/// <summary>
/// 少量 Win32 P/Invoke：控制台窗口、DOS 设备名（subst 等效）、磁盘型号查询。
/// 服务相关的 SCM 接口在 <see cref="ServiceRuntime"/> 中。
/// </summary>
internal static class Win32
{
    // ---------------- 控制台 ----------------

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;

    /// <summary>没有控制台时返回 false（例如被 SCM 启动）。</summary>
    internal static bool HasConsole => GetConsoleWindow() != IntPtr.Zero;

    /// <summary>隐藏当前控制台窗口（计划任务里跑 --linker 时避免黑框）。</summary>
    internal static void HideConsole()
    {
        IntPtr h = GetConsoleWindow();
        if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE);
    }

    // ---------------- DOS 设备（subst 的底层实现） ----------------

    private const uint DDD_RAW_TARGET_PATH = 0x00000001;
    private const uint DDD_REMOVE_DEFINITION = 0x00000002;
    private const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "DefineDosDeviceW")]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string? targetPath);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryDosDeviceW")]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, uint maxLength);

    /// <summary>把盘符（"X:"）指向一个目录，等效于 subst X: &lt;dir&gt;。</summary>
    internal static void MapDriveLetter(string letter, string directory)
    {
        string target = directory.StartsWith(@"\??\", StringComparison.Ordinal) ? directory : @"\??\" + directory;
        if (!DefineDosDevice(DDD_RAW_TARGET_PATH, NormalizeLetter(letter), target))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"无法建立 {letter} 映射");
    }

    /// <summary>解除盘符映射。未映射时静默返回。</summary>
    internal static void UnmapDriveLetter(string letter)
    {
        DefineDosDevice(DDD_RAW_TARGET_PATH | DDD_REMOVE_DEFINITION | DDD_EXACT_MATCH_ON_REMOVE,
            NormalizeLetter(letter), null);
    }

    /// <summary>查询盘符当前指向；未映射返回 null。</summary>
    internal static string? QueryDriveLetter(string letter)
    {
        var sb = new StringBuilder(1024);
        uint n = QueryDosDevice(NormalizeLetter(letter), sb, (uint)sb.Capacity);
        if (n == 0) return null;
        // 多字符串，取第一个
        string raw = sb.ToString();
        int nul = raw.IndexOf('\0');
        if (nul >= 0) raw = raw.Substring(0, nul);
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    internal static string NormalizeLetter(string letter)
    {
        string l = letter.Trim();
        if (l.EndsWith(":", StringComparison.Ordinal)) l = l[..^1];
        if (l.Length != 1 || !char.IsLetter(l[0]))
            throw new ArgumentException($"盘符无效: {letter}（应形如 X:）");
        return l.ToUpperInvariant() + ":";
    }

    // ---------------- 磁盘型号 / 序列号 ----------------

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint StorageDeviceProperty = 0;
    private const uint PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint ioControlCode,
        ref STORAGE_PROPERTY_QUERY inBuffer, uint inBufferSize,
        byte[] outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    /// <summary>
    /// 读取 STORAGE_DEVICE_DESCRIPTOR 里的型号/序列号。失败时返回 false，不影响挂载。
    /// </summary>
    internal static bool TryGetDeviceDescriptor(SafeFileHandle handle, out string? model, out string? serial)
    {
        model = null;
        serial = null;
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageDeviceProperty,
            QueryType = PropertyStandardQuery,
        };
        byte[] buffer = new byte[1024];
        if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, ref query,
                (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(), buffer, (uint)buffer.Length, out uint returned, IntPtr.Zero))
            return false;
        if (returned < 36) return false;

        uint vendorOff = BitConverter.ToUInt32(buffer, 12);
        uint productOff = BitConverter.ToUInt32(buffer, 16);
        uint serialOff = BitConverter.ToUInt32(buffer, 24);

        string vendor = ReadAnsiAt(buffer, vendorOff);
        string product = ReadAnsiAt(buffer, productOff);
        model = string.Join(' ', new[] { vendor, product }.Where(s => s.Length > 0));
        if (model.Length == 0) model = null;
        string sn = ReadAnsiAt(buffer, serialOff).Trim();
        serial = sn.Length == 0 ? null : sn;
        return true;
    }

    // ---------------------------------------------------------------- WinFsp 原生装载器

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryW")]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    private static string? _winfspLoaderPath;
    private static bool _winfspLoaderTried;

    /// <summary>
    /// 找到并预加载 winfsp-x64.dll。新版 WinFsp（2025 / SxS 布局）把原生库放在
    /// %ProgramFiles(x86)%\WinFsp\bin，既不在 System32 也没有加进 PATH，
    /// 所以这里按「exe 同目录 → System32 → 注册表 SxsDir → 安装目录」依次找，找到就 LoadLibrary，
    /// 后续 winfsp-msil 的 P/Invoke 就能按模块名解析到已加载的库。
    /// </summary>
    public static bool EnsureWinfspLoader(out string detail)
    {
        if (_winfspLoaderTried)
        {
            detail = _winfspLoaderPath ?? "未找到";
            return _winfspLoaderPath != null;
        }
        _winfspLoaderTried = true;

        var tried = new List<string>();
        foreach (string candidate in WinfspLoaderCandidates())
        {
            tried.Add(candidate);
            if (!File.Exists(candidate)) continue;
            try
            {
                if (LoadLibraryW(candidate) != IntPtr.Zero)
                {
                    _winfspLoaderPath = candidate;
                    detail = candidate;
                    return true;
                }
                tried[^1] += $"(LoadLibrary 失败, err={Marshal.GetLastWin32Error()})";
            }
            catch (Exception ex)
            {
                tried[^1] += $"({ex.GetType().Name})";
            }
        }

        detail = "未找到 winfsp-x64.dll：" + string.Join(" | ", tried);
        return false;
    }

    private static IEnumerable<string> WinfspLoaderCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "winfsp-x64.dll");

        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        yield return Path.Combine(system32, "winfsp-x64.dll");

        string? sxs = ReadWinfspSxsDir();
        if (sxs != null) yield return Path.Combine(sxs, "bin", "winfsp-x64.dll");

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (programFilesX86.Length > 0) yield return Path.Combine(programFilesX86, "WinFsp", "bin", "winfsp-x64.dll");

        string? installDir = ReadWinfspInstallDir();
        if (installDir != null) yield return Path.Combine(installDir, "bin", "winfsp-x64.dll");
    }

    private static string? ReadWinfspSxsDir() => ReadWinfspRegistryValue("SxsDir");

    private static string? ReadWinfspInstallDir() => ReadWinfspRegistryValue("InstallDir");

    /// <summary>读 HKLM\SOFTWARE\WinFsp 下的字符串值；新版安装器只写 32 位视图。</summary>
    private static string? ReadWinfspRegistryValue(string name)
    {
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = baseKey.OpenSubKey(@"SOFTWARE\WinFsp");
                if (key?.GetValue(name) is string value && value.Length > 0) return value.TrimEnd('\\');
            }
            catch (Exception)
            {
                // 视图不存在或没有权限
            }
        }
        return null;
    }

    /// <summary>WinFsp 的驱动/服务是否已注册（新版只有 WinFsp.Launcher，驱动按需加载）。</summary>
    public static bool WinfspServicesPresent()
    {
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (File.Exists(Path.Combine(system32, "drivers", "winfsp.sys"))) return true;

        foreach (string name in new[] { "winfsp", "WinFsp.Launcher" })
        {
            if (ServiceExists(name)) return true;
        }
        return false;
    }

    private static bool ServiceExists(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key != null;
        }
        catch (Exception)
        {
            return false;
        }
    }
    private static string ReadAnsiAt(byte[] buffer, uint offset)
    {
        if (offset == 0 || offset >= buffer.Length) return string.Empty;
        int end = (int)offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        return Encoding.ASCII.GetString(buffer, (int)offset, end - (int)offset).Trim();
    }
}
