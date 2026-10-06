using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests;

public class ModInfoTests
{
    [Fact]
    public void ParsesLenientJson()
    {
        // комментарии, ключи без кавычек, хвостовая запятая, странный регистр
        const string json = """
        {
            // комментарий
            Type: "code",
            "ModID": "CarryOn",
            "Name": "Carry On",
            "Version": "1.14.0",
            "Authors": ["a", "b"],
            "Dependencies": { "game": "1.22.0", "carryonlib": "*", },
        }
        """;
        var info = ModInfo.Parse(json);
        Assert.Equal("carryon", info.ModId);
        Assert.Equal("Carry On", info.Name);
        Assert.Equal("1.14.0", info.Version);
        Assert.Equal(2, info.Authors.Count);
        Assert.Equal("*", info.Dependencies["CarryOnLib"]);
    }

    [Theory]
    [InlineData("""{ "game": "1.22.8" }""", "1.22.7", "1.22.8")]    // мод новее игры — пометка
    [InlineData("""{ "game": "1.22.7" }""", "1.22.7", null)]        // ровно та же версия — в порядке
    [InlineData("""{ "game": "1.22.0" }""", "1.22.7", null)]        // старше — в порядке
    [InlineData("""{ "game": "1.23.0-rc.1" }""", "1.22.7", "1.23.0-rc.1")]
    [InlineData("""{ "game": "1.22.0" }""", "1.22.0-rc.3", "1.22.0")] // предрелиз ниже релиза — как и в игре
    [InlineData("""{ "game": "", "carryonlib": "9.9.9" }""", "1.22.7", null)] // «любая» и чужие зависимости не в счёт
    [InlineData("""{ "game": "*" }""", "1.22.7", null)]
    [InlineData("""{ "game": "1.22.0", "survival": "1.22.9" }""", "1.22.7", "1.22.9")] // самое строгое из требований
    [InlineData("""{ "game": "1.22.8" }""", null, null)]                // версия игры профиля неизвестна — не сравниваем
    public void NeedsNewerGame_ComparesTheGameDependencyWithTheProfile(string deps, string? game, string? expected)
    {
        var info = ModInfo.Parse("""{ "modid": "m", "version": "1.0", "dependencies": """ + deps + " }");
        var need = info.NeedsNewerGame(Versioning.ModVersion.ParseOrNull(game));
        Assert.Equal(expected, need?.ToString());
    }

    [Fact]
    public void DerivesModIdFromName()
    {
        var info = ModInfo.Parse("""{ "name": "Better Ruins 2!", "version": "1.0" }""");
        Assert.Equal("betterruins2", info.ModId);
    }
}
