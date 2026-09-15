using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace HfsMounter;

/// <summary>A raw catalog record with its key, plus lazily-parsed accessors.</summary>
public sealed class CatalogEntry
{
    public const ushort RecFolder = 1;
    public const ushort RecFile = 2;
    public const ushort RecFolderThread = 3;
    public const ushort RecFileThread = 4;

    public uint ParentCnid;
    public string Name = "";
    public ushort RecType;
    public int DataOff;      // offset of the record data within Node.Data
    public BTree.Node Node = null!;

    public bool IsFolder => RecType == RecFolder;
    public bool IsFile => RecType == RecFile;

    private void EnsureFileRecord()
    {
        if (!IsFile || Node.Data.Length < DataOff + 248) throw new IOException("corrupt/short file record");
    }
    private void EnsureFolderRecord()
    {
        if (!IsFolder || Node.Data.Length < DataOff + 88) throw new IOException("corrupt/short folder record");
    }

    public uint Cnid
    {
        get
        {
            if (IsFile) EnsureFileRecord(); else EnsureFolderRecord();
            return BinaryPrimitives.ReadUInt32BigEndian(Node.Data.AsSpan(DataOff + 8));
        }
    }
    public uint CreatedHfs
    {
        get
        {
            if (IsFile) EnsureFileRecord(); else EnsureFolderRecord();
            return BinaryPrimitives.ReadUInt32BigEndian(Node.Data.AsSpan(DataOff + 12));
        }
    }
    public uint ModifiedHfs
    {
        get
        {
            if (IsFile) EnsureFileRecord(); else EnsureFolderRecord();
            return BinaryPrimitives.ReadUInt32BigEndian(Node.Data.AsSpan(DataOff + 16));
        }
    }
    public uint FileType { get { EnsureFileRecord(); return BinaryPrimitives.ReadUInt32BigEndian(Node.Data.AsSpan(DataOff + 48)); } }
    public uint Creator { get { EnsureFileRecord(); return BinaryPrimitives.ReadUInt32BigEndian(Node.Data.AsSpan(DataOff + 52)); } }
    /// <summary>bsd special.nid — hard link inode number for 'hlnk'/'fdrp' records.</summary>
    public uint SpecialNid { get { EnsureFileRecord(); return BinaryPrimitives.ReadUInt32BigEndian(Node.Data.AsSpan(DataOff + 44)); } }
    public ForkData DataFork { get { EnsureFileRecord(); return ForkData.Parse(Node.Data, DataOff + 88); } }

    public static CatalogEntry? ParseAt(BTree.Node node, int rec)
    {
        int start = node.RecStart[rec];
        if (start + 6 > node.Data.Length) return null;
        ushort keyLen = BinaryPrimitives.ReadUInt16BigEndian(node.Data.AsSpan(start));
        if (keyLen < 6) return null;
        ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(node.Data.AsSpan(start + 6));
        if (start + 8 + 2L * nameLen > node.Data.Length) return null;
        string name = Encoding.BigEndianUnicode.GetString(node.Data, start + 8, nameLen * 2);
        int dataOff = start + 2 + keyLen + (keyLen & 1);
        if (dataOff + 2 > node.Data.Length) return null;
        ushort recType = BinaryPrimitives.ReadUInt16BigEndian(node.Data.AsSpan(dataOff));

        return new CatalogEntry
        {
            ParentCnid = BinaryPrimitives.ReadUInt32BigEndian(node.Data.AsSpan(start + 2)),
            Name = name,
            RecType = recType,
            DataOff = dataOff,
            Node = node,
        };
    }
}

/// <summary>A fully-resolved path target for the WinFsp layer.</summary>
public sealed class ResolvedNode
{
    public bool IsDir;
    public uint Cnid;          // resolved cnid (follows hard links)
    public uint CreatedHfs;
    public uint ModifiedHfs;
    public ulong Size;             // data fork logical size (files)
    public ForkData Data = new();  // data fork (files only)
    public object ExtentsLock = new();
    public List<Extent>? Extents;  // lazily built (files)

    // directory listing cache (dirs)
    public object DirLock = new();
    public Dictionary<string, int>? DirIndex;   // name -> position, for marker positioning
    public List<Listing>? DirList;
}

/// <summary>One directory entry snapshot for enumeration.</summary>
public struct Listing
{
    public string Name;
    public Fsp.Interop.FileInfo Info;
}

