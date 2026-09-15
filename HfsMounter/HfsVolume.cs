using System;
using System.Buffers.Binary;

namespace HfsMounter;

/// <summary>HFS+ extent descriptor (big-endian on disk).</summary>
public readonly struct Extent
{
    public readonly uint StartBlock;
    public readonly uint BlockCount;
    public Extent(uint start, uint count) { StartBlock = start; BlockCount = count; }
}

/// <summary>HFS+ fork data parsed from an 80-byte on-disk structure.</summary>
public sealed class ForkData
{
    public ulong LogicalSize;
    public uint TotalBlocks;
    public byte[] Raw = new byte[64]; // 8 inline extent descriptors

    public static ForkData Parse(byte[] buf, int off)
    {
        var f = new ForkData();
        f.LogicalSize = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(off));
        f.TotalBlocks = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(off + 12));
        Buffer.BlockCopy(buf, off + 16, f.Raw, 0, 64);
        return f;
    }
}

public sealed class HfsVolume
{
    // Volume header offsets (relative to start of the 512-byte header block at +1024)
    private const int VH_ATTRS = 4;
    private const int VH_BLOCKSIZE = 40;
    private const int VH_TOTAL_BLOCKS = 44;
    private const int VH_FREE_BLOCKS = 48;
    private const int VH_ALLOC_FILE = 112;
    private const int VH_EXTENTS_FILE = 192;
    private const int VH_CATALOG_FILE = 272;

    public const ushort SIG_HFSP = 0x482B; // 'H+'
    public const ushort SIG_HFSX = 0x4858; // 'HX'

    public const uint CNID_ROOT_FOLDER = 2;
    public const uint CNID_EXTENTS = 3;
    public const uint CNID_CATALOG = 4;

    public const byte ForkDataFork = 0x00;
    public const byte ForkResource = 0xFF;

    private readonly RawDisk _disk;
    private uint _blockSize;
    private ulong _totalBlocks;
    private ulong _freeBlocks;
    private bool _journaled;
    private bool _caseSensitive;
    private ForkData _catalogFork = new();
    private ForkData _extentsFork = new();
    private BTree? _catalog;
    private BTree? _extents;
    public NodeCache NodeCache { get; } = new();
    public Catalog? Cat { get; private set; }

    public uint BlockSize => _blockSize;
    public bool CaseSensitive => _caseSensitive;
    public ulong TotalBytes => _totalBlocks * _blockSize;
    public ulong FreeBytes => _freeBlocks * _blockSize;

    public HfsVolume(RawDisk disk) { _disk = disk; }

    public void Open()
    {
        var vh = new byte[512];
        _disk.Read(1024, 512, vh, 0);
        ushort sig = BinaryPrimitives.ReadUInt16BigEndian(vh);
        if (sig != SIG_HFSP && sig != SIG_HFSX)
            throw new IOException($"not an HFS+ volume (signature 0x{sig:X4})");

        uint attrs = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(VH_ATTRS));
        _journaled = (attrs & (1u << 13)) != 0;
        _caseSensitive = sig == SIG_HFSX;

