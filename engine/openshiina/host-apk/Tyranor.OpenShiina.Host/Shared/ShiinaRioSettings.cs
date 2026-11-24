using System.Text.Json;

namespace Tyranor.OpenShiina.Host.Shared;

/// <summary>
/// 经安全校验与范围钳制后的 ShiinaRio 引擎运行设置。
/// 字段与 Tyranor-Next 下发的 SETTINGS_JSON 保持严格一致。
/// </summary>
public sealed record ShiinaRioSettings(
    string Renderer = "Gpu",
    string Scaling = "Sharp",
    int FsrSharpness = 8,
    string FramePacing = "Game",
    string FileCache = "Auto",
    bool TranslateX86 = true,
    bool PauseInBackground = true,
    bool GameController = true,
    bool ShowFrameRate = false,
    string Theme = "Shiina",
    string Language = ""
)
{
    private const int MaxSettingsJsonLength = 65_536;

    private static readonly Dictionary<string, string> ValidRenderers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gpu"] = "Gpu",
        ["Cpu"] = "Cpu",
    };

    private static readonly Dictionary<string, string> ValidScalings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sharp"] = "Sharp",
        ["WholeNumbers"] = "WholeNumbers",
        ["Smooth"] = "Smooth",
        ["Nearest"] = "Nearest",
        ["Fsr"] = "Fsr",
        ["Anime4K"] = "Anime4K",
    };

    private static readonly Dictionary<string, string> ValidFramePacing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Game"] = "Game",
        ["Display"] = "Display",
    };

    private static readonly Dictionary<string, string> ValidFileCache = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Auto"] = "Auto",
        ["Potato"] = "Potato",
        ["Low"] = "Low",
        ["Medium"] = "Medium",
        ["High"] = "High",
        ["Max"] = "Max",
    };

    private static readonly Dictionary<string, string> ValidThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Shiina"] = "Shiina",
        ["Parchment"] = "Parchment",
        ["Graphite"] = "Graphite",
        ["White"] = "White",
        ["System"] = "System",
    };

    public static ShiinaRioSettings Default { get; } = new();

    public static ShiinaRioSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }
        if (json.Length > MaxSettingsJsonLength)
        {
            throw new ArgumentException($"settingsJson 长度超出限制（{json.Length} > {MaxSettingsJsonLength}）");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("settingsJson 必须为 JSON 对象");
        }

        return new ShiinaRioSettings(
            Renderer: ReadEnum(root, "renderer", ValidRenderers, Default.Renderer),
            Scaling: ReadEnum(root, "scaling", ValidScalings, Default.Scaling),
            FsrSharpness: Math.Clamp(ReadInt(root, "fsrSharpness", Default.FsrSharpness), 0, 20),
            FramePacing: ReadEnum(root, "framePacing", ValidFramePacing, Default.FramePacing),
            FileCache: ReadEnum(root, "fileCache", ValidFileCache, Default.FileCache),
            TranslateX86: ReadBool(root, "translateX86", Default.TranslateX86),
            PauseInBackground: ReadBool(root, "pauseInBackground", Default.PauseInBackground),
            GameController: ReadBool(root, "gameController", Default.GameController),
            ShowFrameRate: ReadBool(root, "showFrameRate", Default.ShowFrameRate),
            Theme: ReadEnum(root, "theme", ValidThemes, Default.Theme),
            Language: NormalizeLanguage(ReadString(root, "language", Default.Language))
        );
    }

    public static string NormalizeLanguage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }
        var trimmed = raw.Trim();
        if (trimmed.Length > 16 || trimmed.Any(c => char.IsControl(c) || c == '/' || c == '\\'))
        {
            return "";
        }
        if (trimmed.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }
        if (trimmed.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return "ja";
        }
        if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }
        return "";
    }

    private static string ReadEnum(
        JsonElement root,
        string key,
        Dictionary<string, string> allowed,
        string fallback
    )
    {
        if (!TryGetPropertyCaseInsensitive(root, key, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }
        var raw = el.GetString();
        return raw is not null && allowed.TryGetValue(raw.Trim(), out var normalized)
            ? normalized
            : fallback;
    }

    private static int ReadInt(JsonElement root, string key, int fallback)
    {
        if (!TryGetPropertyCaseInsensitive(root, key, out var el) || el.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }
        return el.TryGetInt32(out var val) ? val : fallback;
    }

    private static bool ReadBool(JsonElement root, string key, bool fallback)
    {
        if (!TryGetPropertyCaseInsensitive(root, key, out var el))
        {
            return fallback;
        }
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static string ReadString(JsonElement root, string key, string fallback)
    {
        if (!TryGetPropertyCaseInsensitive(root, key, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }
        return el.GetString() ?? fallback;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement root, string key, out JsonElement value)
    {
        if (root.TryGetProperty(key, out value))
        {
            return true;
        }
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
