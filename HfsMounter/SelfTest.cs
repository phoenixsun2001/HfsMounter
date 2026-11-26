using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;

namespace HfsMounter;

/// <summary>
/// 内置自检：用合成镜像验证 GPT / HFS+ 解析链路，不需要管理员权限，也不接触任何真实磁盘。
/// 用于在没有 Mac 磁盘的机器上确认构建产物没坏。
/// </summary>
public static class SelfTest
{
    private static int _checks;
    private static int _failures;

    public static int Run()
    {
        Console.WriteLine("HfsMounter 自检（不需要管理员权限，也不会访问真实磁盘）");
        string dir;
        try
        {
            dir = CreateScratchDirectory();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"无法创建临时目录，自检中止: {ex.Message}");
            return 1;
        }
        try
        {
            Section("CRC32");
            TestCrc32();

            Section("GPT 分区表解析");
            TestGpt(dir, 512);
            TestGpt(dir, 4096);
            TestGptWithoutHfs(dir);
            TestNotAGpt(dir);

            Section("HFS+ 卷头探测");
            TestHfsProbe(dir);

            Section("HFS+ 卷打开（合成镜像）");
            TestVolumeOpen(dir);

            Section("配置与挂载状态");
            TestConfigRoundTrip(dir);
            TestStateRoundTrip(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine($"全部通过（{_checks} 项检查）。");
            return 0;
        }
        Console.WriteLine($"{_failures} 项失败（共 {_checks} 项检查）。");
        return 1;
    }

    /// <summary>依次尝试数据目录、临时目录、当前目录，取第一个可写的作为自检工作区。</summary>
    private static string CreateScratchDirectory()
    {
        string name = "selftest-" + Guid.NewGuid().ToString("N")[..8];
        string[] roots =
        {
            Path.Combine(AppPaths.DataDir, "selftest"),
            Path.Combine(Path.GetTempPath(), "hfsmounter"),
            Path.Combine(Directory.GetCurrentDirectory(), ".hfsmounter-selftest"),
        };
        var errors = new List<string>();
        foreach (string root in roots)
        {
            try
            {
                string candidate = Path.Combine(root, name);
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception ex)
            {
                errors.Add($"{root}: {ex.Message}");
            }
        }
        throw new IOException(string.Join("; ", errors));
    }

    // ---------------------------------------------------------------- 各项检查

    private static void TestCrc32()
    {
        // CRC-32/ISO-HDLC 的标准校验值
        uint crc = Crc32.Compute("123456789"u8);
        Check(crc == 0xCBF43926u, "CRC32(\"123456789\") == 0xCBF43926", $"实际 0x{crc:X8}");
    }

    private static void TestGpt(string directory, int sectorSize)
    {
        string path = Path.Combine(directory, $"gpt-{sectorSize}.img");
        BuildGptImage(path, sectorSize, hfsPartition: true);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool ok = Gpt.TryRead(fs, out GptTable? table, out string error);
        Check(ok, $"扇区 {sectorSize}: 能解析出 GPT", error);
        if (!ok || table == null) return;

        Check(table.SectorSize == sectorSize, $"扇区 {sectorSize}: 扇区大小判定正确", $"实际 {table.SectorSize}");
        Check(table.Partitions.Count == 1, $"扇区 {sectorSize}: 解析出 1 个分区", $"实际 {table.Partitions.Count}");

        GptPartition? hfs = table.Partitions.FirstOrDefault(p =>
            string.Equals(p.TypeGuid, Gpt.HfsPartitionTypeGuid, StringComparison.OrdinalIgnoreCase));
        Check(hfs != null, $"扇区 {sectorSize}: 识别出 Apple HFS+ 类型 GUID");

        if (hfs == null) return;
        Check(hfs.OffsetBytes == 40L * sectorSize, $"扇区 {sectorSize}: 分区起始偏移正确", $"实际 {hfs.OffsetBytes}");
        Check(hfs.LengthBytes == 61L * sectorSize, $"扇区 {sectorSize}: 分区长度正确", $"实际 {hfs.LengthBytes}");
        Check(hfs.Name == "Macintosh HD", $"扇区 {sectorSize}: 分区名解码正确", hfs.Name);
    }

