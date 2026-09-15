using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;

namespace HfsMounter;

/// <summary>Read-only HFS+ file system exposed through WinFsp.</summary>
public sealed class HfsFileSystem : FileSystemBase
{
    private const int NtSuccess = 0;
    private const int NtAccessDenied = unchecked((int)0xC0000022);
    private const int NtObjectNameNotFound = unchecked((int)0xC0000034);
    private const int NtObjectPathNotFound = unchecked((int)0xC000003A);
    private const int NtInvalidDeviceRequest = unchecked((int)0xC0000010);
    private const int NtIoDeviceError = unchecked((int)0xC0000185);

    // file-specific access bits we must refuse on a read-only volume
    private const uint AccessWriteMask =
        0x00000002 | // FILE_WRITE_DATA
        0x00000004 | // FILE_APPEND_DATA
        0x00000010 | // FILE_WRITE_EA
        0x00000100 | // FILE_WRITE_ATTRIBUTES
        0x00010000 | // DELETE
        0x00040000 | // WRITE_DAC
        0x00080000;  // WRITE_OWNER

    private const long HfsEpochDiff = 2082844800;           // seconds between 1904 and 1970
    private const long FileTimeOffset = 116444736000000000; // 1601 -> 1970 in 100ns ticks

    private readonly HfsVolume _vol;
    private readonly byte[] _sd;

