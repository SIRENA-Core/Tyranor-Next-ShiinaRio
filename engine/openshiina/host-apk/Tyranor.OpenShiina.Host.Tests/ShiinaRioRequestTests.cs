using Tyranor.OpenShiina.Host.Shared;
using Xunit;

namespace Tyranor.OpenShiina.Host.Tests;

public sealed class ShiinaRioRequestTests
{
    private static readonly ShiinaRioCatalog SampleCatalog = ShiinaRioCatalog.Parse("""
        {
          "schemes": [
            { "name": "Aneiro", "originalTitle": "アネイロ", "executables": ["ANEIRO.exe"] }
          ],
          "gameMap": {
            "aneiro.exe": "Aneiro"
          }
        }
        """);

    [Fact]
    public void GoldenGameKeyMatchesCrossLanguageContract()
    {
        // 与 Kotlin 端 ShiinaRioHostContract.computeGameKey 保持精确 SHA-256 黄金向量对齐
        var key = ShiinaRioRequest.ComputeGameKey("/storage/emulated/0/Games/Aneiro/", "Aneiro");
        Assert.Equal("a64cccac8431257df51d3cc32f4062ab89f1e5be2357d9f9573a367960d2666f", key);
    }

    [Fact]
    public void AcceptsValidRequestAndResolvesScheme()
    {
        var input = new ShiinaRioRawLaunchInput(
            ProtocolVersion: 1,
            Engine: "shiinario",
            GameId: "game-1",
            GameName: "Aneiro",
            GameKey: null,
            GameDir: "/storage/emulated/0/Games/Aneiro/",
            LaunchTarget: "ANEIRO.exe",
            SettingsJson: """{"renderer":"Gpu"}""",
            Locale: "zh-CN"
        );

        var ok = ShiinaRioRequest.TryParse(
            input,
            SampleCatalog,
            path => path == "/storage/emulated/0/Games/Aneiro",
            out var req,
            out var error
        );

        Assert.True(ok, error);
        Assert.NotNull(req);
        Assert.Equal("/storage/emulated/0/Games/Aneiro", req.GameDir);
        Assert.Equal("Aneiro", req.ResolvedSchemeName);
        Assert.Equal("a64cccac8431257df51d3cc32f4062ab89f1e5be2357d9f9573a367960d2666f", req.GameKey);
    }

    [Theory]
    [InlineData(0, "shiinario", "/storage/emulated/0/Game", null)]
    [InlineData(1, "impacto", "/storage/emulated/0/Game", null)]
    [InlineData(1, "shiinario", "relative/path", null)]
    [InlineData(1, "shiinario", "/storage/../etc", null)]
    [InlineData(1, "shiinario", "/storage/missing", null)]
    [InlineData(1, "shiinario", "/storage/emulated/0/Game", "../escape.exe")]
    [InlineData(1, "shiinario", "/storage/emulated/0/Game", "sub/file.exe")]
    public void RejectsMalformedOrUnsafeRequests(
        int proto,
        string engine,
        string gameDir,
        string? target
    )
    {
        var input = new ShiinaRioRawLaunchInput(
            ProtocolVersion: proto,
            Engine: engine,
            GameId: "g1",
            GameName: "Test",
            GameKey: null,
            GameDir: gameDir,
            LaunchTarget: target,
            SettingsJson: null,
            Locale: null
        );

        var ok = ShiinaRioRequest.TryParse(
            input,
            SampleCatalog,
            path => path == "/storage/emulated/0/Game",
            out _,
            out var err
        );

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }
}
