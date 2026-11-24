using System.Text.Json;
using Tyranor.OpenShiina.Host.Shared;
using Xunit;

namespace Tyranor.OpenShiina.Host.Tests;

public sealed class ShiinaRioSettingsTests
{
    [Fact]
    public void DefaultsMatchApprovedContract()
    {
        var s = ShiinaRioSettings.Parse(null);
        Assert.Equal("Gpu", s.Renderer);
        Assert.Equal("Sharp", s.Scaling);
        Assert.Equal(8, s.FsrSharpness);
        Assert.Equal("Game", s.FramePacing);
        Assert.Equal("Auto", s.FileCache);
        Assert.True(s.TranslateX86);
        Assert.True(s.PauseInBackground);
        Assert.True(s.GameController);
        Assert.False(s.ShowFrameRate);
        Assert.Equal("Shiina", s.Theme);
        Assert.Equal("", s.Language);
    }

    [Fact]
    public void ClampsFsrSharpnessAndNormalizesEnums()
    {
        var json = """
            {
              "renderer": "cpu",
              "scaling": "anime4k",
              "fsrSharpness": 99,
              "framePacing": "display",
              "fileCache": "potato",
              "translateX86": false,
              "pauseInBackground": false,
              "gameController": false,
              "showFrameRate": true,
              "theme": "graphite",
              "language": "ja-JP"
            }
            """;
        var s = ShiinaRioSettings.Parse(json);
        Assert.Equal("Cpu", s.Renderer);
        Assert.Equal("Anime4K", s.Scaling);
        Assert.Equal(20, s.FsrSharpness);
        Assert.Equal("Display", s.FramePacing);
        Assert.Equal("Potato", s.FileCache);
        Assert.False(s.TranslateX86);
        Assert.False(s.PauseInBackground);
        Assert.False(s.GameController);
        Assert.True(s.ShowFrameRate);
        Assert.Equal("Graphite", s.Theme);
        Assert.Equal("ja", s.Language);
    }

    [Fact]
    public void BuildsDeterministicOpenShiinaSettingsJson()
    {
        var settings = ShiinaRioSettings.Default with { Renderer = "Cpu", FsrSharpness = 12 };
        var json = ShiinaRioPlayerConfig.BuildSettingsJson(settings, "zh-CN");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("Cpu", root.GetProperty("Renderer").GetString());
        Assert.Equal("Sharp", root.GetProperty("Scaling").GetString());
        Assert.Equal(12, root.GetProperty("FsrSharpness").GetInt32());
        Assert.Equal("zh-CN", root.GetProperty("Language").GetString());
        Assert.False(root.GetProperty("ShowFrameRate").GetBoolean());
        Assert.False(root.GetProperty("AndroidBackup").GetBoolean());
    }

    [Fact]
    public void RejectsOversizedSettingsPayload()
    {
        var huge = new string('x', 70_000);
        Assert.Throws<ArgumentException>(() => ShiinaRioSettings.Parse(huge));
    }
}