    private static void TestGptWithoutHfs(string directory)
    {
        string path = Path.Combine(directory, "gpt-ntfs.img");
        BuildGptImage(path, 512, hfsPartition: false);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool ok = Gpt.TryRead(fs, out GptTable? table, out string error);
        Check(ok, "无 HFS 分区的磁盘仍能解析 GPT", error);
        Check(table != null && table.Partitions.Count == 1, "无 HFS 分区的磁盘解析出 1 个分区");
        Check(table != null && table.Partitions.All(p => p.TypeGuid != Gpt.HfsPartitionTypeGuid),
              "无 HFS 分区时不会误判成 Apple HFS+");
    }

    private static void TestNotAGpt(string directory)
    {
        string path = Path.Combine(directory, "not-a-gpt.img");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        {
            fs.SetLength(1 << 20);
        }

        using var check = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool ok = Gpt.TryRead(check, out _, out string error);
        Check(!ok, "保护性 MBR/空盘不会被当成 GPT", error);
        Check(!string.IsNullOrEmpty(error), "失败时给出原因");
    }

    private static void TestHfsProbe(string directory)
    {
        string path = BuildHfsImage(directory);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        bool ok = HfsProbe.TryRead(fs, 0, out HfsVolumeHeader header);
        Check(ok, "合成镜像的卷头能被识别");
        Check(header.BlockSize == 4096, "卷头块大小 = 4096", $"实际 {header.BlockSize}");
        Check(header.TotalBytes == 1024UL * 4096, "卷头总容量正确", $"实际 {header.TotalBytes}");
        Check(!header.IsHfsx, "HFS+ 不报告为 HFSX");
        Check(!header.IsJournaled, "未开日志的卷 IsJournaled = false");

        // 破坏签名后必须拒绝
        byte[] raw = File.ReadAllBytes(path);
        raw[1024] = 0x00;
        string bad = Path.Combine(directory, "bad-signature.img");
        File.WriteAllBytes(bad, raw);
        using var badFs = new FileStream(bad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Check(!HfsProbe.TryRead(badFs, 0, out _), "签名错误时拒绝该卷");
    }

    private static void TestVolumeOpen(string directory)
    {
        string path = BuildHfsImage(directory);

        using (var disk = new RawDisk(path, 0, new FileInfo(path).Length))
        {
            var volume = new HfsVolume(disk);
            volume.Open();
            Check(volume.BlockSize == 4096, "卷打开后块大小 = 4096", $"实际 {volume.BlockSize}");
            Check(volume.TotalBytes == 1024UL * 4096, "卷打开后总容量正确", $"实际 {volume.TotalBytes}");
            Check(volume.Cat != null, "目录树（Catalog）已建立");

            int count = volume.Cat!.EnumerateFolder(HfsVolume.CNID_ROOT_FOLDER).Count();
            Check(count == 0, "空根目录枚举出 0 项", $"实际 {count}");
        }

        // 把 GPT 镜像当 HFS+ 打开必须失败
        string gpt = Path.Combine(directory, "gpt-512.img");
        bool threw = false;
        try
        {
            using var disk = new RawDisk(gpt, 0, new FileInfo(gpt).Length);
            var volume = new HfsVolume(disk);
            volume.Open();
        }
        catch (IOException)
        {
            threw = true;
        }
        Check(threw, "非 HFS+ 卷在打开时抛出 IOException");
    }

    private static void TestConfigRoundTrip(string directory)
    {
        string path = Path.Combine(directory, "config.json");
        var config = new AppConfig
        {
            MountRoot = @"D:\MacDisk",
            PollSeconds = 7,
            MaxMounts = 2,
            LinkerLetter = "Y:",
            AutoOpenExplorer = false,
            MinLogLevel = LogLevel.Debug,
            LogRetainDays = 3,
        };
        config.Save(path);
        Check(File.Exists(path), "配置文件已写出", path);

        AppConfig loaded = AppConfig.Load(path);
        Check(loaded.MountRoot == @"D:\MacDisk", "MountRoot 往返正确", loaded.MountRoot);
        Check(loaded.PollSeconds == 7, "PollSeconds 往返正确", loaded.PollSeconds.ToString());
        Check(loaded.MaxMounts == 2, "MaxMounts 往返正确", loaded.MaxMounts.ToString());
        Check(loaded.LinkerLetter == "Y:", "LinkerLetter 往返正确", loaded.LinkerLetter);
        Check(!loaded.AutoOpenExplorer, "AutoOpenExplorer 往返正确");
        Check(loaded.MinLogLevel == LogLevel.Debug, "MinLogLevel 往返正确", loaded.MinLogLevel.ToString());
        Check(loaded.LogRetainDays == 3, "LogRetainDays 往返正确", loaded.LogRetainDays.ToString());
    }

    private static void TestStateRoundTrip(string directory)
    {
        string path = Path.Combine(directory, "state.json");
        var state = new MountState();
        state.Paths[@"disk-guid/part-guid"] = @"C:\MacDisk";
        state.Paths[@"disk-guid/part-guid-2"] = @"C:\MacDisk2";
        state.Save(path);

        MountState loaded = MountState.Load(path);
        Check(loaded.Paths.Count == 2, "挂载状态条目数往返正确", loaded.Paths.Count.ToString());
        Check(loaded.Paths.TryGetValue(@"disk-guid/part-guid", out string? dir) && dir == @"C:\MacDisk",
              "挂载状态内容往返正确", dir);
    }

    // ---------------------------------------------------------------- 合成镜像

    /// <summary>造一个带保护性 GPT 的镜像；entrySize=128、128 个分区项。</summary>
    private static void BuildGptImage(string path, int sectorSize, bool hfsPartition)
    {
        const int sectors = 128;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        fs.SetLength((long)sectors * sectorSize);

        byte[] header = new byte[sectorSize];
        "EFI PART"u8.CopyTo(header);
        WriteU32LE(header, 8, 0x00010000);       // revision
        WriteU32LE(header, 12, 92);              // headerSize
        WriteU32LE(header, 16, 0);               // header CRC：0 = 不校验
        WriteU64LE(header, 24, 1);               // currentLBA
        WriteU64LE(header, 32, sectors - 1);     // backupLBA
        WriteU64LE(header, 40, 34);              // firstUsableLBA
        WriteU64LE(header, 48, sectors - 34);    // lastUsableLBA
        Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray().CopyTo(header, 56);
        WriteU64LE(header, 72, 2);               // 分区项表 LBA
        WriteU32LE(header, 80, 128);             // numEntries
        WriteU32LE(header, 84, 128);             // entrySize
        WriteU32LE(header, 88, 0);               // 分区项表 CRC：0 = 不校验
        fs.Position = sectorSize;
        fs.Write(header);

        byte[] entries = new byte[128 * 128];
        string typeGuid = hfsPartition
            ? Gpt.HfsPartitionTypeGuid
            : "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"; // Microsoft 基本数据分区
        Guid.Parse(typeGuid).ToByteArray().CopyTo(entries, 0);
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToByteArray().CopyTo(entries, 16);
        WriteU64LE(entries, 32, 40);             // firstLBA
        WriteU64LE(entries, 40, 100);            // lastLBA
        Encoding.Unicode.GetBytes("Macintosh HD").CopyTo(entries, 56);
        fs.Position = 2L * sectorSize;
        fs.Write(entries);
    }

    /// <summary>造一个最小可解析的 HFS+ 卷：卷头 + 空的 catalog B-tree + 空的 extents B-tree。</summary>
    private static string BuildHfsImage(string directory)
    {
        const int blockSize = 4096;
        const int totalBlocks = 1024;
        string path = Path.Combine(directory, "hfs-volume.img");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        fs.SetLength((long)blockSize * totalBlocks);

        byte[] vh = new byte[HfsProbe.VolumeHeaderLength];
        WriteU16BE(vh, 0, HfsVolume.SIG_HFSP);
        WriteU32BE(vh, 4, 0);                            // attributes：未开日志
        WriteU32BE(vh, 40, blockSize);
        WriteU32BE(vh, 44, totalBlocks);
        WriteU32BE(vh, 48, totalBlocks - 100);
        WriteFork(vh, 192, (ulong)blockSize, 1, startBlock: 12, blockCount: 1);          // extents overflow
        WriteFork(vh, 272, (ulong)(8 * blockSize), 8, startBlock: 4, blockCount: 8);     // catalog
        fs.Position = HfsProbe.VolumeHeaderOffset;
        fs.Write(vh);

        // catalog B-tree 头节点（fork 内节点 0）
        byte[] catalogHeader = NewNode(kind: 1, treeDepth: 1, rootNode: 1, firstLeaf: 1,
                                       lastLeaf: 1, nodeSize: blockSize, maxKeyLength: 516, totalNodes: 8);
        fs.Position = 4L * blockSize;
        fs.Write(catalogHeader);

        // catalog 根叶子节点（fork 内节点 1）：空目录
        fs.Position = 5L * blockSize;
        fs.Write(NewNode(kind: 0xFF, numRecs: 0));

        // extents overflow B-tree 头节点
        byte[] extentsHeader = NewNode(kind: 1, treeDepth: 1, rootNode: 1, firstLeaf: 1,
                                       lastLeaf: 1, nodeSize: blockSize, maxKeyLength: 10, totalNodes: 1);
        fs.Position = 12L * blockSize;
        fs.Write(extentsHeader);

        return path;
    }

    private static byte[] NewNode(byte kind, int numRecs = 0, int treeDepth = 0, uint rootNode = 1,
                                  uint firstLeaf = 1, uint lastLeaf = 1, int nodeSize = 4096,
                                  int maxKeyLength = 516, uint totalNodes = 1)
    {
        byte[] node = new byte[4096];
        node[8] = kind;                          // FLink/BLink 都是 0
        WriteU16BE(node, 10, numRecs);
        if (kind == 0xFF) return node;           // 叶子节点到此为止

        int h = 14;                              // 节点头 14 字节之后是 BTreeHeaderRec
        WriteU16BE(node, h + 0, treeDepth);
        WriteU32BE(node, h + 2, rootNode);
        WriteU32BE(node, h + 6, 0);              // leafRecords
        WriteU32BE(node, h + 10, firstLeaf);
        WriteU32BE(node, h + 14, lastLeaf);
        WriteU16BE(node, h + 18, nodeSize);
        WriteU16BE(node, h + 20, maxKeyLength);
        WriteU32BE(node, h + 22, totalNodes);
        if (numRecs == 0) WriteU16BE(node, 10, 3); // 头节点固定 3 条记录
        return node;
    }

    private static void WriteFork(byte[] buffer, int offset, ulong logicalSize, uint totalBlocks,
                                  uint startBlock, uint blockCount)
    {
        WriteU64BE(buffer, offset, logicalSize);
        WriteU32BE(buffer, offset + 8, 0);        // clumpSize
        WriteU32BE(buffer, offset + 12, totalBlocks);
        WriteU32BE(buffer, offset + 16, startBlock);   // 第一个 extent
        WriteU32BE(buffer, offset + 20, blockCount);
    }

    // ---------------------------------------------------------------- 小工具

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    private static void Check(bool condition, string description, string? detail = null)
    {
        _checks++;
        if (condition)
        {
            Console.WriteLine($"  [通过] {description}");
            return;
        }
        _failures++;
        Console.WriteLine($"  [失败] {description}{(string.IsNullOrEmpty(detail) ? "" : $"（{detail}）")}");
    }

    private static void WriteU16BE(byte[] b, int o, int v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), (ushort)v);
    private static void WriteU32BE(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);
    private static void WriteU64BE(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(o), v);
    private static void WriteU32LE(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void WriteU64LE(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
}
