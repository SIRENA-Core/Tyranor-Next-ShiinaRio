using System.Text.Json;

namespace Tyranor.OpenShiina.Host.Shared;

public sealed record ShiinaRioSchemeEntry(
    string Name,
    string OriginalTitle,
    int? Version,
    IReadOnlyList<string> Executables
);

/// <summary>
/// 读取 tyn/games.json 并将 launchTarget 解析为具体游戏方案名（跳过通用兜底方案）。
/// </summary>
public sealed class ShiinaRioCatalog
{
    private readonly Dictionary<string, ShiinaRioSchemeEntry> _schemesByExactName;
    private readonly Dictionary<string, string> _schemesByLowerName;
    private readonly Dictionary<string, string> _gameMap;
    private readonly HashSet<string> _genericSchemes;

    private ShiinaRioCatalog(
        Dictionary<string, ShiinaRioSchemeEntry> schemesByExactName,
        Dictionary<string, string> schemesByLowerName,
        Dictionary<string, string> gameMap,
        HashSet<string> genericSchemes
    )
    {
        _schemesByExactName = schemesByExactName;
        _schemesByLowerName = schemesByLowerName;
        _gameMap = gameMap;
        _genericSchemes = genericSchemes;
    }

    public int SchemeCount => _schemesByExactName.Count;
    public int MappedFileCount => _gameMap.Count;

    public static ShiinaRioCatalog Empty { get; } = new(
        new Dictionary<string, ShiinaRioSchemeEntry>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ShiinaRio v2.34 and older",
            "ShiinaRio v2.36-2.38",
        }
    );

    public static ShiinaRioCatalog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("games.json 根节点必须为 JSON 对象");
        }

        var genericSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ShiinaRio v2.34 and older",
            "ShiinaRio v2.36-2.38",
        };
        if (root.TryGetProperty("genericSchemes", out var genericEl) && genericEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in genericEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                {
                    genericSchemes.Add(name);
                }
            }
        }

        var schemesByExact = new Dictionary<string, ShiinaRioSchemeEntry>(StringComparer.Ordinal);
        var schemesByLower = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("schemes", out var schemesEl) && schemesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schemesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || genericSchemes.Contains(name)) continue;

                var originalTitle = item.TryGetProperty("originalTitle", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString() ?? name
                    : name;
                int? version = item.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.Number && vEl.TryGetInt32(out var v)
                    ? v
                    : null;

                var exes = new List<string>();
                if (item.TryGetProperty("executables", out var eEl) && eEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var exeItem in eEl.EnumerateArray())
                    {
                        if (exeItem.ValueKind == JsonValueKind.String && exeItem.GetString() is { Length: > 0 } exe)
                        {
                            exes.Add(exe);
                        }
                    }
                }

                schemesByExact[name] = new ShiinaRioSchemeEntry(name, originalTitle, version, exes);
                schemesByLower[name] = name;
            }
        }

        var gameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("gameMap", out var mapEl) && mapEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in mapEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var schemeName = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(schemeName) || genericSchemes.Contains(schemeName)) continue;
                gameMap[prop.Name.Trim().ToLowerInvariant()] = schemeName;
            }
        }

        return new ShiinaRioCatalog(schemesByExact, schemesByLower, gameMap, genericSchemes);
    }

    public string? ResolveSchemeName(string? launchTarget)
    {
        if (string.IsNullOrWhiteSpace(launchTarget))
        {
            return null;
        }

        var trimmed = launchTarget.Trim();
        if (_genericSchemes.Contains(trimmed))
        {
            return null;
        }
        if (_schemesByExactName.ContainsKey(trimmed))
        {
            return trimmed;
        }
        if (_schemesByLowerName.TryGetValue(trimmed, out var canonicalScheme))
        {
            return canonicalScheme;
        }

        var lower = trimmed.ToLowerInvariant();
        if (_gameMap.TryGetValue(lower, out var mapped))
        {
            return mapped;
        }
        if (_gameMap.TryGetValue($"{lower}.exe", out var mappedExe))
        {
            return mappedExe;
        }

        return null;
    }
}
