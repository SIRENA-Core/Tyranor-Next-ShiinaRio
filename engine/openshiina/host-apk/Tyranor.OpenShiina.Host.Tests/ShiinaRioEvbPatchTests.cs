using System.Buffers.Binary;
using System.Text;
using Tyranor.OpenShiina.Host.Shared;
using Xunit;

namespace Tyranor.OpenShiina.Host.Tests;

public sealed class ShiinaRioEvbPatchTests : IDisposable
{
    private readonly string _tempRoot;

    public ShiinaRioEvbPatchTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "tyn-evb-tests-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // 测试清理失败可忽略
        }
    }

    [Fact]
    public void AplibDecompressesEmptyAndPureLiterals()
    {
        Assert.Empty(EvbVirtualFileExtractor.DecompressAplib([]));

        var writer = new AplibTestEncoder();
        byte[] expected = "HelloShiina!"u8.ToArray();
        writer.WriteLiteral(expected[0]);
        for (int i = 1; i < expected.Length; i++)
        {
            writer.WriteLiteralTag(expected[i]);
        }
        writer.WriteEof();

        byte[] actual = EvbVirtualFileExtractor.DecompressAplib(writer.ToArray());
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AplibDecompressesShortMediumLongAndRepeatedMatches()
    {
        // 验证：字面量 + 4-bit 短偏移(含 offs=0 与 offs>0) + 7-bit 中匹配(含重叠 RLE) + gamma 长匹配(含重复偏移与大偏移 >= 32000)
        var writer = new AplibTestEncoder();
        var expected = new List<byte>();

        // 1) 首字节 'A'
        writer.WriteLiteral((byte)'A');
        expected.Add((byte)'A');

        // 2) 4-bit 短偏移 offs=0 -> 输出 0x00
        writer.WriteShort4Bit(0);
        expected.Add(0x00);

        // 3) 4-bit 短偏移 offs=2 -> 回溯 2 字节得 'A'
        writer.WriteShort4Bit(2);
        expected.Add((byte)'A');

        // 4) 7-bit 中匹配 offs=1, len=3 (重叠复制 'A' 三次 -> "AAA")
        writer.WriteMedium7Bit(offset: 1, length: 3);
        expected.Add((byte)'A');
        expected.Add((byte)'A');
        expected.Add((byte)'A');

        // 5) 写入一段字面量用于长匹配测试
        byte[] pattern = "ShiinaRio-EVB-Test-"u8.ToArray();
        foreach (byte b in pattern)
        {
            writer.WriteLiteralTag(b);
            expected.Add(b);
        }

        // 6) gamma 长匹配 (lwm == 0): offs = pattern.Length (< 128, gamma_len = 2 => 实际 len = 4)
        writer.WriteGammaMatch(offset: pattern.Length, gammaLength: 2, lwm: 0);
        for (int i = 0; i < 4; i++)
        {
            expected.Add(expected[expected.Count - pattern.Length]);
        }

        // 7) 写入一个字面量把 lwm 重置为 0，随后使用重复偏移 (gamma=2, lwm=0) 复用 lastOffset
        writer.WriteLiteralTag((byte)'!');
        expected.Add((byte)'!');
        writer.WriteRepeatLastOffset(length: 3);
        for (int i = 0; i < 3; i++)
        {
            expected.Add(expected[expected.Count - pattern.Length]);
        }

        // 8) 填充到超过 32000 字节以测试 offs >= 1280 与 offs >= 32000 长度补偿分支
        int padNeeded = 32010 - expected.Count;
        writer.WriteLiteralTag((byte)'Z');
        expected.Add((byte)'Z');
        for (int i = 0; i < padNeeded; i++)
        {
            writer.WriteMedium7Bit(offset: 1, length: 3);
            expected.Add((byte)'Z');
            expected.Add((byte)'Z');
            expected.Add((byte)'Z');
            if (expected.Count >= 32010)
            {
                break;
            }
        }

        int bigOffset = expected.Count - 1;
        Assert.True(bigOffset >= 32000);
        writer.WriteLiteralTag((byte)'X');
        expected.Add((byte)'X');
        // lwm == 0, bigOffset+1 >= 32000, gammaLength = 2 -> 实际长度 = 2 + 1(>=32000) + 1(>=1280) = 4
        writer.WriteGammaMatch(offset: bigOffset + 1, gammaLength: 2, lwm: 0);
        for (int i = 0; i < 4; i++)
        {
            expected.Add(expected[expected.Count - (bigOffset + 1)]);
        }

        writer.WriteEof();

        byte[] actual = EvbVirtualFileExtractor.DecompressAplib(writer.ToArray());
        Assert.Equal(expected.ToArray(), actual);
    }

    [Fact]
    public void AplibRejectsCorruptTruncatedAndOutOfBoundsStreams()
    {
        // 截断无 EOF 标记
        Assert.Throws<InvalidDataException>(() => EvbVirtualFileExtractor.DecompressAplib([(byte)'A', 0x00]));

        // 4-bit 偏移越界
        var badShort = new AplibTestEncoder();
        badShort.WriteLiteral((byte)'A');
        badShort.WriteShort4Bit(5);
        badShort.WriteEof();
        Assert.Throws<InvalidDataException>(() => EvbVirtualFileExtractor.DecompressAplib(badShort.ToArray()));

        // 7-bit 偏移越界
        var badMedium = new AplibTestEncoder();
        badMedium.WriteLiteral((byte)'A');
        badMedium.WriteMedium7Bit(offset: 10, length: 2);
        badMedium.WriteEof();
        Assert.Throws<InvalidDataException>(() => EvbVirtualFileExtractor.DecompressAplib(badMedium.ToArray()));

        // 超出 maxOutputBytes 防炸弹
        var bomb = new AplibTestEncoder();
        bomb.WriteLiteral((byte)'A');
        bomb.WriteMedium7Bit(offset: 1, length: 3);
        bomb.WriteEof();
        Assert.Throws<InvalidDataException>(() => EvbVirtualFileExtractor.DecompressAplib(bomb.ToArray(), maxOutputBytes: 2));
    }

    [Fact]
    public void HasEnigmaSectionDistinguishesEvbPeNormalPeAndNonPe()
    {
        string evbExe = Path.Combine(_tempRoot, "evb.exe");
        string plainExe = Path.Combine(_tempRoot, "plain.exe");
        string notPe = Path.Combine(_tempRoot, "notpe.bin");
        string missing = Path.Combine(_tempRoot, "missing.exe");

        File.WriteAllBytes(evbExe, BuildSyntheticEvbPe([]));
        File.WriteAllBytes(plainExe, BuildSyntheticPeWithSection(".rdata", "hello"u8.ToArray()));
        File.WriteAllBytes(notPe, "NOT_A_PE_FILE"u8.ToArray());

        Assert.True(EvbVirtualFileExtractor.HasEnigmaSection(evbExe));
        Assert.False(EvbVirtualFileExtractor.HasEnigmaSection(plainExe));
        Assert.False(EvbVirtualFileExtractor.HasEnigmaSection(notPe));
        Assert.False(EvbVirtualFileExtractor.HasEnigmaSection(missing));
    }

    [Fact]
    public void ExtractOverlayAssetsExtractsValidFilesAndBlocksNativeAndTraversal()
    {
        byte[] ttfData = [0x00, 0x01, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44];
        byte[] s25Part1 = new byte[65536];
        for (int i = 0; i < s25Part1.Length; i++)
        {
            s25Part1[i] = (byte)(i % 251);
        }
        byte[] s25Part2 = "S25_TAIL_PAYLOAD_AAAAAAAAAAAAAAAA"u8.ToArray();
        byte[] s25Full = [.. s25Part1, .. s25Part2];
        byte[] txtData = "ShiinaRio 中文剧本测试内容\nLine2\n"u8.ToArray();

        var specs = new List<SyntheticEvbEntry>
        {
            new("%DEFAULT FOLDER%", "ROUNDED.ttf", ttfData, CompressMode.Uncompressed),
            new("%DEFAULT FOLDER%/d", "TITLE.s25", s25Full, CompressMode.ChunkedMixed),
            new("%DEFAULT FOLDER%/p", "01_C_00.TXT", txtData, CompressMode.ChunkedAplib),
            new("%DEFAULT FOLDER%", "empty.txt", [], CompressMode.Uncompressed),
            new("%DEFAULT FOLDER%", "RioShiinaPatch.dll", [0x4D, 0x5A, 0x90], CompressMode.Uncompressed),
            new("%DEFAULT FOLDER%", "patcher.exe", [0x4D, 0x5A, 0x00], CompressMode.Uncompressed),
            new("%DEFAULT FOLDER%", "helper.bat", "@echo off"u8.ToArray(), CompressMode.Uncompressed),
            new("%DEFAULT FOLDER%", "../evil.txt", "pwned"u8.ToArray(), CompressMode.Uncompressed),
            new("%DEFAULT FOLDER%/..", "escape2.txt", "pwned2"u8.ToArray(), CompressMode.Uncompressed),
            new("%DEFAULT FOLDER%", "bad:name.txt", "colon"u8.ToArray(), CompressMode.Uncompressed),
            new("%SystemFolder%", "system_skip.txt", "skip"u8.ToArray(), CompressMode.Uncompressed),
        };

        string exePath = Path.Combine(_tempRoot, "game_chs.exe");
        File.WriteAllBytes(exePath, BuildSyntheticEvbPe(specs));

        string outParent = Path.Combine(_tempRoot, "game");
        string destDir = Path.Combine(outParent, "translation");
        Directory.CreateDirectory(outParent);

        var extracted = EvbVirtualFileExtractor.ExtractOverlayAssets(exePath, destDir);

        Assert.Equal(
            ["ROUNDED.ttf", "d/TITLE.s25", "empty.txt", "p/01_C_00.TXT"],
            extracted
        );
        Assert.Equal(ttfData, File.ReadAllBytes(Path.Combine(destDir, "ROUNDED.ttf")));
        Assert.Equal(s25Full, File.ReadAllBytes(Path.Combine(destDir, "d", "TITLE.s25")));
        Assert.Equal(txtData, File.ReadAllBytes(Path.Combine(destDir, "p", "01_C_00.TXT")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(destDir, "empty.txt")));

        Assert.False(File.Exists(Path.Combine(destDir, "RioShiinaPatch.dll")));
        Assert.False(File.Exists(Path.Combine(destDir, "patcher.exe")));
        Assert.False(File.Exists(Path.Combine(destDir, "helper.bat")));
        Assert.False(File.Exists(Path.Combine(destDir, "system_skip.txt")));
        Assert.False(File.Exists(Path.Combine(outParent, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(outParent, "escape2.txt")));
    }

    [Fact]
    public void SyncExtractsCachesPrioritizesAndCleansUpStateMachine()
    {
        string gameDir = Path.Combine(_tempRoot, "sync_game");
        Directory.CreateDirectory(gameDir);

        // 0) 无 EVB 补丁
        var noPatch = ShiinaRioEvbTranslationSync.Sync(gameDir, null);
        Assert.Equal(EvbTranslationSyncStatus.NoEvbPatch, noPatch.Status);

        // 放一个子目录备份 EVB 与一个顶层 unins 干扰 EVB，验证都不会被误选
        string backupDir = Path.Combine(gameDir, "backup");
        Directory.CreateDirectory(backupDir);
        File.WriteAllBytes(
            Path.Combine(backupDir, "backup_chs.exe"),
            BuildSyntheticEvbPe([new("%DEFAULT FOLDER%", "wrong.txt", "wrong"u8.ToArray(), CompressMode.Uncompressed)])
        );
        File.WriteAllBytes(
            Path.Combine(gameDir, "unins000.exe"),
            BuildSyntheticEvbPe([new("%DEFAULT FOLDER%", "unins.txt", "wrong"u8.ToArray(), CompressMode.Uncompressed)])
        );
        Assert.Equal(EvbTranslationSyncStatus.NoEvbPatch, ShiinaRioEvbTranslationSync.Sync(gameDir, null).Status);

        // 1) 放入顶层汉化 EVB EXE -> 首次提取
        byte[] scriptV1 = "LINE1_V1\n"u8.ToArray();
        string exeV1 = Path.Combine(gameDir, "GAME_CHS_20200918.EXE");
        File.WriteAllBytes(
            exeV1,
            BuildSyntheticEvbPe([new("%DEFAULT FOLDER%/p", "01.TXT", scriptV1, CompressMode.ChunkedAplib)])
        );

        var first = ShiinaRioEvbTranslationSync.Sync(gameDir, null);
        Assert.Equal(EvbTranslationSyncStatus.Extracted, first.Status);
        Assert.Equal("GAME_CHS_20200918.EXE", first.SourceExeName);
        Assert.Equal(1, first.FileCount);

        string extractedFile = Path.Combine(gameDir, "translation", "p", "01.TXT");
        string markerFile = Path.Combine(gameDir, "translation", ShiinaRioEvbTranslationSync.MarkerFileName);
        Assert.Equal(scriptV1, File.ReadAllBytes(extractedFile));
        Assert.True(File.Exists(markerFile));

        var sentinelTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(extractedFile, sentinelTime);

        // 2) 再次同步 -> 命中 CacheHit 且不覆写文件时间戳
        var cached = ShiinaRioEvbTranslationSync.Sync(gameDir, null);
        Assert.Equal(EvbTranslationSyncStatus.CacheHit, cached.Status);
        Assert.Equal(sentinelTime, File.GetLastWriteTimeUtc(extractedFile));

        // 3) 多个 EVB EXE 共存时优先选更新且带汉化关键词的文件，或 launchTarget 显式指定的文件
        byte[] scriptV2 = "LINE1_V2\n"u8.ToArray();
        string exeV2 = Path.Combine(gameDir, "GAME_CHS_20201017.EXE");
        File.WriteAllBytes(
            exeV2,
            BuildSyntheticEvbPe([new("%DEFAULT FOLDER%/p", "02.TXT", scriptV2, CompressMode.Uncompressed)])
        );
        File.SetLastWriteTimeUtc(exeV1, new DateTime(2020, 9, 18, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(exeV2, new DateTime(2020, 10, 17, 0, 0, 0, DateTimeKind.Utc));

        var upgraded = ShiinaRioEvbTranslationSync.Sync(gameDir, null);
        Assert.Equal(EvbTranslationSyncStatus.Extracted, upgraded.Status);
        Assert.Equal("GAME_CHS_20201017.EXE", upgraded.SourceExeName);
        Assert.False(File.Exists(extractedFile));
        Assert.Equal(scriptV2, File.ReadAllBytes(Path.Combine(gameDir, "translation", "p", "02.TXT")));

        // 显式 launchTarget 指向 v1
        var explicitV1 = ShiinaRioEvbTranslationSync.Sync(gameDir, "game_chs_20200918.exe");
        Assert.Equal(EvbTranslationSyncStatus.Extracted, explicitV1.Status);
        Assert.Equal("GAME_CHS_20200918.EXE", explicitV1.SourceExeName);
        Assert.Equal(scriptV1, File.ReadAllBytes(extractedFile));

        // 4) 移走全部有效顶层 EVB EXE -> 自动清理托管文件与空 translation 目录
        File.Delete(exeV1);
        File.Delete(exeV2);

        var cleaned = ShiinaRioEvbTranslationSync.Sync(gameDir, null);
        Assert.Equal(EvbTranslationSyncStatus.Cleaned, cleaned.Status);
        Assert.False(Directory.Exists(Path.Combine(gameDir, "translation")));
    }

    [Fact]
    public void SyncPreservesManualUserTranslationFolderWithoutMarker()
    {
        string gameDir = Path.Combine(_tempRoot, "manual_game");
        string translationDir = Path.Combine(gameDir, "translation");
        Directory.CreateDirectory(translationDir);

        string userFile = Path.Combine(translationDir, "text.tsv");
        byte[] userBytes = "id\toriginal\ttranslation\n"u8.ToArray();
        File.WriteAllBytes(userFile, userBytes);

        // 即使目录内存在 EVB EXE，只要无 marker 且 translation 非空，就必须原样保留用户文件
        File.WriteAllBytes(
            Path.Combine(gameDir, "PATCH_CHS.exe"),
            BuildSyntheticEvbPe([new("%DEFAULT FOLDER%", "text.tsv", "overwritten"u8.ToArray(), CompressMode.Uncompressed)])
        );

        var result = ShiinaRioEvbTranslationSync.Sync(gameDir, null);
        Assert.Equal(EvbTranslationSyncStatus.PreservedUserTranslation, result.Status);
        Assert.Equal(userBytes, File.ReadAllBytes(userFile));
        Assert.False(File.Exists(Path.Combine(translationDir, ShiinaRioEvbTranslationSync.MarkerFileName)));
    }

    [Fact]
    public void TrySyncSafeDegradesGracefullyOnCorruptEvbWithoutLeavingArtifacts()
    {
        string gameDir = Path.Combine(_tempRoot, "corrupt_game");
        Directory.CreateDirectory(gameDir);

        // 构造一个有 .enigma1 节但内部树损坏的 EVB EXE
        byte[] badSection = new byte[128];
        "EVB\0"u8.CopyTo(badSection.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(badSection.AsSpan(16 + 0x40), 99999); // 超出节尾

        string badExe = Path.Combine(gameDir, "CORRUPT_CHS.exe");
        File.WriteAllBytes(badExe, BuildSyntheticPeWithSection(".enigma1", badSection));

        var result = ShiinaRioEvbTranslationSync.TrySyncSafe(gameDir, null);
        Assert.Equal(EvbTranslationSyncStatus.Failed, result.Status);
        Assert.Equal("CORRUPT_CHS.exe", result.SourceExeName);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.False(Directory.Exists(Path.Combine(gameDir, "translation")));
    }

    private enum CompressMode
    {
        Uncompressed,
        ChunkedAplib,
        ChunkedMixed,
    }

    private sealed record SyntheticEvbEntry(
        string FolderPath,
        string FileName,
        byte[] Data,
        CompressMode Mode
    );

    private static byte[] BuildSyntheticPeWithSection(string sectionName, byte[] sectionPayload)
    {
        const uint peOffset = 0x80;
        const ushort optHeaderSize = 0x60;
        const uint sectionTableOffset = peOffset + 24 + optHeaderSize;
        const uint sectionRawOffset = 0x200;

        byte[] pe = new byte[sectionRawOffset + sectionPayload.Length];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x3C, 4), peOffset);

        "PE\0\0"u8.CopyTo(pe.AsSpan((int)peOffset, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan((int)peOffset + 6, 2), 1); // NumberOfSections
        BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan((int)peOffset + 20, 2), optHeaderSize);

        Span<byte> secHdr = pe.AsSpan((int)sectionTableOffset, 40);
        byte[] nameBytes = Encoding.ASCII.GetBytes(sectionName);
        nameBytes.AsSpan(0, Math.Min(8, nameBytes.Length)).CopyTo(secHdr.Slice(0, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(secHdr.Slice(8, 4), (uint)sectionPayload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(secHdr.Slice(12, 4), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(secHdr.Slice(16, 4), (uint)sectionPayload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(secHdr.Slice(20, 4), sectionRawOffset);

        sectionPayload.CopyTo(pe.AsSpan((int)sectionRawOffset));
        return pe;
    }

    private static byte[] BuildSyntheticEvbPe(List<SyntheticEvbEntry> entries)
    {
        // 构建目录树与数据区
        var treeBody = new MemoryStream();
        var dataBlob = new MemoryStream();

        uint nextNodeId = 1;
        var createdFolders = new Dictionary<string, uint>(StringComparer.Ordinal);

        // 先按 FolderPath 分组（确保 %DEFAULT FOLDER% 与其它顶层文件夹有序创建）
        void EnsureFolderChain(string folderPath)
        {
            if (createdFolders.ContainsKey(folderPath))
            {
                return;
            }

            var parts = folderPath.Split('/');
            string current = "";
            for (int i = 0; i < parts.Length; i++)
            {
                current = i == 0 ? parts[i] : $"{current}/{parts[i]}";
                if (createdFolders.ContainsKey(current))
                {
                    continue;
                }

                WriteFolderNode(treeBody, nextNodeId++, (uint)i, parts[i]);
                createdFolders[current] = (uint)i;
            }
        }

        // 为保证栈深度嵌套正确，按 folderPath 分组后连续写同一目录下的文件
        foreach (var group in entries.GroupBy(static e => e.FolderPath, StringComparer.Ordinal))
        {
            EnsureFolderChain(group.Key);
            uint fileDepth = (uint)group.Key.Split('/').Length;
            foreach (var entry in group)
            {
                byte[] stored = EncodePayload(entry.Data, entry.Mode);
                ulong offset = (ulong)dataBlob.Position;
                dataBlob.Write(stored, 0, stored.Length);
                WriteFileNode(
                    treeBody,
                    nextNodeId++,
                    fileDepth,
                    entry.FileName,
                    (ulong)entry.Data.Length,
                    offset,
                    (ulong)stored.Length
                );
            }
        }

        byte[] treeBytes = treeBody.ToArray();
        uint treeSize = (uint)(0x0F + treeBytes.Length);
        byte[] section = new byte[32 + 0x44 + treeSize + dataBlob.Length];

        int evbOff = 32;
        "EVB\0"u8.CopyTo(section.AsSpan(evbOff, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(evbOff + 0x40, 4), treeSize);
        treeBytes.CopyTo(section.AsSpan(evbOff + 0x53));
        dataBlob.ToArray().CopyTo(section.AsSpan(evbOff + 0x44 + (int)treeSize));

        return BuildSyntheticPeWithSection(".enigma1", section);
    }

    private static byte[] EncodePayload(byte[] plain, CompressMode mode)
    {
        if (plain.Length == 0 || mode == CompressMode.Uncompressed)
        {
            return plain;
        }

        const int chunkMax = 65536;
        int numChunks = (plain.Length + chunkMax - 1) / chunkMax;
        int dataStart = numChunks * 12;

        var encodedChunks = new List<byte[]>(numChunks);
        for (int i = 0; i < numChunks; i++)
        {
            int start = i * chunkMax;
            int len = Math.Min(chunkMax, plain.Length - start);
            ReadOnlySpan<byte> slice = plain.AsSpan(start, len);

            if (mode == CompressMode.ChunkedMixed && i == 0)
            {
                encodedChunks.Add(slice.ToArray());
            }
            else
            {
                encodedChunks.Add(EncodeAplibLiterals(slice));
            }
        }

        int totalLen = dataStart + encodedChunks.Sum(static c => c.Length);
        byte[] blob = new byte[totalLen];
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(0, 8), (ulong)dataStart);

        int hdrPos = 8;
        ulong curOffset = (ulong)dataStart;
        for (int i = 0; i < numChunks; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(hdrPos, 4), (uint)encodedChunks[i].Length);
            hdrPos += 4;
            if (i < numChunks - 1)
            {
                curOffset += (ulong)encodedChunks[i].Length;
                BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(hdrPos, 8), curOffset);
                hdrPos += 8;
            }
        }

        int writePos = dataStart;
        for (int i = 0; i < numChunks; i++)
        {
            encodedChunks[i].CopyTo(blob.AsSpan(writePos));
            writePos += encodedChunks[i].Length;
        }

        return blob;
    }

    private static byte[] EncodeAplibLiterals(ReadOnlySpan<byte> plain)
    {
        var enc = new AplibTestEncoder();
        enc.WriteLiteral(plain[0]);
        for (int i = 1; i < plain.Length; i++)
        {
            enc.WriteLiteralTag(plain[i]);
        }
        enc.WriteEof();
        return enc.ToArray();
    }

    private static void WriteFolderNode(Stream s, uint id, uint depth, string name)
    {
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(0, 4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), depth);
        s.Write(hdr);

        byte[] nameBytes = Encoding.Unicode.GetBytes(name + "\0");
        s.Write(nameBytes, 0, nameBytes.Length);
        s.WriteByte(3);

        Span<byte> tail = stackalloc byte[29];
        s.Write(tail);
    }

    private static void WriteFileNode(
        Stream s,
        uint id,
        uint depth,
        string name,
        ulong origSize,
        ulong dataOffset,
        ulong storedSize
    )
    {
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(0, 4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), depth);
        s.Write(hdr);

        byte[] nameBytes = Encoding.Unicode.GetBytes(name + "\0");
        s.Write(nameBytes, 0, nameBytes.Length);
        s.WriteByte(2);

        Span<byte> body = stackalloc byte[57];
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(2, 8), origSize);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(2 + 8 + 24 + 7, 8), dataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(body.Slice(2 + 8 + 24 + 7 + 8, 8), storedSize);
        s.Write(body);
    }

    /// <summary>
    /// 用于单元测试构建各种合法与边界 aPLib 码流的辅助编码器。
    /// </summary>
    private sealed class AplibTestEncoder
    {
        private readonly List<byte> _buf = [];
        private int _tagPos = -1;
        private int _bitCount = 8;

        public void WriteLiteral(byte b) => _buf.Add(b);

        public void WriteLiteralTag(byte b)
        {
            WriteBit(0);
            _buf.Add(b);
        }

        public void WriteShort4Bit(int offset)
        {
            WriteBit(1);
            WriteBit(1);
            WriteBit(1);
            for (int shift = 3; shift >= 0; shift--)
            {
                WriteBit((offset >> shift) & 1);
            }
        }

        public void WriteMedium7Bit(int offset, int length)
        {
            WriteBit(1);
            WriteBit(1);
            WriteBit(0);
            _buf.Add((byte)((offset << 1) | (length - 2)));
        }

        public void WriteGammaMatch(int offset, int gammaLength, int lwm)
        {
            WriteBit(1);
            WriteBit(0);
            int high = offset >> 8;
            int low = offset & 0xFF;
            int gammaOff = high + (lwm == 0 ? 3 : 2);
            WriteGamma(gammaOff);
            _buf.Add((byte)low);
            WriteGamma(gammaLength);
        }

        public void WriteRepeatLastOffset(int length)
        {
            WriteBit(1);
            WriteBit(0);
            WriteGamma(2);
            WriteGamma(length);
        }

        public void WriteEof()
        {
            WriteBit(1);
            WriteBit(1);
            WriteBit(0);
            _buf.Add(0x00);
        }

        public byte[] ToArray() => [.. _buf];

        private void WriteBit(int bit)
        {
            if (_bitCount == 8)
            {
                _tagPos = _buf.Count;
                _buf.Add(0);
                _bitCount = 0;
            }

            if ((bit & 1) != 0)
            {
                _buf[_tagPos] |= (byte)(1 << (7 - _bitCount));
            }
            _bitCount++;
        }

        private void WriteGamma(int value)
        {
            // value >= 2: 最高位 1 隐含，随后每 bit 之间插入 1 个继续位，最后一位后接 0
            int bits = 0;
            int tmp = value;
            while (tmp > 1)
            {
                bits++;
                tmp >>= 1;
            }

            for (int i = bits - 1; i >= 0; i--)
            {
                WriteBit((value >> i) & 1);
                WriteBit(i > 0 ? 1 : 0);
            }
        }
    }
}
