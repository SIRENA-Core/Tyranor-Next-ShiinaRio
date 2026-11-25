using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tyranor.OpenShiina.Host.Shared;

/// <summary>
/// 宿主 EVB 单文件汉化覆盖同步状态。
/// </summary>
public enum EvbTranslationSyncStatus
{
    NoEvbPatch = 0,
    PreservedUserTranslation = 1,
    CacheHit = 2,
    Extracted = 3,
    Cleaned = 4,
    Failed = 5,
}

/// <summary>
/// EVB 单文件汉化覆盖同步结果记录。
/// </summary>
public sealed record EvbTranslationSyncResult(
    EvbTranslationSyncStatus Status,
    string? SourceExeName = null,
    int FileCount = 0,
    string? Message = null
);

/// <summary>
/// 宿主托管 EVB 单文件汉化补丁同步器：在游戏启动前安全维护 `&lt;gameDir&gt;/translation/` 与托管标记文件。
/// </summary>
public static class ShiinaRioEvbTranslationSync
{
    public const string TranslationFolderName = "translation";
    public const string MarkerFileName = ".tyranor-evb-overlay.json";
    private const int MarkerSchemaVersion = 1;

    private static readonly string[] IgnoredExePrefixes =
    [
        "unins",
        "setup",
        "config",
        "update",
        "install",
        "redist",
        "dxsetup",
    ];

    private static readonly string[] TranslationHintKeywords =
    [
        "chs",
        "cht",
        "cn",
        "zh",
        "sc",
        "tc",
        "han",
        "汉化",
        "中文",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class EvbOverlayMarker
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("sourceExeName")]
        public string SourceExeName { get; set; } = "";

        [JsonPropertyName("sourceFileSize")]
        public long SourceFileSize { get; set; }

        [JsonPropertyName("sourceLastWriteUtcTicks")]
        public long SourceLastWriteUtcTicks { get; set; }

