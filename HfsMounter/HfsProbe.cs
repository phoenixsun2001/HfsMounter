using System;
using System.Buffers.Binary;
using System.IO;

namespace HfsMounter;

/// <summary>HFS+/HFSX 卷头（volume header，位于分区偏移 1024 处，512 字节）。</summary>
public readonly struct HfsVolumeHeader
{
    public ushort Signature { get; init; }
    public uint BlockSize { get; init; }
    public uint TotalBlocks { get; init; }
    public uint FreeBlocks { get; init; }
    public uint Attributes { get; init; }

    public bool IsHfsx => Signature == HfsVolume.SIG_HFSX;
    public bool IsJournaled => (Attributes & (1u << 13)) != 0;
    public ulong TotalBytes => (ulong)TotalBlocks * BlockSize;
    public ulong FreeBytes => (ulong)FreeBlocks * BlockSize;
    public string SignatureText => Signature == HfsVolume.SIG_HFSP ? "H+" : "HX";
}

/// <summary>只看卷头的最小探测，用于"这块盘到底是不是 HFS+"以及拔盘健康检查。</summary>
public static class HfsProbe
{
    public const int VolumeHeaderOffset = 1024;
    public const int VolumeHeaderLength = 512;

    public static bool TryParse(ReadOnlySpan<byte> block, out HfsVolumeHeader header)
    {
        header = default;
        if (block.Length < VolumeHeaderLength) return false;

        ushort signature = BinaryPrimitives.ReadUInt16BigEndian(block);
        if (signature != HfsVolume.SIG_HFSP && signature != HfsVolume.SIG_HFSX) return false;

        uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(block[40..]);
        if (blockSize < 512 || blockSize > (1u << 20) || (blockSize & (blockSize - 1)) != 0) return false;

        header = new HfsVolumeHeader
        {
            Signature = signature,
            BlockSize = blockSize,
            TotalBlocks = BinaryPrimitives.ReadUInt32BigEndian(block[44..]),
            FreeBlocks = BinaryPrimitives.ReadUInt32BigEndian(block[48..]),
            Attributes = BinaryPrimitives.ReadUInt32BigEndian(block[4..]),
        };
        return header.TotalBlocks > 0;
    }

    /// <summary>直接从裸设备读卷头（partitionOffset 为分区起始字节偏移）。</summary>
    public static bool TryRead(FileStream fs, long partitionOffset, out HfsVolumeHeader header)
    {
        header = default;
        byte[] buffer = new byte[VolumeHeaderLength];
        if (!Gpt.ReadExact(fs, partitionOffset + VolumeHeaderOffset, buffer, 0, buffer.Length)) return false;
        return TryParse(buffer, out header);
    }
}
