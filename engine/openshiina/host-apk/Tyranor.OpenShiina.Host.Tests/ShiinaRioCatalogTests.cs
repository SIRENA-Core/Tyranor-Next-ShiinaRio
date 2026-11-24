using Tyranor.OpenShiina.Host.Shared;
using Xunit;

namespace Tyranor.OpenShiina.Host.Tests;

public sealed class ShiinaRioCatalogTests
{
    private static string FindRepoGamesJson()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "games.json");
        if (File.Exists(bundled))
        {
            return bundled;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "engine", "openshiina", "games.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("无法定位 engine/openshiina/games.json");
    }

    [Fact]
    public void ParsesRealCatalogAndSkipsGenericSchemes()
    {
        var json = File.ReadAllText(FindRepoGamesJson());
        var catalog = ShiinaRioCatalog.Parse(json);

        Assert.True(catalog.SchemeCount >= 160);
        Assert.True(catalog.MappedFileCount >= 170);

        Assert.Equal("Aneiro", catalog.ResolveSchemeName("ANEIRO.exe"));
        Assert.Equal("Aneiro", catalog.ResolveSchemeName("aneiro"));
        Assert.Equal("Aneiro", catalog.ResolveSchemeName("Aneiro"));
        Assert.Equal("Ayakashibito", catalog.ResolveSchemeName("ayakasi.EXE"));

        // 禁止直接命中通用兜底方案名
        Assert.Null(catalog.ResolveSchemeName("ShiinaRio v2.34 and older"));
        Assert.Null(catalog.ResolveSchemeName("ShiinaRio v2.36-2.38"));
    }

    [Fact]
    public void IgnoresGenericSchemesEvenIfInjectedIntoCatalog()
    {
        var json = """
            {
              "genericSchemes": ["ShiinaRio v2.34 and older"],
              "schemes": [
                { "name": "ShiinaRio v2.34 and older", "originalTitle": "Generic", "executables": ["game.exe"] },
                { "name": "CustomTitle", "originalTitle": "Custom", "executables": ["custom.exe"] }
              ],
              "gameMap": {
                "game.exe": "ShiinaRio v2.34 and older",
                "custom.exe": "CustomTitle"
              }
            }
            """;

        var catalog = ShiinaRioCatalog.Parse(json);
        Assert.Equal(1, catalog.SchemeCount);
        Assert.Equal(1, catalog.MappedFileCount);
        Assert.Null(catalog.ResolveSchemeName("game.exe"));
        Assert.Equal("CustomTitle", catalog.ResolveSchemeName("custom.exe"));
    }
}
