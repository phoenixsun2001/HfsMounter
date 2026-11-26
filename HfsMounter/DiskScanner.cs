using System;
using System.Collections.Generic;
using System.IO;

namespace HfsMounter;

/// <summary>一块可挂载的 HFS+ 卷（物理磁盘上的一个分区）。</summary>
public sealed class HfsCandidate
{
    /// <summary>跨插拔稳定的身份：GPT 磁盘 GUID + 分区 GUID。</summary>
    public required string VolumeId { get; init; }

    public required string DiskPath { get; init; }
    public int DiskIndex { get; init; }
    public string DiskGuid { get; init; } = "";
    public string PartGuid { get; init; } = "";
    public int PartIndex { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public int SectorSize { get; init; }
    public string? Model { get; init; }
    public string? Serial { get; init; }
    public HfsVolumeHeader Header { get; init; }

    public string Describe() =>
        $"{(string.IsNullOrEmpty(Model) ? "未知磁盘" : Model)} ({DiskPath} 分区 {PartIndex}, {FormatSize(Length)}, {Header.SignatureText})";

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.#} TB"
        : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB"
        : $"{bytes} B";
}

public sealed class DiskScanResult
{
    public List<HfsCandidate> Candidates { get; } = new();

    /// <summary>有磁盘因为权限不足而无法读取（通常意味着进程没有管理员权限）。</summary>
    public bool AccessDenied { get; set; }

    /// <summary>无法访问的磁盘示例，便于日志说明。</summary>
    public string? AccessDeniedSample { get; set; }
}

/// <summary>枚举 \\.\PHYSICALDRIVEn，找出带 Apple HFS+ 分区的磁盘。</summary>
public static class DiskScanner
{
    public const int MaxDiskIndex = 32;

    public static DiskScanResult Scan(bool verbose = false)
    {
        var result = new DiskScanResult();
        int consecutiveMissing = 0;

        for (int index = 0; index < MaxDiskIndex; index++)
        {
            string path = @"\\.\PHYSICALDRIVE" + index;
            FileStream? fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                consecutiveMissing = 0;
                ScanDisk(fs, index, path, result, verbose);
            }
            catch (FileNotFoundException)
            {
                // 盘序号一般连续，连续三个不存在就认为枚举结束
                if (++consecutiveMissing >= 3) break;
            }
            catch (DirectoryNotFoundException)
            {
                if (++consecutiveMissing >= 3) break;
            }
            catch (UnauthorizedAccessException)
            {
                consecutiveMissing = 0;
                result.AccessDenied = true;
                result.AccessDeniedSample ??= path;
            }
            catch (IOException ex) when (ex is not FileNotFoundException)
            {
                consecutiveMissing = 0;
                if (verbose) Log.Debug($"跳过 {path}: {ex.Message}");
            }
            finally
            {
                fs?.Dispose();
            }
        }

        return result;
    }

    /// <summary>只探测指定的磁盘路径（例如 \\.\PHYSICALDRIVE1）上的 HFS+ 分区。</summary>
    public static bool TryProbe(string devicePath, out HfsCandidate? candidate, out string error)
    {
        candidate = null;
        error = "";
        try
        {
            using var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            var result = new DiskScanResult();
            ScanDisk(fs, ParseDiskIndex(devicePath), devicePath, result, verbose: false);
            if (result.Candidates.Count == 0)
            {
                error = $"{devicePath} 上没有找到可识别的 HFS+ 分区";
                return false;
            }
            candidate = result.Candidates[0];
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = $"无法读取 {devicePath}（需要管理员权限）";
            return false;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or DirectoryNotFoundException)
        {
            error = $"无法读取 {devicePath}: {ex.Message}";
            return false;
        }
    }

    private static int ParseDiskIndex(string devicePath)
    {
        const string prefix = @"\\.\PHYSICALDRIVE";
        return devicePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(devicePath.AsSpan(prefix.Length), out int index)
            ? index
            : -1;
    }

    private static void ScanDisk(FileStream fs, int index, string path, DiskScanResult result, bool verbose)
    {
        string? model = null;
        string? serial = null;
        try
        {
            Win32.TryGetDeviceDescriptor(fs.SafeFileHandle, out model, out serial);
        }
        catch (Exception)
        {
            // 型号只是锦上添花
        }

        if (!Gpt.TryRead(fs, out GptTable? table, out string error) || table == null)
        {
            if (verbose) Log.Debug($"{path}: {error}");
            return;
        }

        if (table.IntegrityWarning != null)
            Log.Warn($"{path}: {table.IntegrityWarning}");

        int foundBefore = result.Candidates.Count;
        foreach (GptPartition part in table.Partitions)
        {
            if (!string.Equals(part.TypeGuid, Gpt.HfsPartitionTypeGuid, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!HfsProbe.TryRead(fs, part.OffsetBytes, out HfsVolumeHeader header))
            {
                Log.Warn($"{path} 分区 {part.Index} 标为 Apple HFS+，但卷头无效（可能尚未格式化或数据被破坏），已跳过");
                continue;
            }

            string partId = part.UniqueGuid == "00000000-0000-0000-0000-000000000000"
                ? $"{table.DiskGuid}#{part.Index}"
                : part.UniqueGuid;

            result.Candidates.Add(new HfsCandidate
            {
                VolumeId = $"{table.DiskGuid}/{partId}",
                DiskPath = path,
                DiskIndex = index,
                DiskGuid = table.DiskGuid,
                PartGuid = partId,
                PartIndex = part.Index,
                Offset = part.OffsetBytes,
                Length = part.LengthBytes,
                SectorSize = table.SectorSize,
                Model = model,
                Serial = serial,
                Header = header,
            });
        }

        if (verbose && result.Candidates.Count == foundBefore)
            Log.Debug($"{path}: 没有 HFS+ 分区");
    }

    /// <summary>Windows 上是否以管理员身份运行（读物理磁盘的必要条件）。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
