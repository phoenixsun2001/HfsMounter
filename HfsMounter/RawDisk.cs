using System;
using System.Collections.Concurrent;
using System.IO;

namespace HfsMounter;

/// <summary>
/// Thread-safe aligned reader over a range of a raw physical disk.
/// All public reads are relative to the volume base offset.
/// Reads are sector-aligned (4 KiB) because Windows requires alignment on raw device handles.
/// </summary>
public sealed class RawDisk : IDisposable
{
    private const int Align = 4096;
    private readonly FileStream _fs;
    private readonly long _baseOffset;
    private readonly long _length;
    private readonly object _readLock = new();

    public RawDisk(string devicePath, long baseOffset, long length)
    {
        _fs = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.None);
        _baseOffset = baseOffset;
        _length = length;
    }

    public long Length => _length;

    /// <summary>Read count bytes at volume-relative offset into buf[bufOff..].</summary>
    public void Read(long offset, int count, byte[] buf, int bufOff)
    {
        if (offset < 0 || count < 0 || offset + count > _length)
            throw new IOException($"read out of range: off={offset} count={count} len={_length}");
        if (count == 0) return;

        lock (_readLock)
        {
            long absStart = _baseOffset + offset;
            long alignedStart = absStart & ~(long)(Align - 1);
            long alignedEnd = (absStart + count + (Align - 1)) & ~(long)(Align - 1);
            int skip = (int)(absStart - alignedStart);
            int total = (int)(alignedEnd - alignedStart);

            var tmp = new byte[total];
            _fs.Seek(alignedStart, SeekOrigin.Begin);
            int done = 0;
            while (done < total)
            {
                int n = _fs.Read(tmp, done, total - done);
                if (n <= 0) throw new IOException($"short read at {alignedStart + done}");
                done += n;
            }
            Buffer.BlockCopy(tmp, skip, buf, bufOff, count);
        }
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>LRU-free bounded cache of B-tree nodes keyed by (tree, node id).</summary>
public sealed class NodeCache
{
    private const int Cap = 100_000;
    private readonly ConcurrentDictionary<(int tree, uint node), byte[]> _map = new();

    public bool TryGet(int tree, uint node, out byte[] data) => _map.TryGetValue((tree, node), out data!);

    public void Put(int tree, uint node, byte[] data)
    {
        if (_map.Count >= Cap)
        {
            _map.Clear(); // crude bound; nodes are cheap to re-read via disk cache
        }
        _map[(tree, node)] = data;
    }
}
