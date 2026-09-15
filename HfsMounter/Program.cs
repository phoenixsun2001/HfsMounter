using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using Fsp;

namespace HfsMounter;

public static class Program
{
    // GPT partition type GUID for Apple HFS+: 48465300-0000-11AA-AA11-00306543ECAC
    // (mixed-endian on-disk byte order)
    private static readonly byte[] HfsPartitionType =
    {
        0x00, 0x53, 0x46, 0x48, 0x00, 0x00, 0xAA, 0x11,
        0xAA, 0x11, 0x00, 0x30, 0x65, 0x43, 0xEC, 0xAC,
    };

    private sealed record HfsPartition(string Device, long OffsetBytes, long LengthBytes);

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("错误: " + ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        string? device = null;
        string? letter = null;
        string? mountDir = null;
        bool listOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--device": device = args[++i]; break;
                case "--letter": letter = args[++i]; break;
                case "--mount-dir": mountDir = args[++i]; break;
                case "--list": listOnly = true; break;
                default:
                    if (device == null) device = args[i];
                    else if (letter == null) letter = args[i];
                    break;
            }
        }

        // Drive letters created by an elevated process are only visible in that
        // elevation session, so a directory mount is the default (globally visible).
        if (letter == null && mountDir == null && !listOnly)
            mountDir = "C:\\MacDisk";

        HfsPartition part;
        if (device != null)
        {
            var (off, len) = FindHfsPartition(device);
            part = new HfsPartition(device, off, len);
        }
        else
        {
            part = ScanForHfsDisk() ?? throw new IOException("在 PHYSICALDRIVE0-15 上没有找到 HFS+ 分区");
        }

        Console.WriteLine($"目标: {part.Device}  分区偏移={part.OffsetBytes / (1L << 30)} GiB  大小={part.LengthBytes / (1L << 30)} GiB");

        using var disk = new RawDisk(part.Device, part.OffsetBytes, part.LengthBytes);
        var vol = new HfsVolume(disk);
        vol.Open();

        if (listOnly)
        {
            PrintTree(vol, depth: 0);
            return 0;
        }

        var fs = new HfsFileSystem(vol);
        FileSystemHost host;
        try
        {
            host = new FileSystemHost(fs);
            string mountPoint = letter ?? mountDir!;
            if (letter == null)
                Console.WriteLine($"将挂载到目录 {mountPoint}（对所有程序可见）");
            int status = host.Mount(mountPoint, null, false, 0);
            if (status != 0)
            {
                Console.Error.WriteLine($"挂载失败: NTSTATUS 0x{status:X8}");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            throw;
        }

        Console.WriteLine($"挂载成功: 盘符 {host.MountPoint()}  （保持本进程运行即可访问；结束进程即卸载）");
        Console.WriteLine("按 Ctrl+C 或结束本进程来卸载。");

        var done = new AutoResetEvent(false);
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            try { host.Unmount(); } catch { }
            done.Set();
        };
        done.WaitOne();
        return 0;
    }

    private static void PrintTree(HfsVolume vol, int depth)
    {
        var cat = vol.Cat!;
        var indent = new string(' ', depth * 3);
        foreach (var child in cat.EnumerateFolder(HfsVolume.CNID_ROOT_FOLDER))
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
                var real = cat.ResolveDirHardLink(child.SpecialNid);
                kind = real != null && real.IsFolder ? "DHLP" : "BAD!";
            }
            else if (isFileHardLink)
            {
                var real = cat.ResolveFileHardLink(child.SpecialNid);
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
                PrintSubtree(vol, child.Cnid, depth + 1);
            if (isDirHardLink && depth < 1)
            {
                var real = cat.ResolveDirHardLink(child.SpecialNid);
                if (real != null && real.IsFolder) PrintSubtree(vol, real.Cnid, depth + 1);
            }
        }
    }

    private static void PrintSubtree(HfsVolume vol, uint cnid, int depth)
    {
        var cat = vol.Cat!;
        var indent = new string(' ', depth * 3);
        int shown = 0;
        foreach (var child in cat.EnumerateFolder(cnid))
        {
            if (shown++ >= 8) { Console.WriteLine($"{indent}... 更多条目省略"); break; }
            string kind = child.IsFolder ? "DIR " : "FILE";
            ulong size = child.IsFile ? child.DataFork.LogicalSize : 0;
            Console.WriteLine($"{indent}{kind} {child.Name}  ({size} bytes)");
        }
    }

    private static HfsPartition? ScanForHfsDisk()
    {
        for (int i = 0; i < 16; i++)
        {
            string dev = $"\\\\.\\PHYSICALDRIVE{i}";
            try
            {
                var (off, len) = FindHfsPartition(dev);
                Console.WriteLine($"在 {dev} 发现 HFS+ 分区");
                return new HfsPartition(dev, off, len);
            }
            catch (FileNotFoundException) { }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"警告: {dev} 无法访问（权限不足），跳过");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ({dev}: {ex.Message})");
            }
        }
        return null;
    }

    /// <summary>Locate the Apple HFS+ partition via the GPT; returns its byte offset and length.</summary>
    private static (long off, long len) FindHfsPartition(string device)
    {
        foreach (uint sectorSize in new uint[] { 512, 4096 })
        {
            try
            {
                using var fs = new FileStream(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                var header = new byte[92];
                if (!ReadExactly(fs, sectorSize, header)) throw new IOException("无法读取 GPT 头");
                if (Encoding.ASCII.GetString(header, 0, 8) != "EFI PART")
                    continue;

                // GPT header is LITTLE-endian (unlike HFS+ structures)
                ulong entryLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
                uint numEntries = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
                uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
                if (entrySize < 128 || entrySize > 4096 || numEntries == 0 || numEntries > 1024)
                    continue;

                var entries = new byte[numEntries * entrySize];
                if (!ReadExactly(fs, (long)(entryLba * sectorSize), entries))
                    throw new IOException("无法读取 GPT 分区表");

                for (uint i = 0; i < numEntries; i++)
                {
                    int off = (int)(i * entrySize);
                    bool isHfs = true;
                    for (int b = 0; b < 16; b++)
                        if (entries[off + b] != HfsPartitionType[b]) { isHfs = false; break; }
                    if (!isHfs) continue;

                    ulong first = BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(off + 32));
                    ulong last = BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(off + 40));
                    long offset = (long)(first * sectorSize);
                    long length = (long)((last - first + 1) * sectorSize);
                    return (offset, length);
                }
                throw new IOException("GPT 中没有 Apple HFS+ 分区");
            }
            catch (IOException) when (sectorSize == 512)
            {
                // try 4Kn sector layout before giving up
            }
        }
        throw new IOException("该磁盘不是 GPT 或没有 Apple HFS+ 分区");
    }

    private static bool ReadExactly(FileStream fs, long pos, byte[] buf)
    {
        fs.Seek(pos, SeekOrigin.Begin);
        int done = 0;
        while (done < buf.Length)
        {
            int n = fs.Read(buf, done, buf.Length - done);
            if (n <= 0) return false;
            done += n;
        }
        return true;
    }
}