public sealed class Catalog
{
    private static readonly string[] PrivateRootNames =
    {
        "\0\0\0\0HFS+ Private Data",
        ".HFS+ Private Directory Data\r",
        ".journal",
        ".journal_info_block",
    };

    private const uint FileTypeHardLinkFile = 0x686C6E6B; // 'hlnk'
    private const uint FileTypeHardLinkDir = 0x66647270;  // 'fdrp'
    private const uint CreatorHfsPlus = 0x6866732B;       // 'hfs+'

    private readonly HfsVolume _vol;
    private readonly BTree _btree;
    private uint? _metaDirCnid;   // "\0\0\0\0HFS+ Private Data" folder (file inodes)
    private uint? _dirMetaCnid;   // ".HFS+ Private Directory Data\r" folder (dir inodes)
    private readonly Dictionary<uint, CatalogEntry> _inodeCache = new();
    private readonly object _sync = new();

    public Catalog(HfsVolume vol, BTree btree)
    {
        _vol = vol;
        _btree = btree;
    }

    public bool IsPrivateRootName(string name)
    {
        foreach (var p in PrivateRootNames)
            if (string.Equals(name, p, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Enumerate the children of a folder CNID in catalog order. Streaming.</summary>
    public IEnumerable<CatalogEntry> EnumerateFolder(uint folderCnid)
    {
        var (node, rec) = _btree.PositionByParent(folderCnid);
        while (node != null)
        {
            while (rec < node.NumRecs)
            {
                uint p = BTree.CatalogKeyParent(node, rec);
                if (p > folderCnid) yield break;
                if (p == folderCnid)
                {
                    var e = CatalogEntry.ParseAt(node, rec);
                    if (e != null &&
                        e.RecType is not (CatalogEntry.RecFolderThread or CatalogEntry.RecFileThread))
                        yield return e;
                }
                rec++;
            }
            if (node.FLink == 0) yield break;
            node = _btree.GetNode(node.FLink);
            rec = 0;
        }
    }

    private static bool NameMatches(string a, string b, bool caseSensitive) =>
        caseSensitive ? string.Equals(a, b, StringComparison.Ordinal)
                      : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Look up a child entry by name. Fast path: direct descent (exact for ASCII names);
    /// fallback: full folder scan (correct under any collation).</summary>
    public CatalogEntry? LookupChild(uint parent, string name, bool caseSensitive)
    {
        var node = DescendByFullName(parent, name, caseSensitive);
        if (node != null)
        {
            foreach (var e in ScanNodeForChild(node, parent, name, caseSensitive))
                return e;
        }
        foreach (var e in EnumerateFolder(parent))
            if (NameMatches(e.Name, name, caseSensitive) &&
                e.RecType is CatalogEntry.RecFolder or CatalogEntry.RecFile)
                return e;
        return null;
    }

    private BTree.Node? DescendByFullName(uint parent, string name, bool caseSensitive)
    {
        var node = _btree.GetNode(_btree.RootNode);
        int guard = 0;
        while (node.Kind == BTree.KindIndex)
        {
            if (++guard > 128) throw new IOException("btree descent too deep");
            int chosen = -1;
            for (int i = 0; i < node.NumRecs; i++)
            {
                uint p = BTree.CatalogKeyParent(node, i);
                if (p < parent) { chosen = i; continue; }
                if (p > parent) break;
                var e = CatalogEntry.ParseAt(node, i);
                if (e == null) break;
                if (CompareName(e.Name, name, caseSensitive) <= 0) chosen = i;
                else break;
            }
            uint child = chosen < 0 ? BTree.IndexChild(node, 0) : BTree.IndexChild(node, chosen);
            node = _btree.GetNode(child);
        }
        return node.Kind == BTree.KindLeaf ? node : null;
    }

    private static int CompareName(string a, string b, bool caseSensitive) =>
        caseSensitive ? string.CompareOrdinal(a, b)
                      : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<CatalogEntry> ScanNodeForChild(BTree.Node node, uint parent, string name, bool caseSensitive)
    {
        for (int i = 0; i < node.NumRecs; i++)
        {
            if (BTree.CatalogKeyParent(node, i) != parent) continue;
            var e = CatalogEntry.ParseAt(node, i);
            if (e != null && e.RecType is CatalogEntry.RecFolder or CatalogEntry.RecFile &&
                NameMatches(e.Name, name, caseSensitive))
                yield return e;
        }
    }

    private uint MetaDirCnid()
    {
        if (_metaDirCnid == null)
        {
            var e = LookupChild(HfsVolume.CNID_ROOT_FOLDER, "\0\0\0\0HFS+ Private Data", false);
            if (e == null || !e.IsFolder) throw new IOException("HFS+ Private Data folder not found");
            _metaDirCnid = e.Cnid;
        }
        return _metaDirCnid.Value;
    }

    private uint DirMetaCnid()
    {
        if (_dirMetaCnid == null)
        {
            var e = LookupChild(HfsVolume.CNID_ROOT_FOLDER, ".HFS+ Private Directory Data\r", false);
            if (e == null || !e.IsFolder) throw new IOException("HFS+ Private Directory Data folder not found");
            _dirMetaCnid = e.Cnid;
        }
        return _dirMetaCnid.Value;
    }

    public bool IsFileHardLink(CatalogEntry e) =>
        e.IsFile && e.FileType == FileTypeHardLinkFile && e.Creator == CreatorHfsPlus;

    public bool IsDirHardLink(CatalogEntry e) =>
        e.IsFile && e.FileType == FileTypeHardLinkDir && e.Creator == CreatorHfsPlus;

    /// <summary>Resolve a 'hlnk' file record to its real iNode file record.</summary>
    public CatalogEntry? ResolveFileHardLink(uint inodeNum)
    {
        lock (_sync)
        {
            if (_inodeCache.TryGetValue(inodeNum, out var hit)) return hit;
        }
        var real = LookupChild(MetaDirCnid(), "iNode" + inodeNum.ToString(System.Globalization.CultureInfo.InvariantCulture), false);
        if (real != null)
        {
            lock (_sync) { _inodeCache[inodeNum] = real; }
        }
        return real;
    }

    /// <summary>Resolve a 'fdrp' directory-hard-link record to its target folder record.</summary>
    public CatalogEntry? ResolveDirHardLink(uint inodeNum) =>
        LookupChild(DirMetaCnid(), "dir_" + inodeNum.ToString(System.Globalization.CultureInfo.InvariantCulture), false);

    private ResolvedNode RootNode() => new() { IsDir = true, Cnid = HfsVolume.CNID_ROOT_FOLDER };

    private ResolvedNode BuildResolved(CatalogEntry rec, bool isDir)
    {
        if (isDir)
        {
            return new ResolvedNode
            {
                IsDir = true,
                Cnid = rec.Cnid,
                CreatedHfs = rec.CreatedHfs,
                ModifiedHfs = rec.ModifiedHfs,
            };
        }
        var fork = rec.DataFork;
        return new ResolvedNode
        {
            IsDir = false,
            Cnid = rec.Cnid,
            CreatedHfs = rec.CreatedHfs,
            ModifiedHfs = rec.ModifiedHfs,
            Data = fork,
            Size = fork.LogicalSize,
        };
    }

    /// <summary>Resolve an ordered path component list starting at the root folder.</summary>
    public ResolvedNode? ResolvePath(IReadOnlyList<string> comps, bool caseSensitive)
    {
        var current = RootNode();
        foreach (var comp in comps)
        {
            if (comp.Length == 0) continue;
            if (!current.IsDir) return null;

            var child = LookupChild(current.Cnid, comp, caseSensitive);
            if (child == null) return null;

            // hard links: directory links matter mid-path (e.g. Time Machine "Latest")
            CatalogEntry real = child;
            bool isDir = child.IsFolder;
            if (IsDirHardLink(child))
            {
                var t = ResolveDirHardLink(child.SpecialNid);
                if (t == null || !t.IsFolder) return null;
                real = t;
                isDir = true;
            }
            else if (IsFileHardLink(child))
            {
                var t = ResolveFileHardLink(child.SpecialNid);
                if (t == null || !t.IsFile) return null;
                real = t;
                isDir = false;
            }

            current = BuildResolved(real, isDir);
        }
        return current;
    }

    /// <summary>Read a byte range out of a resolved file node.</summary>
    public void ReadFile(ResolvedNode node, long offset, int count, byte[] buf, int bufOff)
    {
        if (node.Extents == null)
        {
            lock (node.ExtentsLock)
            {
                node.Extents ??= _vol.GetExtents(node.Cnid, HfsVolume.ForkDataFork, node.Data);
            }
        }
        _vol.ReadFork(node.Cnid, HfsVolume.ForkDataFork, node.Data, node.Extents, offset, count, buf, bufOff);
    }
}