        [JsonPropertyName("managedFiles")]
        public string[] ManagedFiles { get; set; } = [];
    }

    /// <summary>
    /// 按宿主规则从游戏目录顶层发现最优 EVB 汉化单文件 EXE。
    /// </summary>
    public static string? ResolveEvbCandidate(string gameDir, string? launchTarget)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            return null;
        }

        List<string> topLevelExes;
        try
        {
            topLevelExes = Directory
                .EnumerateFiles(gameDir, "*", SearchOption.TopDirectoryOnly)
                .Where(static path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (topLevelExes.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(launchTarget))
        {
            var trimmedTarget = launchTarget.Trim();
            if (!trimmedTarget.Contains('/') && !trimmedTarget.Contains('\\') && !trimmedTarget.Contains(".."))
            {
                var explicitMatch = topLevelExes.FirstOrDefault(
                    path => string.Equals(Path.GetFileName(path), trimmedTarget, StringComparison.OrdinalIgnoreCase)
                );
                if (explicitMatch is not null && EvbVirtualFileExtractor.HasEnigmaSection(explicitMatch))
                {
                    return explicitMatch;
                }
            }
        }

        var evbCandidates = topLevelExes
            .Where(static path => !IsIgnoredNonGameExe(Path.GetFileName(path)))
            .Where(EvbVirtualFileExtractor.HasEnigmaSection)
            .ToList();

        if (evbCandidates.Count == 0)
        {
            return null;
        }

        if (evbCandidates.Count == 1)
        {
            return evbCandidates[0];
        }

        return evbCandidates
            .OrderByDescending(static path => HasTranslationHint(Path.GetFileName(path)))
            .ThenByDescending(static path => SafeGetLastWriteUtcTicks(path))
            .ThenByDescending(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .First();
    }

    /// <summary>
    /// 容错同步入口：安全捕获一切解析或 IO 异常并返回 Failed 状态，绝不抛异常阻断游戏启动。
    /// </summary>
    public static EvbTranslationSyncResult TrySyncSafe(string gameDir, string? launchTarget)
    {
        try
        {
            return Sync(gameDir, launchTarget);
        }
        catch (Exception ex)
        {
            string? exeName = null;
            try
            {
                var candidate = ResolveEvbCandidate(gameDir, launchTarget);
                exeName = candidate is null ? null : Path.GetFileName(candidate);
            }
            catch
            {
                // 忽略二次探测异常
            }

            return new EvbTranslationSyncResult(
                EvbTranslationSyncStatus.Failed,
                exeName,
                0,
                ex.Message
            );
        }
    }

    /// <summary>
    /// 同步 `&lt;gameDir&gt;/translation/` 目录与 `.tyranor-evb-overlay.json` 托管状态。
    /// </summary>
    public static EvbTranslationSyncResult Sync(string gameDir, string? launchTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDir);

        var translationDir = Path.Combine(gameDir, TranslationFolderName);
        var markerPath = Path.Combine(translationDir, MarkerFileName);
        bool translationExists = Directory.Exists(translationDir);
        bool markerExists = File.Exists(markerPath);

        // 保护用户手工翻译铁律：目录非空且不存在宿主标记时绝对不触碰任何文件
        if (translationExists && !markerExists && IsDirectoryNonEmpty(translationDir))
        {
            return new EvbTranslationSyncResult(EvbTranslationSyncStatus.PreservedUserTranslation);
        }

        var candidate = ResolveEvbCandidate(gameDir, launchTarget);
        EvbOverlayMarker? oldMarker = markerExists ? TryReadMarker(markerPath) : null;

        if (candidate is null)
        {
            if (markerExists)
            {
                int cleanedCount = CleanupManagedFiles(translationDir, markerPath, oldMarker);
                DeleteDirectoryIfEmpty(translationDir);
                return new EvbTranslationSyncResult(
                    EvbTranslationSyncStatus.Cleaned,
                    oldMarker?.SourceExeName,
                    cleanedCount
                );
            }

            return new EvbTranslationSyncResult(EvbTranslationSyncStatus.NoEvbPatch);
        }

        var candidateInfo = new FileInfo(candidate);
        string exeName = candidateInfo.Name;
        long fileSize = candidateInfo.Length;
        long lastWriteTicks = candidateInfo.LastWriteTimeUtc.Ticks;

        if (oldMarker is not null
            && oldMarker.SchemaVersion == MarkerSchemaVersion
            && string.Equals(oldMarker.SourceExeName, exeName, StringComparison.OrdinalIgnoreCase)
            && oldMarker.SourceFileSize == fileSize
            && oldMarker.SourceLastWriteUtcTicks == lastWriteTicks
            && AllManagedFilesExist(translationDir, oldMarker.ManagedFiles))
        {
            return new EvbTranslationSyncResult(
                EvbTranslationSyncStatus.CacheHit,
                exeName,
                oldMarker.ManagedFiles.Length
            );
        }

        if (markerExists)
        {
            CleanupManagedFiles(translationDir, markerPath, oldMarker);
        }

        bool existedBeforeExtract = Directory.Exists(translationDir);
        IReadOnlyList<string> extractedFiles;
        try
        {
            extractedFiles = EvbVirtualFileExtractor.ExtractOverlayAssets(candidate, translationDir);
        }
        catch
        {
            if (!existedBeforeExtract)
            {
                DeleteDirectoryIfEmpty(translationDir);
            }
            throw;
        }

        var newMarker = new EvbOverlayMarker
        {
            SchemaVersion = MarkerSchemaVersion,
            SourceExeName = exeName,
            SourceFileSize = fileSize,
            SourceLastWriteUtcTicks = lastWriteTicks,
            ManagedFiles = extractedFiles.ToArray(),
        };
        WriteMarkerAtomic(markerPath, newMarker);

        return new EvbTranslationSyncResult(
            EvbTranslationSyncStatus.Extracted,
            exeName,
            extractedFiles.Count
        );
    }

    private static bool IsIgnoredNonGameExe(string fileName)
    {
        foreach (var prefix in IgnoredExePrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasTranslationHint(string fileName)
    {
        foreach (var keyword in TranslationHintKeywords)
        {
            if (fileName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static long SafeGetLastWriteUtcTicks(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsDirectoryNonEmpty(string dirPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dirPath).Any();
        }
        catch
        {
            return true;
        }
    }

    private static EvbOverlayMarker? TryReadMarker(string markerPath)
    {
        try
        {
            var json = File.ReadAllText(markerPath);
            var marker = JsonSerializer.Deserialize<EvbOverlayMarker>(json, JsonOptions);
            if (marker is null || marker.ManagedFiles is null)
            {
                return null;
            }
            return marker;
        }
        catch
        {
            return null;
        }
    }

    private static bool AllManagedFilesExist(string translationDir, string[] managedFiles)
    {
        var rootFull = Path.GetFullPath(translationDir);
        var rootWithSep = EnsureTrailingSeparator(rootFull);

        foreach (var rel in managedFiles)
        {
            if (!EvbVirtualFileExtractor.TryNormalizeSafeRelativePath(rel, out var normalized))
            {
                return false;
            }
            var target = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
            {
                return false;
            }
        }

        return true;
    }

    private static int CleanupManagedFiles(string translationDir, string markerPath, EvbOverlayMarker? marker)
    {
        int removed = 0;
        var rootFull = Path.GetFullPath(translationDir);
        var rootWithSep = EnsureTrailingSeparator(rootFull);
        var candidateEmptyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (marker?.ManagedFiles is not null && Directory.Exists(rootFull))
        {
            foreach (var rel in marker.ManagedFiles)
            {
                if (!EvbVirtualFileExtractor.TryNormalizeSafeRelativePath(rel, out var normalized))
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.Exists(target))
                {
                    File.Delete(target);
                    removed++;
                }

                var dir = Path.GetDirectoryName(target);
                while (!string.IsNullOrEmpty(dir)
                    && !string.Equals(dir, rootFull, StringComparison.OrdinalIgnoreCase)
                    && dir.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                {
                    candidateEmptyDirs.Add(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
        }

        foreach (var dir in candidateEmptyDirs.OrderByDescending(static d => d.Length))
        {
            DeleteDirectoryIfEmpty(dir);
        }

        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }

        return removed;
    }

    private static void WriteMarkerAtomic(string markerPath, EvbOverlayMarker marker)
    {
        var dir = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmpPath = markerPath + ".tmp";
        var json = JsonSerializer.Serialize(marker, JsonOptions) + "\n";
        File.WriteAllText(tmpPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmpPath, markerPath, overwrite: true);
    }

    private static void DeleteDirectoryIfEmpty(string dirPath)
    {
        try
        {
            if (Directory.Exists(dirPath) && !Directory.EnumerateFileSystemEntries(dirPath).Any())
            {
                Directory.Delete(dirPath);
            }
        }
        catch
        {
            // 忽略目录删除失败
        }
    }

    private static string EnsureTrailingSeparator(string fullPath) =>
        fullPath.EndsWith(Path.DirectorySeparatorChar) || fullPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
}
