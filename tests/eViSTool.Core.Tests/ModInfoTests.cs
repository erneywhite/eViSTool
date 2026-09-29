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

    [Fact]
    public void DerivesModIdFromName()
    {
        var info = ModInfo.Parse("""{ "name": "Better Ruins 2!", "version": "1.0" }""");
        Assert.Equal("betterruins2", info.ModId);
    }
}
