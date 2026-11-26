using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HfsMounter;

/// <summary>%ProgramData%\HfsMounter（不可写时回退到 %LOCALAPPDATA%\HfsMounter）。</summary>
public static class AppPaths
{
    private static string? _dataDir;

    public static string DataDir => _dataDir ??= ResolveDataDir();

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string LogDir => Path.Combine(DataDir, "logs");

    /// <summary>显式指定配置路径所在目录（--data-dir）。</summary>
    public static void OverrideDataDir(string directory)
    {
        _dataDir = Path.GetFullPath(directory);
    }

    private static string ResolveDataDir()
    {
        // 依次尝试：ProgramData（服务/所有用户）→ LocalAppData → 临时目录 → 当前目录
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HfsMounter"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HfsMounter"),
            Path.Combine(Path.GetTempPath(), "HfsMounter"),
            Path.Combine(Directory.GetCurrentDirectory(), ".hfsmounter"),
        };
        var errors = new List<string>();
        foreach (string candidate in candidates)
        {
            try
            {
                if (IsWritable(candidate)) return candidate;
            }
            catch (Exception ex)
            {
                errors.Add($"{candidate}: {ex.Message}");
            }
        }
        throw new IOException("找不到可写的数据目录（尝试过: " + string.Join("; ", candidates) + "）");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".write-probe");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>常驻服务的配置，保存为 config.json。</summary>
public sealed class AppConfig
{
    /// <summary>首选挂载目录；多个 HFS 卷时依次使用 C:\MacDisk2、C:\MacDisk3…</summary>
    public string MountRoot { get; set; } = @"C:\MacDisk";

    /// <summary>轮询磁盘的间隔（秒）。</summary>
    public int PollSeconds { get; set; } = 3;

    /// <summary>最多同时挂载几个卷。</summary>
    public int MaxMounts { get; set; } = 4;

    /// <summary>卷连续多少次探测失败后判定为已拔出并卸载。</summary>
    public int RemovalGracePolls { get; set; } = 2;

    /// <summary>--linker 使用的盘符。</summary>
    public string LinkerLetter { get; set; } = "X:";

    /// <summary>--linker 在新挂载出现时自动打开资源管理器。</summary>
    public bool AutoOpenExplorer { get; set; } = true;

    /// <summary>卷在资源管理器里显示的名字；留空表示使用 HFS+ 卷名，再退回 "MacDisk"。</summary>
    public string? VolumeLabel { get; set; }

    public LogLevel MinLogLevel { get; set; } = LogLevel.Info;

    public int LogRetainDays { get; set; } = 14;

    [JsonIgnore]
    public int EffectivePollSeconds => Math.Clamp(PollSeconds, 1, 300);

    [JsonIgnore]
    public int EffectiveMaxMounts => Math.Clamp(MaxMounts, 1, 26);

    [JsonIgnore]
    public int EffectiveRemovalGrace => Math.Clamp(RemovalGracePolls, 1, 20);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        try
        {
            if (!File.Exists(path))
            {
                var fresh = new AppConfig();
                fresh.Save(path);
                return fresh;
            }

            string json = File.ReadAllText(path);
            AppConfig? cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return (cfg ?? new AppConfig()).Normalize();
        }
        catch (Exception ex)
        {
            Log.Warn($"配置文件 {path} 读取失败，使用默认值: {ex.Message}");
            return new AppConfig();
        }
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.ConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private AppConfig Normalize()
    {
        if (string.IsNullOrWhiteSpace(MountRoot)) MountRoot = @"C:\MacDisk";
        MountRoot = MountRoot.Trim().TrimEnd('\\', '/');
        if (MountRoot.Length == 2 && MountRoot[1] == ':') MountRoot += "\\";
        if (string.IsNullOrWhiteSpace(LinkerLetter)) LinkerLetter = "X:";
        if (string.IsNullOrWhiteSpace(VolumeLabel)) VolumeLabel = null;
        return this;
    }
}

/// <summary>持久化"卷 → 挂载目录"，重插同一块盘时尽量复用同一个目录。</summary>
public sealed class MountState
{
    public Dictionary<string, string> Paths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static MountState Load(string? path = null)
    {
        path ??= AppPaths.StateFile;
        try
        {
            if (!File.Exists(path)) return new MountState();
            var state = JsonSerializer.Deserialize<MountState>(File.ReadAllText(path), JsonOptions);
            if (state == null) return new MountState();
            state.Paths = new Dictionary<string, string>(state.Paths, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex)
        {
            Log.Warn($"状态文件读取失败，忽略: {ex.Message}");
            return new MountState();
        }
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.StateFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"状态文件写入失败: {ex.Message}");
        }
    }
}
