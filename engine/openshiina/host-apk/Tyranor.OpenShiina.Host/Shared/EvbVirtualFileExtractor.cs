using System.Buffers;
using System.Buffers.Binary;
using System.Security;
using System.Text;

namespace Tyranor.OpenShiina.Host.Shared;

/// <summary>
/// Enigma Virtual Box (EVB) 单文件可执行程序虚拟文件系统解析与提取器。
/// 仅依赖纯 .NET 标准库，支持极速 PE `.enigma1` 探针、aPLib 分块解压与严格路径/炸弹防护。
/// </summary>
public static class EvbVirtualFileExtractor
{
    public const long MaxSingleFileSizeBytes = 64L * 1024 * 1024;
    public const long MaxTotalExtractedBytes = 256L * 1024 * 1024;
    public const int MaxExtractedFileCount = 4096;

    private const int ChunkPlaintextSize = 65536;
    private const int MaxTreeSizeBytes = 16 * 1024 * 1024;
    private const int MaxChunkCount = 2048;
    private const int MaxSectionSearchBufferBytes = 64 * 1024;

    private static readonly byte[] EnigmaSectionNameBytes = ".enigma1"u8.ToArray();
    private static readonly byte[] EvbMagicBytes = "EVB\0"u8.ToArray();

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll",
        ".exe",
        ".sys",
        ".ocx",
        ".scr",
        ".bat",
        ".cmd",
        ".com",
        ".msi",
        ".pdb",
        ".reg",
    };

    private sealed record EvbFileEntry(
        string RelativePath,
        ulong OriginalSize,
        ulong DataOffset,
        ulong StoredSize
    );

    /// <summary>
    /// 极速探针：仅读取 PE DOS 头、PE 头与节表，判断是否包含 `.enigma1` 节。
    /// 非 PE、损坏文件、无权限或不存在时安全返回 false，不抛出异常。
    /// </summary>
    public static bool HasEnigmaSection(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(
                exePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            return TryFindEnigmaSection(fs, out _, out _);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException
            or ArgumentException
        )
        {
            return false;
        }
    }

    /// <summary>
    /// 从 EVB 可执行文件中提取 `%DEFAULT FOLDER%` 下的安全资产到目标目录，返回实际写入的相对路径（正斜杠、排序去重）。
    /// 若中途发生格式错误或资源超限异常，会自动清理本次已写出的半成品文件。
    /// </summary>
    public static IReadOnlyList<string> ExtractOverlayAssets(string exePath, string destinationDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);

        using var fs = new FileStream(
            exePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        return ExtractOverlayAssets(fs, destinationDir);
    }

    /// <summary>
    /// 从只读 PE 流中提取 `%DEFAULT FOLDER%` 下的安全资产到目标目录。
    /// </summary>
    public static IReadOnlyList<string> ExtractOverlayAssets(Stream peStream, string destinationDir)
    {
        ArgumentNullException.ThrowIfNull(peStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);
        if (!peStream.CanSeek || !peStream.CanRead)
        {
            throw new ArgumentException("输入流必须支持读取与定位。", nameof(peStream));
        }

        if (!TryFindEnigmaSection(peStream, out var sectionOffset, out var sectionSize))
        {
            throw new InvalidDataException("PE 文件中未找到 .enigma1 节。");
        }

        if (sectionSize <= 0 || sectionOffset < 0 || sectionOffset > peStream.Length || sectionSize > peStream.Length - sectionOffset)
        {
            throw new InvalidDataException(".enigma1 节原始区间越界或为空。");
        }

        var evbOffset = FindEvbMagicOffset(peStream, sectionOffset, sectionSize);
        if (evbOffset < 0)
        {
            throw new InvalidDataException(".enigma1 节中未找到 EVB\\0 魔数。");
        }

        if (evbOffset + 0x53L > sectionSize)
        {
            throw new InvalidDataException("EVB 头部长度不足，数据已截断。");
        }

        peStream.Position = sectionOffset + evbOffset + 0x40L;
        Span<byte> treeSizeBuf = stackalloc byte[4];
        ReadExact(peStream, treeSizeBuf);
        uint treeSize = BinaryPrimitives.ReadUInt32LittleEndian(treeSizeBuf);

        if (treeSize < 0x0F || treeSize > MaxTreeSizeBytes)
        {
            throw new InvalidDataException($"EVB 目录树大小异常：{treeSize}。");
        }

        long dataBase = evbOffset + 0x44L + treeSize;
        if (dataBase > sectionSize)
        {
            throw new InvalidDataException("EVB 目录树声明长度超出 .enigma1 节边界。");
        }

        int treeBodyLength = checked((int)(dataBase - (evbOffset + 0x53L)));
        var treeBytes = new byte[treeBodyLength];
        peStream.Position = sectionOffset + evbOffset + 0x53L;
        ReadExact(peStream, treeBytes);

        var entries = ParseEvbTree(treeBytes, sectionSize - dataBase);

        var destRootFull = Path.GetFullPath(destinationDir);
        var destRootWithSep = destRootFull.EndsWith(Path.DirectorySeparatorChar) || destRootFull.EndsWith(Path.AltDirectorySeparatorChar)
            ? destRootFull
            : destRootFull + Path.DirectorySeparatorChar;
        var rootExistedBefore = Directory.Exists(destRootFull);
        Directory.CreateDirectory(destRootFull);

        var writtenFullPaths = new List<string>();
        var createdSubDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writtenRelPaths = new List<string>();

        try
        {
            foreach (var entry in entries)
            {
                var osRel = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var targetPath = Path.GetFullPath(Path.Combine(destRootFull, osRel));
                if (!targetPath.StartsWith(destRootWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    // 二次防线：拦截任何可能越出目标目录的路径
                    continue;
                }

                var parentDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    TrackAndCreateDirectory(parentDir, destRootFull, createdSubDirs);
                }

                var payload = ReadAndExtractPayload(peStream, sectionOffset + dataBase, entry);
                var tempPath = targetPath + ".evb-tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllBytes(tempPath, payload);
                    File.Move(tempPath, targetPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }

                writtenFullPaths.Add(targetPath);
                writtenRelPaths.Add(entry.RelativePath);
            }
        }
        catch
        {
            CleanupPartialExtraction(writtenFullPaths, createdSubDirs, destRootFull, rootExistedBefore);
            throw;
        }

        return writtenRelPaths
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 校验条目相对路径是否安全（拒绝空名称、控制字符、冒号、绝对路径与 `.` / `..` 穿越段），并返回规范化正斜杠路径。
    /// </summary>
    public static bool TryNormalizeSafeRelativePath(string? rawRelPath, out string normalizedRelPath)
    {
        normalizedRelPath = "";
        if (string.IsNullOrWhiteSpace(rawRelPath))
        {
            return false;
        }

        if (rawRelPath.Contains('\0') || rawRelPath.Contains(':'))
        {
            return false;
        }

        foreach (char c in rawRelPath)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        if (rawRelPath.StartsWith('/') || rawRelPath.StartsWith('\\'))
        {
            return false;
        }

        var parts = rawRelPath.Split(['/', '\\']);
        if (parts.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < parts.Length; i++)
        {
            var seg = parts[i];
            if (string.IsNullOrWhiteSpace(seg) || seg is "." or "..")
            {
                return false;
            }
        }

        normalizedRelPath = string.Join('/', parts);
        return true;
    }

    /// <summary>
    /// 解压单块 aPLib 压缩数据。支持空输入、标准 4-bit/7-bit/gamma 回溯匹配与重叠拷贝，遇到截断或越界安全抛出 InvalidDataException。
    /// </summary>
    public static byte[] DecompressAplib(ReadOnlySpan<byte> compressed, int maxOutputBytes = (int)MaxSingleFileSizeBytes)
    {
        if (maxOutputBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        }

        if (compressed.Length == 0)
        {
            return [];
        }

        if (maxOutputBytes == 0)
        {
            throw new InvalidDataException("aPLib 解压输出超出允许上限（0 字节）。");
        }

        var reader = new AplibBitReader(compressed);
        var output = new List<byte>(Math.Min(compressed.Length * 2, Math.Min(maxOutputBytes, ChunkPlaintextSize)));
        output.Add(reader.ReadByte());

        int lastOffset = 0;
        int lwm = 0;

        while (true)
        {
            if (reader.GetBit() == 1)
            {
                if (reader.GetBit() == 1)
                {
                    if (reader.GetBit() == 1)
                    {
                        int offs = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            offs = (offs << 1) + reader.GetBit();
                        }

                        if (output.Count >= maxOutputBytes)
                        {
                            throw new InvalidDataException("aPLib 解压数据超出最大输出限制。");
                        }

                        if (offs == 0)
                        {
                            output.Add(0);
                        }
                        else
                        {
                            if (offs > output.Count)
                            {
                                throw new InvalidDataException($"aPLib 4-bit 短偏移越界：offs={offs}, outLen={output.Count}。");
                            }
                            output.Add(output[output.Count - offs]);
                        }

                        lwm = 0;
                    }
                    else
                    {
                        int b = reader.ReadByte();
                        int length = 2 + (b & 1);
                        int offs = b >> 1;
                        if (offs == 0)
                        {
                            break;
                        }

                        CopyMatch(output, offs, length, maxOutputBytes);
                        lastOffset = offs;
                        lwm = 1;
                    }
                }
                else
                {
                    int offs = reader.GetGamma();
                    if (lwm == 0 && offs == 2)
                    {
                        offs = lastOffset;
                        int length = reader.GetGamma();
                        CopyMatch(output, offs, length, maxOutputBytes);
                    }
                    else
                    {
                        int high = lwm == 0 ? offs - 3 : offs - 2;
                        if (high < 0 || high > (int.MaxValue >> 8))
                        {
                            throw new InvalidDataException($"aPLib gamma 高位偏移无效：{high}。");
                        }

                        offs = (high << 8) + reader.ReadByte();
                        int length = reader.GetGamma();
                        if (offs >= 32000)
                        {
                            length += 1;
                        }
                        if (offs >= 1280)
                        {
                            length += 1;
                        }
                        if (offs < 128)
                        {
                            length += 2;
                        }

                        CopyMatch(output, offs, length, maxOutputBytes);
                    }

                    lastOffset = offs;
                    lwm = 1;
                }
            }
            else
            {
                if (output.Count >= maxOutputBytes)
                {
                    throw new InvalidDataException("aPLib 解压数据超出最大输出限制。");
                }

                output.Add(reader.ReadByte());
                lwm = 0;
            }
        }

        return [.. output];
    }

    private static bool TryFindEnigmaSection(Stream stream, out long rawOffset, out long rawSize)
    {
        rawOffset = 0;
        rawSize = 0;

        if (!stream.CanSeek || !stream.CanRead || stream.Length < 0x40)
        {
            return false;
        }

        stream.Position = 0;
        Span<byte> dosHeader = stackalloc byte[0x40];
        if (!TryReadExact(stream, dosHeader))
        {
            return false;
        }

        if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            return false;
        }

        uint peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dosHeader.Slice(0x3C, 4));
        if (peOffset < 0x40 || peOffset > stream.Length - 24)
        {
            return false;
        }

        stream.Position = peOffset;
        Span<byte> coffHeader = stackalloc byte[24];
        if (!TryReadExact(stream, coffHeader))
        {
            return false;
        }

        if (coffHeader[0] != (byte)'P'
            || coffHeader[1] != (byte)'E'
            || coffHeader[2] != 0
            || coffHeader[3] != 0)
        {
            return false;
        }

        ushort numSections = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader.Slice(6, 2));
        ushort optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader.Slice(20, 2));
        long sectionTableOffset = (long)peOffset + 24 + optionalHeaderSize;

        if (numSections == 0
            || sectionTableOffset > stream.Length
            || (long)numSections * 40 > stream.Length - sectionTableOffset)
        {
            return false;
        }

        stream.Position = sectionTableOffset;
        Span<byte> sectionHeader = stackalloc byte[40];
        for (int i = 0; i < numSections; i++)
        {
            if (!TryReadExact(stream, sectionHeader))
            {
                return false;
            }

            if (IsEnigmaSectionName(sectionHeader.Slice(0, 8)))
            {
                rawSize = BinaryPrimitives.ReadUInt32LittleEndian(sectionHeader.Slice(16, 4));
                rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(sectionHeader.Slice(20, 4));
                return true;
            }
        }

        return false;
    }

    private static bool IsEnigmaSectionName(ReadOnlySpan<byte> rawName8)
    {
        int len = rawName8.Length;
        while (len > 0 && rawName8[len - 1] == 0)
        {
            len--;
        }

        return rawName8.Slice(0, len).SequenceEqual(EnigmaSectionNameBytes);
    }

    private static long FindEvbMagicOffset(Stream stream, long sectionOffset, long sectionSize)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxSectionSearchBufferBytes);
        try
        {
            long scanned = 0;
            int carry = 0;

            while (scanned < sectionSize)
            {
                int toRead = (int)Math.Min(MaxSectionSearchBufferBytes - carry, sectionSize - scanned);
                stream.Position = sectionOffset + scanned;
                ReadExact(stream, buffer.AsSpan(carry, toRead));

                int validLen = carry + toRead;
                int hit = buffer.AsSpan(0, validLen).IndexOf(EvbMagicBytes);
                if (hit >= 0)
                {
                    return scanned - carry + hit;
                }

                scanned += toRead;
                carry = Math.Min(3, validLen);
                if (carry > 0)
                {
                    Buffer.BlockCopy(buffer, validLen - carry, buffer, 0, carry);
                }
            }

            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<EvbFileEntry> ParseEvbTree(ReadOnlySpan<byte> treeBytes, long dataRegionSize)
    {
        if (dataRegionSize < 0)
        {
            throw new InvalidDataException("EVB 数据区长度为负。");
        }

        var entries = new List<EvbFileEntry>();
        var folderStack = new Stack<(uint Depth, bool InDefaultFolder, string RelativeDir)>();
        int pos = 0;
        long totalExtractedBytes = 0;

        while (pos < treeBytes.Length)
        {
            if (pos + 12 > treeBytes.Length)
            {
                throw new InvalidDataException($"EVB 节点头截断（pos={pos}）。");
            }

            _ = BinaryPrimitives.ReadUInt32LittleEndian(treeBytes.Slice(pos, 4));
            uint depth = BinaryPrimitives.ReadUInt32LittleEndian(treeBytes.Slice(pos + 4, 4));
            _ = BinaryPrimitives.ReadUInt32LittleEndian(treeBytes.Slice(pos + 8, 4));
            pos += 12;

            string name = ReadUtf16LeNullTerminated(treeBytes, ref pos);
            if (pos >= treeBytes.Length)
            {
                throw new InvalidDataException($"EVB 节点缺少类型字段（name={name}）。");
            }

            byte nodeType = treeBytes[pos++];

            while (folderStack.Count > 0 && folderStack.Peek().Depth >= depth)
            {
                folderStack.Pop();
            }

            bool hasParent = folderStack.Count > 0;
            bool parentInDefault = hasParent && folderStack.Peek().InDefaultFolder;
            string parentRel = hasParent ? folderStack.Peek().RelativeDir : "";

            if (nodeType == 3)
            {
                if (pos + 29 > treeBytes.Length)
                {
                    throw new InvalidDataException($"EVB 目录节点截断（name={name}）。");
                }
                pos += 29;

                if (!hasParent)
                {
                    bool isDefaultRoot = string.Equals(name, "%DEFAULT FOLDER%", StringComparison.OrdinalIgnoreCase);
                    folderStack.Push((depth, isDefaultRoot, ""));
                }
                else if (!parentInDefault)
                {
                    folderStack.Push((depth, false, ""));
                }
                else
                {
                    string combinedDir = string.IsNullOrEmpty(parentRel) ? name : $"{parentRel}/{name}";
                    folderStack.Push((depth, true, combinedDir));
                }
            }
            else if (nodeType == 2)
            {
                if (pos + 57 > treeBytes.Length)
                {
                    throw new InvalidDataException($"EVB 文件节点截断（name={name}）。");
                }

                pos += 2;
                ulong origSize = BinaryPrimitives.ReadUInt64LittleEndian(treeBytes.Slice(pos, 8));
                pos += 8;
                pos += 24;
                pos += 7;
                ulong dataOffset = BinaryPrimitives.ReadUInt64LittleEndian(treeBytes.Slice(pos, 8));
                ulong storedSize = BinaryPrimitives.ReadUInt64LittleEndian(treeBytes.Slice(pos + 8, 8));
                pos += 16;

                if (!parentInDefault)
                {
                    continue;
                }

                string rawRelPath = string.IsNullOrEmpty(parentRel) ? name : $"{parentRel}/{name}";
                if (!TryNormalizeSafeRelativePath(rawRelPath, out var safeRelPath))
                {
                    // 拦截非法或路径穿越条目
                    continue;
                }

                if (BlockedExtensions.Contains(Path.GetExtension(safeRelPath)))
                {
                    continue;
                }

                if (origSize > (ulong)MaxSingleFileSizeBytes)
                {
                    throw new InvalidDataException($"EVB 条目 {safeRelPath} 原始大小 {origSize} 超过单文件上限 {MaxSingleFileSizeBytes}。");
                }

                ulong maxAllowedStored = (ulong)MaxSingleFileSizeBytes + 1024 * 1024;
                if (storedSize > maxAllowedStored)
                {
                    throw new InvalidDataException($"EVB 条目 {safeRelPath} 存储大小 {storedSize} 超过安全上限。");
                }

                if (dataOffset > (ulong)dataRegionSize || storedSize > (ulong)dataRegionSize - dataOffset)
                {
                    throw new InvalidDataException($"EVB 条目 {safeRelPath} 数据区间越界（offset={dataOffset}, size={storedSize}）。");
                }

                if (entries.Count >= MaxExtractedFileCount)
                {
                    throw new InvalidDataException($"EVB 提取文件数量超过上限 {MaxExtractedFileCount}。");
                }

                checked
                {
                    totalExtractedBytes += (long)origSize;
                }
                if (totalExtractedBytes > MaxTotalExtractedBytes)
                {
                    throw new InvalidDataException($"EVB 提取总体积超过上限 {MaxTotalExtractedBytes}。");
                }

                entries.Add(new EvbFileEntry(safeRelPath, origSize, dataOffset, storedSize));
            }
            else
            {
                throw new InvalidDataException($"未知的 EVB 节点类型 {nodeType}（pos={pos - 1}）。");
            }
        }

        if (pos != treeBytes.Length)
        {
            throw new InvalidDataException($"EVB 目录树结束位置不一致：pos={pos} != len={treeBytes.Length}。");
        }

        return entries;
    }

    private static string ReadUtf16LeNullTerminated(ReadOnlySpan<byte> buf, ref int pos)
    {
        int start = pos;
        while (pos + 1 < buf.Length)
        {
            if (buf[pos] == 0 && buf[pos + 1] == 0)
            {
                string text = Encoding.Unicode.GetString(buf.Slice(start, pos - start));
                pos += 2;
                return text;
            }
            pos += 2;
        }

        throw new InvalidDataException($"EVB UTF-16LE 字符串未以 NUL 结尾（start={start}）。");
    }

    private static byte[] ReadAndExtractPayload(Stream peStream, long dataBaseAbsolute, EvbFileEntry entry)
    {
        if (entry.OriginalSize == 0)
        {
            return [];
        }

        int origSize = checked((int)entry.OriginalSize);
        int storedSize = checked((int)entry.StoredSize);
        if (storedSize == 0)
        {
            throw new InvalidDataException($"EVB 条目 {entry.RelativePath} 的 storedSize 为 0，但 origSize 为 {origSize}。");
        }

        var blob = new byte[storedSize];
        peStream.Position = dataBaseAbsolute + checked((long)entry.DataOffset);
        ReadExact(peStream, blob);

        return ExtractFilePayload(blob, origSize, entry.RelativePath);
    }

    private static byte[] ExtractFilePayload(ReadOnlySpan<byte> blob, int origSize, string relPath)
    {
        if (origSize == 0)
        {
            return [];
        }

        if (blob.Length == origSize)
        {
            return blob.ToArray();
        }

        if (blob.Length < 12)
        {
            throw new InvalidDataException($"EVB 压缩块过短（{relPath}）：len={blob.Length}。");
        }

        ulong dataStartU64 = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(0, 8));
        if (dataStartU64 < 12 || dataStartU64 % 12 != 0 || dataStartU64 > (ulong)blob.Length)
        {
            throw new InvalidDataException($"EVB 分块头 dataStart 非法（{relPath}）：dataStart={dataStartU64}, blobLen={blob.Length}。");
        }

        ulong numChunksU64 = dataStartU64 / 12;
        if (numChunksU64 > MaxChunkCount)
        {
            throw new InvalidDataException($"EVB 分块数量过多（{relPath}）：numChunks={numChunksU64}。");
        }

        int numChunks = (int)numChunksU64;
        int dataStart = (int)dataStartU64;
        var chunkSizes = new uint[numChunks];
        var chunkOffsets = new ulong[numChunks];
        chunkOffsets[0] = dataStartU64;

        int hdrPos = 8;
        for (int i = 0; i < numChunks; i++)
        {
            chunkSizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(hdrPos, 4));
            hdrPos += 4;
            if (i < numChunks - 1)
            {
                chunkOffsets[i + 1] = BinaryPrimitives.ReadUInt64LittleEndian(blob.Slice(hdrPos, 8));
                hdrPos += 8;
            }
        }

        if (hdrPos != dataStart)
        {
            throw new InvalidDataException($"EVB 分块头游标异常（{relPath}）：hdrPos={hdrPos} != dataStart={dataStart}。");
        }

        var result = new byte[origSize];
        int written = 0;

        for (int i = 0; i < numChunks; i++)
        {
            ulong offset = chunkOffsets[i];
            uint csz = chunkSizes[i];
            if (offset > (ulong)blob.Length || csz > (ulong)blob.Length - offset)
            {
                throw new InvalidDataException($"EVB 分块越界或截断（{relPath}, chunk={i}）。");
            }

            int remaining = origSize - written;
            if (remaining <= 0)
            {
                throw new InvalidDataException($"EVB 分块数量超出原始文件大小（{relPath}, chunk={i}）。");
            }

            int expectedPlainLen = Math.Min(ChunkPlaintextSize, remaining);
            var chunkSpan = blob.Slice((int)offset, (int)csz);

            if (csz == (uint)expectedPlainLen)
            {
                chunkSpan.CopyTo(result.AsSpan(written, expectedPlainLen));
                written += expectedPlainLen;
            }
            else
            {
                byte[] decoded = DecompressAplib(chunkSpan, expectedPlainLen);
                if (decoded.Length != expectedPlainLen)
                {
                    throw new InvalidDataException(
                        $"EVB 分块解压长度不匹配（{relPath}, chunk={i}）：actual={decoded.Length}, expected={expectedPlainLen}。"
                    );
                }

                decoded.AsSpan().CopyTo(result.AsSpan(written, expectedPlainLen));
                written += expectedPlainLen;
            }
        }

        if (written != origSize)
        {
            throw new InvalidDataException($"EVB 文件解压总长度不匹配（{relPath}）：actual={written}, expected={origSize}。");
        }

        return result;
    }

    private static void CopyMatch(List<byte> output, int offset, int length, int maxOutputBytes)
    {
        if (offset <= 0 || offset > output.Count)
        {
            throw new InvalidDataException($"aPLib 回溯偏移越界：offset={offset}, outLen={output.Count}。");
        }

        if (length <= 0 || output.Count + length > maxOutputBytes)
        {
            throw new InvalidDataException($"aPLib 匹配长度越界：length={length}, outLen={output.Count}, max={maxOutputBytes}。");
        }

        // 逐字节追加以正确处理 length > offset 的重叠重复复制
        for (int i = 0; i < length; i++)
        {
            output.Add(output[output.Count - offset]);
        }
    }

    private static void TrackAndCreateDirectory(string targetDir, string rootDir, HashSet<string> createdSubDirs)
    {
        var pending = new Stack<string>();
        var current = Path.GetFullPath(targetDir);
        while (!string.IsNullOrEmpty(current)
            && !string.Equals(current, rootDir, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(current))
        {
            pending.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            Directory.CreateDirectory(dir);
            createdSubDirs.Add(dir);
        }
    }

    private static void CleanupPartialExtraction(
        List<string> writtenFullPaths,
        HashSet<string> createdSubDirs,
        string destRootFull,
        bool rootExistedBefore
    )
    {
        foreach (var file in writtenFullPaths)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // 清理失败不覆盖原始解析异常
            }
        }

        foreach (var dir in createdSubDirs.OrderByDescending(static d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch
            {
                // 忽略清理错误
            }
        }

        if (!rootExistedBefore)
        {
            try
            {
                if (Directory.Exists(destRootFull) && !Directory.EnumerateFileSystemEntries(destRootFull).Any())
                {
                    Directory.Delete(destRootFull);
                }
            }
            catch
            {
                // 忽略清理错误
            }
        }
    }

    private static bool TryReadExact(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination.Slice(total));
            if (read == 0)
            {
                return false;
            }
            total += read;
        }
        return true;
    }

    private static void ReadExact(Stream stream, Span<byte> destination)
    {
        if (!TryReadExact(stream, destination))
        {
            throw new InvalidDataException("EVB 文件流意外到达结尾，数据已截断。");
        }
    }

    private ref struct AplibBitReader(ReadOnlySpan<byte> src)
    {
        private readonly ReadOnlySpan<byte> _src = src;
        private int _pos = 0;
        private byte _tag = 0;
        private int _bitCount = 0;

        public byte ReadByte()
        {
            if (_pos >= _src.Length)
            {
                throw new InvalidDataException("aPLib 数据流意外截断（读取字节越界）。");
            }
            return _src[_pos++];
        }

        public int GetBit()
        {
            if (_bitCount == 0)
            {
                _tag = ReadByte();
                _bitCount = 8;
            }

            _bitCount--;
            int bit = (_tag >> 7) & 1;
            _tag <<= 1;
            return bit;
        }

        public int GetGamma()
        {
            int res = 1;
            while (true)
            {
                if (res > (int.MaxValue >> 1))
                {
                    throw new InvalidDataException("aPLib gamma 编码溢出。");
                }
                res = (res << 1) + GetBit();
                if (GetBit() == 0)
                {
                    break;
                }
            }
            return res;
        }
    }
}
