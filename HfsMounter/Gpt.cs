using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HfsMounter;

/// <summary>GPT 分区项。</summary>
public sealed class GptPartition
{
    public int Index { get; init; }
    public string TypeGuid { get; init; } = "";
    public string UniqueGuid { get; init; } = "";
    public long FirstLba { get; init; }
    public long LastLba { get; init; }
    public long OffsetBytes { get; init; }
    public long LengthBytes { get; init; }
    public string Name { get; init; } = "";
}

/// <summary>解析出来的 GPT 保护分区表。</summary>
public sealed class GptTable
{
    public int SectorSize { get; init; }
    public string DiskGuid { get; init; } = "";
    public List<GptPartition> Partitions { get; init; } = new();
    public string? IntegrityWarning { get; init; }
}

/// <summary>GPT（GUID 分区表）读取，纯解析，不做任何写操作。</summary>
public static class Gpt
{
    /// <summary>Apple HFS+ 分区类型 GUID。</summary>
    public const string HfsPartitionTypeGuid = "48465300-0000-11aa-aa11-00306543ecac";

    private static readonly int[] CandidateSectorSizes = { 512, 4096 };

    public static bool TryRead(FileStream fs, out GptTable? table, out string error)
    {
        table = null;
        error = "";
        var errors = new List<string>();
        foreach (int sectorSize in CandidateSectorSizes)
        {
            if (TryReadWithSectorSize(fs, sectorSize, out table, out error)) return true;
            if (!string.IsNullOrEmpty(error)) errors.Add(error);
        }

        table = null;
        error = errors.Count > 0 ? errors[0] : "无法识别分区表";
        return false;
    }

    private static bool TryReadWithSectorSize(FileStream fs, int sectorSize, out GptTable? table, out string error)
    {
        table = null;
        error = "";
        if (fs.Length < sectorSize * 3L)
        {
            error = $"设备太小（{fs.Length} 字节），不像是磁盘";
            return false;
        }

        byte[] header = new byte[sectorSize];
        if (!ReadExact(fs, sectorSize, header, 0, sectorSize))
        {
            error = "读取 GPT 头失败";
            return false;
        }

        if (!header.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
        {
            error = $"LBA1 不是 GPT 头（扇区大小 {sectorSize}）";
            return false;
        }

        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (headerSize < 92 || headerSize > sectorSize)
        {
            error = $"GPT 头长度异常: {headerSize}";
            return false;
        }

        string diskGuid = new Guid(header.AsSpan(56, 16)).ToString("D");
        long entryLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
        uint numEntries = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
        if (numEntries == 0 || numEntries > 1024 || entrySize < 128 || entrySize > 4096)
        {
            error = $"GPT 分区项表参数异常: num={numEntries} size={entrySize}";
            return false;
        }

        long arrayBytes = (long)numEntries * entrySize;
        if (arrayBytes > 4L * 1024 * 1024 || entryLba <= 0)
        {
            error = "GPT 分区项表越界";
            return false;
        }

        byte[] array = new byte[arrayBytes];
        if (!ReadExact(fs, entryLba * sectorSize, array, 0, array.Length))
        {
            error = "读取 GPT 分区项表失败";
            return false;
        }

        string? warning = null;
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88));
        if (expectedCrc != 0)
        {
            byte[] copy = header.AsSpan(0, headerSize).ToArray();
            copy.AsSpan(16, 4).Clear();
            if (Crc32.Compute(copy) != expectedCrc)
                warning = "GPT 头 CRC 校验不匹配（分区表可能被破坏或磁盘未正常弹出）";
        }

        var partitions = new List<GptPartition>();
        for (int i = 0; i < (int)numEntries; i++)
        {
            int off = (int)(i * entrySize);
            ReadOnlySpan<byte> entry = array.AsSpan(off, (int)entrySize);
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0) continue; // 空项

            long first = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]);
            long last = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]);
            if (last < first) continue;

            partitions.Add(new GptPartition
            {
                Index = i + 1,
                TypeGuid = new Guid(entry[..16]).ToString("D"),
                UniqueGuid = new Guid(entry[16..32]).ToString("D"),
                FirstLba = first,
                LastLba = last,
                OffsetBytes = first * sectorSize,
                LengthBytes = (last - first + 1) * sectorSize,
                Name = Encoding.Unicode.GetString(entry[56..128]).TrimEnd('\0'),
            });
        }

        table = new GptTable
        {
            SectorSize = sectorSize,
            DiskGuid = diskGuid,
            Partitions = partitions,
            IntegrityWarning = warning,
        };
        return true;
    }

    /// <summary>读取一段数据；越界或读不满返回 false（不抛异常）。</summary>
    internal static bool ReadExact(FileStream fs, long offset, byte[] buffer, int bufferOffset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > fs.Length) return false;
        try
        {
            fs.Position = offset;
            int read = 0;
            while (read < count)
            {
                int n = fs.Read(buffer, bufferOffset + read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>标准 CRC-32（IEEE 802.3）。</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
