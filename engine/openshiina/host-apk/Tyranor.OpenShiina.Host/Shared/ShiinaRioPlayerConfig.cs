using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tyranor.OpenShiina.Host.Shared;

/// <summary>
/// 生成并写入供 OpenShiina.Game.PlayerSettings.Load() 读取的 settings.json。
/// </summary>
public static class ShiinaRioPlayerConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ResolveEffectiveLanguage(ShiinaRioSettings settings, string? appLocale)
    {
        if (!string.IsNullOrEmpty(settings.Language))
        {
            return settings.Language;
        }
        return ShiinaRioSettings.NormalizeLanguage(appLocale);
    }

    public static string BuildSettingsJson(ShiinaRioSettings settings, string? appLocale)
    {
        var effectiveLang = ResolveEffectiveLanguage(settings, appLocale);
        var payload = new Dictionary<string, object?>
        {
            ["Renderer"] = settings.Renderer,
            ["FramePacing"] = settings.FramePacing,
            ["Scaling"] = settings.Scaling,
            ["Language"] = effectiveLang,
            ["Theme"] = settings.Theme,
            ["LibraryByName"] = false,
            ["FileCache"] = settings.FileCache,
            ["FsrSharpness"] = settings.FsrSharpness,
            ["PauseInBackground"] = settings.PauseInBackground,
            ["GameController"] = settings.GameController,
            ["ShowFrameRate"] = settings.ShowFrameRate,
            ["PerfLog"] = false,
            ["TranslateX86"] = settings.TranslateX86,
            ["DrawTrace"] = false,
            ["AndroidBackup"] = false,
        };
        return JsonSerializer.Serialize(payload, JsonOptions) + "\n";
    }

    public static void WriteSettingsFile(string dataRoot, ShiinaRioSettings settings, string? appLocale)
    {
        Directory.CreateDirectory(dataRoot);
        var target = Path.Combine(dataRoot, "settings.json");
        var temp = Path.Combine(dataRoot, $"settings.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, BuildSettingsJson(settings, appLocale), new System.Text.UTF8Encoding(false));
        File.Move(temp, target, overwrite: true);
    }
}
