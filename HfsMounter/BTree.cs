using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace HfsMounter;

/// <summary>
/// Read-only B-tree over a catalog/extents fork.
/// Catalog keys are descended by parent CNID only (numeric, the primary key component,
/// so ordinal comparison always matches the tree's collation for positioning).
/// Extents keys are fully numeric, so full-key ordinal descent is exact.
/// </summary>
public sealed class BTree
{
    public const int TreeCatalog = 1;
    public const int TreeExtents = 2;

    public const sbyte KindLeaf = -1;
    public const sbyte KindIndex = 0;
    public const sbyte KindHeader = 1;

    private readonly HfsVolume _vol;
    private readonly ForkData _fork;
    public readonly int TreeId;
    public uint NodeSize { get; private set; }
    public uint RootNode { get; private set; }
    public uint FirstLeaf { get; private set; }
    public uint ForkStartBlock { get; private set; }
    public ulong TotalBytes => _fork.LogicalSize;
    public uint TotalNodes { get; private set; }

    private readonly Dictionary<uint, Node> _nodes = new();
    private const int NodeCacheCap = 8192;

    public sealed class Node
    {
        public byte[] Data = Array.Empty<byte>();
        public sbyte Kind;
        public ushort NumRecs;
        public uint FLink, BLink;
        public int[] RecStart = Array.Empty<int>();
        public int[] RecEnd = Array.Empty<int>();
    }

    public BTree(HfsVolume vol, ForkData fork, int treeId)
    {
        _vol = vol;
        _fork = fork;
        TreeId = treeId;
        ForkStartBlock = BinaryPrimitives.ReadUInt32BigEndian(fork.Raw.AsSpan(0));
        long forkBase = (long)ForkStartBlock * vol.BlockSize;

        // The header record (inside node 0) carries the node size, so probe the
        // first 512 bytes to self-bootstrap, then re-read node 0 at full size.
        var probe = new byte[512];
        vol.ReadAt(forkBase, 512, probe, 0);
        NodeSize = BinaryPrimitives.ReadUInt16BigEndian(probe.AsSpan(14 + 18));
        RootNode = BinaryPrimitives.ReadUInt32BigEndian(probe.AsSpan(14 + 2));
        FirstLeaf = BinaryPrimitives.ReadUInt32BigEndian(probe.AsSpan(14 + 10));
        TotalNodes = BinaryPrimitives.ReadUInt32BigEndian(probe.AsSpan(14 + 22));
        if (NodeSize < 512 || NodeSize > 131072 || (NodeSize & (NodeSize - 1)) != 0)
            throw new IOException($"bad btree node size {NodeSize}");
    }

    private byte[] ReadNodeRaw(uint node)
    {
        long off = (long)node * NodeSize;
        if (off + NodeSize > (long)TotalBytes) throw new IOException($"node {node} out of range");
        var buf = new byte[(int)NodeSize];
        _vol.ReadAt((long)ForkStartBlock * _vol.BlockSize + off, (int)NodeSize, buf, 0);
        return buf;
    }

