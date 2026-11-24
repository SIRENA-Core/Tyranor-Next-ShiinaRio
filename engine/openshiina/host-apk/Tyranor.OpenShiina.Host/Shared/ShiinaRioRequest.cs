using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Tyranor.OpenShiina.Host.Shared;

public sealed record ShiinaRioRawLaunchInput(
    int ProtocolVersion,
    string? Engine,
    string? GameId,
    string? GameName,
    string? GameKey,
    string? GameDir,
    string? LaunchTarget,
    string? SettingsJson,
    string? Locale
);

public sealed record ShiinaRioRequest(
    string GameId,
    string GameName,
    string GameKey,
    string GameDir,
    string? LaunchTarget,
    string? ResolvedSchemeName,
    ShiinaRioSettings Settings,
    string? Locale
)
{
    private static readonly Regex GameKeyRegex = new("^[0-9a-f]{16,64}$", RegexOptions.Compiled);

    public static bool TryParse(
        ShiinaRioRawLaunchInput input,
        ShiinaRioCatalog catalog,
        Func<string, bool> directoryExists,
        out ShiinaRioRequest? request,
        out string? error
    )
    {
        request = null;
        if (input.ProtocolVersion != ShiinaRioContract.ProtocolVersion)
        {
            error = $"不支持的协议版本：{input.ProtocolVersion}";
            return false;
        }

        if (!string.Equals(input.Engine?.Trim(), ShiinaRioContract.EngineId, StringComparison.Ordinal))
        {
            error = $"引擎标识不匹配：{input.Engine ?? "<null>"}";
            return false;
        }

        if (!TryValidateDirectory(input.GameDir, directoryExists, out var canonicalDir, out error))
        {
            return false;
        }

        if (!TryValidateLaunchTarget(input.LaunchTarget, out var cleanTarget, out error))
        {
            return false;
        }

        ShiinaRioSettings settings;
        try
        {
            settings = ShiinaRioSettings.Parse(input.SettingsJson);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
        {
            error = $"引擎设置 JSON 无效：{ex.Message}";
            return false;
        }

        var resolvedScheme = catalog.ResolveSchemeName(cleanTarget);
        var identityTarget = resolvedScheme ?? cleanTarget ?? "";

        string gameKey;
        if (!string.IsNullOrWhiteSpace(input.GameKey))
        {
            var candidateKey = input.GameKey.Trim().ToLowerInvariant();
            if (!GameKeyRegex.IsMatch(candidateKey))
            {
                error = "gameKey 格式非法（必须为 16-64 位小写十六进制）";
                return false;
            }
            gameKey = candidateKey;
        }
        else
        {
            gameKey = ComputeGameKey(canonicalDir, identityTarget);
        }

        var gameName = SanitizeDisplayText(input.GameName) ?? Path.GetFileName(canonicalDir);
        if (string.IsNullOrWhiteSpace(gameName))
        {
            gameName = "ShiinaRio Game";
        }
        var gameId = SanitizeDisplayText(input.GameId) ?? gameKey;
        var locale = ShiinaRioSettings.NormalizeLanguage(input.Locale);

        request = new ShiinaRioRequest(
            GameId: gameId,
            GameName: gameName,
            GameKey: gameKey,
            GameDir: canonicalDir,
            LaunchTarget: cleanTarget,
            ResolvedSchemeName: resolvedScheme,
            Settings: settings,
            Locale: string.IsNullOrEmpty(locale) ? null : locale
        );
        error = null;
        return true;
    }

    public static string ComputeGameKey(string canonicalGameDir, string? schemeOrTarget)
    {
        var trimmedDir = canonicalGameDir.Trim();
        if (trimmedDir.Length > 1)
        {
            trimmedDir = trimmedDir.TrimEnd('/', '\\');
        }
        var trimmedTarget = (schemeOrTarget ?? "").Trim();
        var payload = $"{trimmedTarget}\n{trimmedDir}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    private static bool TryValidateDirectory(
        string? rawDir,
        Func<string, bool> directoryExists,
        out string canonicalDir,
        out string? error
    )
    {
        canonicalDir = "";
        if (string.IsNullOrWhiteSpace(rawDir))
        {
            error = "缺少 gameDir 参数";
            return false;
        }

        var trimmed = rawDir.Trim();
        if (trimmed.Contains('\0') || trimmed.Any(char.IsControl))
        {
            error = "gameDir 包含非法控制字符";
            return false;
        }

        // 同时兼容 POSIX 绝对路径（Android）与 Windows 测试路径，统一拒绝相对路径与 ".." 段
        var isAbsolute = trimmed.StartsWith('/') || Path.IsPathFullyQualified(trimmed);
        if (!isAbsolute)
        {
            error = "gameDir 必须为绝对路径";
            return false;
        }

        var segments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static seg => seg is ".." or "."))
        {
            error = "gameDir 不允许包含相对路径段 (. / ..)";
            return false;
        }

        var normalized = trimmed.Length > 1 ? trimmed.TrimEnd('/', '\\') : trimmed;
        if (!directoryExists(normalized))
        {
            error = $"游戏目录不存在或不可读：{normalized}";
            return false;
        }

        canonicalDir = normalized;
        error = null;
        return true;
    }

    private static bool TryValidateLaunchTarget(
        string? rawTarget,
        out string? cleanTarget,
        out string? error
    )
    {
        cleanTarget = null;
        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            error = null;
            return true;
        }

        var trimmed = rawTarget.Trim();
        if (trimmed.Length > 256
            || trimmed.Contains('\0')
            || trimmed.Contains('/')
            || trimmed.Contains('\\')
            || trimmed.Contains("..")
            || trimmed.Any(char.IsControl))
        {
            error = "launchTarget 包含非法字符或路径分隔符";
            return false;
        }

        cleanTarget = trimmed;
        error = null;
        return true;
    }

    private static string? SanitizeDisplayText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var filtered = new string(raw.Trim().Where(static c => !char.IsControl(c)).ToArray());
        if (filtered.Length == 0)
        {
            return null;
        }
        return filtered.Length > 200 ? filtered[..200] : filtered;
    }
}
