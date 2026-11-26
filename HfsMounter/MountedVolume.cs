using System;
using System.IO;
using Fsp;

namespace HfsMounter;

/// <summary>
/// 一个已经挂载的 HFS+ 卷：持有裸磁盘句柄、HfsVolume、WinFsp 宿主。
/// 挂载失败时保证已分配的资源全部释放，不会留下半挂载状态。
/// </summary>
public sealed class MountedVolume : IDisposable
{
    public required HfsCandidate Candidate { get; init; }
    public required string MountPath { get; init; }
    public required string Label { get; init; }
    public string? VolumeName { get; private set; }
    public DateTime MountedAtUtc { get; } = DateTime.UtcNow;
    public int ConsecutiveHealthFailures { get; set; }

    private RawDisk? _disk;
    private HfsVolume? _volume;
    private FileSystemHost? _host;

    private MountedVolume() { }

    public static MountedVolume? TryMount(HfsCandidate candidate, string mountPath, AppConfig config, out string error)
    {
        error = "";
        PrepareMountPoint(mountPath);

        RawDisk? disk = null;
        try
        {
            disk = new RawDisk(candidate.DiskPath, candidate.Offset, candidate.Length);
            var volume = new HfsVolume(disk);
            volume.Open();

            string? volumeName = TryReadVolumeName(volume);
            string label = config.VolumeLabel
                           ?? volumeName
                           ?? (string.IsNullOrEmpty(candidate.Model) ? "MacDisk" : candidate.Model!);

            var fs = new HfsFileSystem(volume) { Label = label };
            var host = new FileSystemHost(fs);

            int preflight = host.Preflight(mountPath);
            if (preflight != 0)
            {
                host.Dispose();
                disk.Dispose();
                error = $"挂载点 {mountPath} 不可用（Preflight NTSTATUS 0x{preflight:X8}）";
                return null;
            }

            // Synchronized=true：WinFsp 串行化回调，避免 BTree 节点缓存并发竞争（只读卷无性能损失）
            int status = host.Mount(mountPath, null, true, 0);
            if (status != 0)
            {
                host.Dispose();
                disk.Dispose();
                error = $"挂载失败: NTSTATUS 0x{status:X8}";
                return null;
            }

            var mounted = new MountedVolume
            {
                Candidate = candidate,
                MountPath = mountPath,
                Label = label,
                VolumeName = volumeName,
            };
            mounted._disk = disk;
            mounted._volume = volume;
            mounted._host = host;
            return mounted;
        }
        catch (Exception ex)
        {
            disk?.Dispose();
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>读一次卷头，判断磁盘是否还在。失败计数由调用方管理。</summary>
    public bool HealthCheck()
    {
        if (_disk == null) return false;
        try
        {
            byte[] buffer = new byte[HfsProbe.VolumeHeaderLength];
            _disk.Read(HfsProbe.VolumeHeaderOffset, buffer.Length, buffer, 0);
            return HfsProbe.TryParse(buffer, out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Unmount()
    {
        FileSystemHost? host = _host;
        _host = null;
        if (host != null)
        {
            try { host.Unmount(); }
            catch (Exception ex) { Log.Warn($"卸载 {MountPath} 时出错: {ex.Message}"); }
            try { host.Dispose(); }
            catch (Exception ex) { Log.Debug($"释放文件系统宿主时出错: {ex.Message}"); }
        }

        _volume = null;
        try { _disk?.Dispose(); } catch (Exception) { }
        _disk = null;

        RemoveMountPointIfEmpty(MountPath);
    }

    public void Dispose() => Unmount();

    /// <summary>清理上次异常退出留下的 WinFsp 挂载点（reparse point）。</summary>
    private static void PrepareMountPoint(string mountPath)
    {
        if (File.Exists(mountPath))
            throw new IOException($"{mountPath} 是一个文件，无法作为挂载点");

        if (!Directory.Exists(mountPath))
        {
            Directory.CreateDirectory(mountPath);
            return;
        }

        // 已存在：只有"空的、上次残留的挂载点"才可以安全清掉
        var info = new DirectoryInfo(mountPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            try
            {
                info.Delete();
                Directory.CreateDirectory(mountPath);
                Log.Info($"已清理残留挂载点 {mountPath}");
                return;
            }
            catch (Exception ex)
            {
                throw new IOException($"{mountPath} 上存在无法清理的残留挂载点: {ex.Message}");
            }
        }
    }

    private static void RemoveMountPointIfEmpty(string mountPath)
    {
        try
        {
            if (!Directory.Exists(mountPath)) return;
            var info = new DirectoryInfo(mountPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return;
            info.Delete();
        }
        catch (Exception ex)
        {
            Log.Debug($"清理挂载点 {mountPath} 失败: {ex.Message}");
        }
    }

    private static string? TryReadVolumeName(HfsVolume volume)
    {
        try
        {
            // 根目录（CNID 2）在父 CNID 1 下，它的名字就是 HFS+ 卷名
            foreach (CatalogEntry entry in volume.Cat!.EnumerateFolder(1))
            {
                if (entry.Cnid == HfsVolume.CNID_ROOT_FOLDER && !string.IsNullOrWhiteSpace(entry.Name))
                    return entry.Name.Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"读取卷名失败: {ex.Message}");
        }
        return null;
    }
}