    public HfsFileSystem(HfsVolume vol)
    {
        _vol = vol;
        // read + execute for Everyone; nothing grants write so Windows itself caps access
        var sd = new RawSecurityDescriptor("D:P(A;;FRFX;;;WD)");
        _sd = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_sd, 0);
    }

    private Catalog Cat => _vol.Cat!;

    private static ulong HfsToWin32(uint hfs)
    {
        if (hfs == 0) return 0;
        long unix = (long)hfs - HfsEpochDiff;
        if (unix < 0) return 0;
        return (ulong)(unix * 10_000_000 + FileTimeOffset);
    }

    private static Fsp.Interop.FileInfo MakeInfo(bool isDir, ulong size, uint cnid, uint created, uint modified)
    {
        var fi = new Fsp.Interop.FileInfo
        {
            FileAttributes = (uint)(isDir ? System.IO.FileAttributes.Directory
                                          : System.IO.FileAttributes.ReadOnly | System.IO.FileAttributes.Normal),
            AllocationSize = isDir ? 0 : (size + (ulong)_Align - 1) & ~((ulong)_Align - 1),
            FileSize = size,
            CreationTime = HfsToWin32(created),
            LastAccessTime = HfsToWin32(modified),
            LastWriteTime = HfsToWin32(modified),
            ChangeTime = HfsToWin32(modified),
            IndexNumber = cnid,
            HardLinks = 0,
            ReparseTag = 0,
        };
        return fi;
    }

    private static readonly long _Align = 4096;

    private static string[] PathComponents(string fileName)
    {
        var s = fileName.Replace('/', '\\');
        s = s.TrimStart('\\');
        if (s.Length == 0) return Array.Empty<string>();
        return s.Split('\\');
    }

    public override Int32 Init(Object Host)
    {
        var host = (FileSystemHost)Host;
        host.SectorSize = 512;
        host.SectorsPerAllocationUnit = (ushort)(_vol.BlockSize / 512);
        host.MaxComponentLength = 255;
        host.UnicodeOnDisk = true;
        host.CaseSensitiveSearch = _vol.CaseSensitive;
        host.CasePreservedNames = true;
        host.PersistentAcls = false;
        host.PostCleanupWhenModifiedOnly = false;
        host.FileSystemName = "HFS+";
        return FileSystemBase.STATUS_SUCCESS;
    }

    public override Int32 GetVolumeInfo(out Fsp.Interop.VolumeInfo VolumeInfo)
    {
        VolumeInfo = new Fsp.Interop.VolumeInfo
        {
            TotalSize = _vol.TotalBytes,
            FreeSize = 0, // read-only
        };
        VolumeInfo.SetVolumeLabel("MacDisk");
        return FileSystemBase.STATUS_SUCCESS;
    }

    private ResolvedNode? TryResolve(string fileName, out int status)
    {
        var comps = PathComponents(fileName);
        var node = Cat.ResolvePath(comps, _vol.CaseSensitive);
        if (node == null)
        {
            status = NtObjectPathNotFound;
            return null;
        }
        status = NtSuccess;
        return node;
    }

    public override Int32 GetSecurityByName(String FileName, out UInt32 FileAttributes, ref Byte[] SecurityDescriptor)
    {
        FileAttributes = 0;
        var node = TryResolve(FileName, out int status);
        if (node == null) return status;

        FileAttributes = node.IsDir
            ? (uint)System.IO.FileAttributes.Directory
            : (uint)(System.IO.FileAttributes.ReadOnly | System.IO.FileAttributes.Normal);
        SecurityDescriptor ??= _sd;
        return FileSystemBase.STATUS_SUCCESS;
    }

    public override Int32 Open(String FileName, UInt32 CreateOptions, UInt32 GrantedAccess,
        out Object FileNode, out Object FileDesc, out Fsp.Interop.FileInfo FileInfo, out String NormalizedName)
    {
        FileNode = null!;
        FileDesc = null!;
        FileInfo = default;
        NormalizedName = null!;

        if ((GrantedAccess & AccessWriteMask) != 0)
            return NtAccessDenied;

        var node = TryResolve(FileName, out int status);
        if (node == null) return status;

        FileDesc = node;
        FileInfo = MakeInfo(node.IsDir, node.Size, node.Cnid, node.CreatedHfs, node.ModifiedHfs);
        NormalizedName = FileName;
        return FileSystemBase.STATUS_SUCCESS;
    }

    public override Int32 Read(Object FileNode, Object FileDesc, IntPtr Buffer, UInt64 Offset,
        UInt32 Length, out UInt32 BytesTransferred)
    {
        BytesTransferred = 0;
        var node = (ResolvedNode)FileDesc;
        if (node.IsDir) return NtInvalidDeviceRequest;
        if (Offset >= node.Size) return FileSystemBase.STATUS_SUCCESS;

        int len = (int)Math.Min(Length, node.Size - Offset);
        var buf = new byte[len];
        try
        {
            Cat.ReadFile(node, (long)Offset, len, buf, 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"read error at {Offset}+{len} (cnid {node.Cnid}): {ex.Message}");
            return NtIoDeviceError;
        }
        Marshal.Copy(buf, 0, Buffer, len);
        BytesTransferred = (uint)len;
        return FileSystemBase.STATUS_SUCCESS;
    }

    public override Int32 GetFileInfo(Object FileNode, Object FileDesc, out Fsp.Interop.FileInfo FileInfo)
    {
        var node = (ResolvedNode)FileDesc;
        FileInfo = MakeInfo(node.IsDir, node.Size, node.Cnid, node.CreatedHfs, node.ModifiedHfs);
        return FileSystemBase.STATUS_SUCCESS;
    }

    public override Int32 Flush(Object FileNode, Object FileDesc, out Fsp.Interop.FileInfo FileInfo)
    {
        var node = FileDesc as ResolvedNode;
        FileInfo = node == null
            ? default
            : MakeInfo(node.IsDir, node.Size, node.Cnid, node.CreatedHfs, node.ModifiedHfs);
        return FileSystemBase.STATUS_SUCCESS;
    }

    public override Int32 GetSecurity(Object FileNode, Object FileDesc, ref Byte[] SecurityDescriptor)
    {
        SecurityDescriptor ??= _sd;
        return FileSystemBase.STATUS_SUCCESS;
    }

    public override Boolean ReadDirectoryEntry(Object FileNode, Object FileDesc, String Pattern, String Marker,
        ref Object Context, out String FileName, out Fsp.Interop.FileInfo FileInfo)
    {
        var node = (ResolvedNode)FileDesc;

        List<Listing> list;
        lock (node.DirLock)
        {
            if (node.DirList == null)
            {
                node.DirList = BuildListing(node);
                var idx = new Dictionary<string, int>(node.DirList.Count, StringComparer.Ordinal);
                for (int i = 0; i < node.DirList.Count; i++) idx[node.DirList[i].Name] = i;
                node.DirIndex = idx;
            }
            list = node.DirList;
        }

        int start = 0;
        if (Context is int pos)
        {
            start = pos;
        }
        else if (!string.IsNullOrEmpty(Marker))
        {
            if (node.DirIndex != null && node.DirIndex.TryGetValue(Marker, out int mi))
                start = mi + 1;
            else
                start = list.FindIndex(x => string.CompareOrdinal(x.Name, Marker) > 0);
            if (start < 0) start = list.Count;
        }

        if (start >= list.Count)
        {
            Context = start;
            FileName = null!;
            FileInfo = default;
            return false;
        }

        Context = start + 1;
        FileName = list[start].Name;
        FileInfo = list[start].Info;
        return true;
    }

    private List<Listing> BuildListing(ResolvedNode dir)
    {
        var result = new List<Listing>();
        result.Add(new Listing
        {
            Name = ".",
            Info = MakeInfo(true, 0, dir.Cnid, dir.CreatedHfs, dir.ModifiedHfs),
        });
        if (dir.Cnid != HfsVolume.CNID_ROOT_FOLDER)
        {
            // parent time info unknown cheaply; use the folder's own times
            result.Add(new Listing
            {
                Name = "..",
                Info = MakeInfo(true, 0, 0, dir.CreatedHfs, dir.ModifiedHfs),
            });
        }

        foreach (var child in Cat.EnumerateFolder(dir.Cnid))
        {
            if (dir.Cnid == HfsVolume.CNID_ROOT_FOLDER && Cat.IsPrivateRootName(child.Name))
                continue;

            bool isDir = child.IsFolder;
            uint cnid = child.Cnid;
            uint created = child.CreatedHfs;
            uint modified = child.ModifiedHfs;
            ulong size = 0;

            if (!isDir)
            {
                if (Cat.IsFileHardLink(child))
                {
                    var real = Cat.ResolveFileHardLink(child.SpecialNid);
                    if (real != null && real.IsFile)
                    {
                        cnid = real.Cnid;
                        created = real.CreatedHfs;
                        modified = real.ModifiedHfs;
                        size = real.DataFork.LogicalSize;
                    }
                }
                else if (Cat.IsDirHardLink(child))
                {
                    var real = Cat.ResolveDirHardLink(child.SpecialNid);
                    if (real != null && real.IsFolder)
                    {
                        isDir = true;
                        cnid = real.Cnid;
                        created = real.CreatedHfs;
                        modified = real.ModifiedHfs;
                    }
                }
                else
                {
                    size = child.DataFork.LogicalSize;
                }
            }

            result.Add(new Listing
            {
                Name = child.Name,
                Info = MakeInfo(isDir, size, cnid, created, modified),
            });
        }
        return result;
    }

    // ---- all mutating operations are denied on a read-only volume ----

    public override Int32 Create(String FileName, UInt32 CreateOptions, UInt32 GrantedAccess,
        UInt32 FileAttributes, Byte[] SecurityDescriptor, UInt64 AllocationSize,
        out Object FileNode, out Object FileDesc, out Fsp.Interop.FileInfo FileInfo, out String NormalizedName)
    {
        FileNode = null!; FileDesc = null!; FileInfo = default; NormalizedName = null!;
        return NtAccessDenied;
    }

    public override Int32 Overwrite(Object FileNode, Object FileDesc, UInt32 FileAttributes,
        Boolean ReplaceFileAttributes, UInt64 AllocationSize, out Fsp.Interop.FileInfo FileInfo)
    {
        FileInfo = default;
        return NtAccessDenied;
    }

    public override Int32 Write(Object FileNode, Object FileDesc, IntPtr Buffer, UInt64 Offset,
        UInt32 Length, Boolean WriteToEndOfFile, Boolean ConstrainedIo,
        out UInt32 BytesTransferred, out Fsp.Interop.FileInfo FileInfo)
    {
        BytesTransferred = 0; FileInfo = default;
        return NtAccessDenied;
    }

    public override Int32 SetBasicInfo(Object FileNode, Object FileDesc, UInt32 FileAttributes,
        UInt64 CreationTime, UInt64 LastAccessTime, UInt64 LastWriteTime, UInt64 ChangeTime,
        out Fsp.Interop.FileInfo FileInfo)
    {
        FileInfo = default;
        return NtAccessDenied;
    }

    public override Int32 SetFileSize(Object FileNode, Object FileDesc, UInt64 NewSize,
        Boolean SetAllocationSize, out Fsp.Interop.FileInfo FileInfo)
    {
        FileInfo = default;
        return NtAccessDenied;
    }

    public override Int32 CanDelete(Object FileNode, Object FileDesc, String FileName) => NtAccessDenied;

    public override Int32 Rename(Object FileNode, Object FileDesc, String FileName, String NewFileName,
        Boolean ReplaceIfExists) => NtAccessDenied;

    public override Int32 SetDelete(Object FileNode, Object FileDesc, String FileName, Boolean DeleteFile) =>
        NtAccessDenied;

    public override Int32 SetSecurity(Object FileNode, Object FileDesc, AccessControlSections Sections,
        Byte[] SecurityDescriptor) => NtAccessDenied;

    public override Int32 ExceptionHandler(Exception ex)
    {
        Console.Error.WriteLine("FS exception: " + ex);
        return NtIoDeviceError;
    }

    public override void Unmounted(Object Host)
    {
        Console.WriteLine("卷已卸载。");
    }
}