        _blockSize = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(VH_BLOCKSIZE));
        _totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(VH_TOTAL_BLOCKS));
        _freeBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh.AsSpan(VH_FREE_BLOCKS));
        if (_blockSize < 512 || (_blockSize & (_blockSize - 1)) != 0)
            throw new IOException($"bad block size {_blockSize}");

        _extentsFork = ForkData.Parse(vh, VH_EXTENTS_FILE);
        _catalogFork = ForkData.Parse(vh, VH_CATALOG_FILE);

        _extents = new BTree(this, _extentsFork, BTree.TreeExtents);
        _catalog = new BTree(this, _catalogFork, BTree.TreeCatalog);
        Cat = new Catalog(this, _catalog);

        Console.WriteLine($"HFS+ 卷已打开: 块大小={_blockSize} 总计={_totalBlocks * _blockSize / (1 << 30)} GiB " +
                          $"空闲={_freeBlocks * _blockSize / (1 << 30)} GiB 日志式={_journaled} 大小写敏感={_caseSensitive}");
    }

    /// <summary>Read arbitrary bytes at a volume-relative byte offset.</summary>
    public void ReadAt(long offset, int count, byte[] buf, int bufOff) =>
        _disk.Read(offset, count, buf, bufOff);

    /// <summary>
    /// Resolve the full extent list of a fork: the 8 inline extents plus any
    /// number of extents fetched from the extents-overflow B-tree.
    /// Key layout: [keylen u16][forkType u8][pad u8][cnid u32][startBlock u32] (all numeric => ordinal compare is safe).
    /// </summary>
    public System.Collections.Generic.List<Extent> GetExtents(uint cnid, byte forkType, ForkData fork)
    {
        var list = new System.Collections.Generic.List<Extent>(16);
        for (int i = 0; i < 8; i++)
        {
            int off = i * 8;
            uint sb = BinaryPrimitives.ReadUInt32BigEndian(fork.Raw.AsSpan(off));
            uint bc = BinaryPrimitives.ReadUInt32BigEndian(fork.Raw.AsSpan(off + 4));
            if (bc != 0) list.Add(new Extent(sb, bc));
        }

        ulong covered = 0;
        foreach (var e in list) covered += e.BlockCount;
        if (fork.TotalBlocks == 0 || covered >= fork.TotalBlocks || _extents is null)
            return list;

        var btree = _extents;
        while (covered < fork.TotalBlocks)
        {
            var rec = btree.FindExtentRecord(forkType, cnid, (uint)covered);
            if (rec == null) throw new IOException($"extents overflow missing for cnid={cnid} block={covered}");
            bool progressed = false;
            foreach (var e in rec)
            {
                if (e.BlockCount == 0) continue;
                if (e.StartBlock + e.BlockCount <= covered) continue; // already covered
                list.Add(e);
                covered = e.StartBlock + e.BlockCount;
                progressed = true;
            }
            if (!progressed) throw new IOException($"extent chain stuck for cnid={cnid}");
        }
        return list;
    }

    /// <summary>Read a contiguous range of a fork (built from extents) into buf.</summary>
    public void ReadFork(uint cnid, byte forkType, ForkData fork, System.Collections.Generic.List<Extent> extents,
                         long offset, int count, byte[] buf, int bufOff)
    {
        if (offset < 0 || (ulong)offset + (ulong)count > fork.LogicalSize)
            throw new IOException($"fork read out of bounds: off={offset} count={count} size={fork.LogicalSize}");
        if (count == 0) return;

        long block = offset / _blockSize;
        int into = (int)(offset % _blockSize);

        // find extent containing 'block' (extent lists are short; linear scan with cursor is fine)
        uint baseBlock = 0;
        int ei = 0;
        while (ei < extents.Count && baseBlock + extents[ei].BlockCount <= block)
        {
            baseBlock += extents[ei].BlockCount;
            ei++;
        }
        if (ei >= extents.Count) throw new IOException($"block {block} beyond extents for cnid={cnid}");

        int remaining = count;
        int srcSkip = (int)(block - baseBlock) * (int)_blockSize + into;
        while (remaining > 0 && ei < extents.Count)
        {
            var ext = extents[ei];
            long extBytes = (long)ext.BlockCount * _blockSize - srcSkip;
            int take = (int)Math.Min(extBytes, remaining);
            if (take > 0)
            {
                long abs = (long)ext.StartBlock * _blockSize + srcSkip;
                _disk.Read(abs, take, buf, bufOff);
                bufOff += take;
                remaining -= take;
                srcSkip += take;
            }
            if (remaining > 0)
            {
                ei++;
                srcSkip = 0;
            }
        }
        if (remaining > 0) throw new IOException($"short fork read for cnid={cnid}");
    }
}