    public Node GetNode(uint node)
    {
        if (_nodes.TryGetValue(node, out var n)) return n;
        var data = ReadNodeRaw(node);
        n = new Node { Data = data };
        n.FLink = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0));
        n.BLink = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        n.Kind = (sbyte)data[8];
        n.NumRecs = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));

        n.RecStart = new int[n.NumRecs];
        n.RecEnd = new int[n.NumRecs];
        for (int i = 0; i < n.NumRecs; i++)
            n.RecStart[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)(NodeSize - 2 * (i + 1))));
        int freeSpace = n.NumRecs > 0
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)(NodeSize - 2 * (n.NumRecs + 1))))
            : 0;
        for (int i = 0; i < n.NumRecs; i++)
            n.RecEnd[i] = i == n.NumRecs - 1 ? freeSpace : n.RecStart[i + 1];

        if (_nodes.Count >= NodeCacheCap) _nodes.Clear();
        _nodes[node] = n;
        return n;
    }

    /// <summary>Key length field (u16) at record start; returns (parentCnid) for catalog keys,
    /// or (forkType,cnid,startBlock) for extent keys, both read via explicit offsets by callers.</summary>
    public static ushort KeyLen(Node node, int rec) =>
        BinaryPrimitives.ReadUInt16BigEndian(node.Data.AsSpan(node.RecStart[rec]));

    /// <summary>Catalog key parent CNID (offset +2 after the key_len field).</summary>
    public static uint CatalogKeyParent(Node node, int rec) =>
        BinaryPrimitives.ReadUInt32BigEndian(node.Data.AsSpan(node.RecStart[rec] + 2));

    /// <summary>Data offset of a keyed record: recStart + 2 + keyLen (+1 pad if odd).</summary>
    public static int KeyedDataOffset(Node node, int rec)
    {
        int start = node.RecStart[rec];
        ushort keyLen = BinaryPrimitives.ReadUInt16BigEndian(node.Data.AsSpan(start));
        return start + 2 + keyLen + (keyLen & 1);
    }

    /// <summary>Index-node child pointer stored right after the key.</summary>
    public static uint IndexChild(Node node, int rec) =>
        BinaryPrimitives.ReadUInt32BigEndian(node.Data.AsSpan(KeyedDataOffset(node, rec)));

    /// <summary>
    /// Descend from the root to a leaf, choosing at each index node the rightmost record
    /// whose catalog key parent is &lt;= parent (or the first record if none match).
    /// </summary>
    public Node FindLeafByParent(uint parent)
    {
        var node = GetNode(RootNode);
        int guard = 0;
        while (node.Kind == KindIndex)
        {
            if (++guard > 128) throw new IOException("btree descent too deep");
            int chosen = -1;
            for (int i = 0; i < node.NumRecs; i++)
            {
                if (CatalogKeyParent(node, i) <= parent) chosen = i;
                else break;
            }
            uint child = chosen < 0 ? IndexChild(node, 0) : IndexChild(node, chosen);
            node = GetNode(child);
        }
        if (node.Kind != KindLeaf) throw new IOException("unexpected btree node kind");
        return node;
    }

    /// <summary>
    /// Position at the first catalog record (in leaf chain order) whose key parent is &gt;= parent.
    /// Returns the node and record index; (null,0) if none.
    /// </summary>
    public (Node? node, int rec) PositionByParent(uint parent)
    {
        var node = FindLeafByParent(parent);
        // walk back while this node starts after the target range
        int guard = 0;
        while (node.NumRecs > 0 && CatalogKeyParent(node, 0) >= parent && node.BLink != 0 && ++guard < 1024)
            node = GetNode(node.BLink);

        // scan forward for first record with parent >= parent
        while (true)
        {
            for (int i = 0; i < node.NumRecs; i++)
            {
                uint p = CatalogKeyParent(node, i);
                if (p >= parent) return (node, i);
            }
            if (node.FLink == 0) return (null, 0);
            node = GetNode(node.FLink);
        }
    }

    /// <summary>
    /// Find the extents-overflow leaf record covering (forkType, cnid, block):
    /// the last record with the same (forkType,cnid) and start_block &lt;= block.
    /// Returns the 8 extent descriptors, or null.
    /// </summary>
    public Extent[]? FindExtentRecord(byte forkType, uint cnid, uint block)
    {
        var node = GetNode(RootNode);
        int guard = 0;
        while (node.Kind == KindIndex)
        {
            if (++guard > 128) throw new IOException("btree descent too deep");
            int chosen = -1;
            for (int i = 0; i < node.NumRecs; i++)
            {
                int c = CompareExtentKey(node, i, forkType, cnid, block);
                if (c <= 0) chosen = i;
                else break;
            }
            uint child = chosen < 0 ? IndexChild(node, 0) : IndexChild(node, chosen);
            node = GetNode(child);
        }

        int last = -1;
        for (int i = 0; i < node.NumRecs; i++)
        {
            int start = node.RecStart[i];
            ushort keyLen = BinaryPrimitives.ReadUInt16BigEndian(node.Data.AsSpan(start));
            byte ft = node.Data[start + 2];
            uint c = BinaryPrimitives.ReadUInt32BigEndian(node.Data.AsSpan(start + 4));
            if (c > cnid || (c == cnid && ft > forkType)) break;
            if (c != cnid || ft != forkType) continue;
            uint sb = BinaryPrimitives.ReadUInt32BigEndian(node.Data.AsSpan(start + 8));
            if (sb <= block) last = i;
        }
        if (last < 0) return null;

        int dataOff = KeyedDataOffset(node, last);
        var extents = new Extent[8];
        for (int i = 0; i < 8; i++)
        {
            uint sb = BinaryPrimitives.ReadUInt32BigEndian(node.Data.AsSpan(dataOff + i * 8));
            uint bc = BinaryPrimitives.ReadUInt32BigEndian(node.Data.AsSpan(dataOff + i * 8 + 4));
            extents[i] = new Extent(sb, bc);
        }
        return extents;
    }

    private static int CompareExtentKey(Node node, int rec, byte forkType, uint cnid, uint block)
    {
        int start = node.RecStart[rec];
        byte ft = node.Data[start + 2];
        uint c = BinaryPrimitives.ReadUInt32BigEndian(node.Data.AsSpan(start + 4));
        uint sb = BinaryPrimitives.ReadUInt32BigEndian(node.Data.AsSpan(start + 8));
        if (ft != forkType) return ft < forkType ? -1 : 1;
        if (c != cnid) return c < cnid ? -1 : 1;
        if (sb != block) return sb < block ? -1 : 1;
        return 0;
    }
}
