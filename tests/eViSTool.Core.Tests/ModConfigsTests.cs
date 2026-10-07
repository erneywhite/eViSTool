using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

public sealed class ModConfigsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-mcfg-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _cfg;
    private readonly ModConfigBackups _backups;

    public ModConfigsTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _cfg = Directory.CreateDirectory(ModConfigs.DirFor(_data)).FullName;
        _backups = new ModConfigBackups(Path.Combine(_root, "backups"), keep: 3);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static LocalMod Mod(string modId, string name) =>
        new($"{modId}.zip", new ModInfo { ModId = modId, OriginalModId = modId, Name = name, Version = "1.0.0" }, null);

    private static readonly LocalMod[] Mods =
    [
        Mod("autolootreforged", "AutoLootReforged"),
        Mod("footprints", "Footprints"),
        Mod("primitivesurvival", "Primitive Survival"),
        Mod("prospecttogether", "Prospect Together"),
        Mod("canparty", "Can Party"),
        Mod("electricityaddon", "Electricity Addon"),
        Mod("carryon", "Carry On"),
        Mod("carryonlib", "CarryOnLib"),
    ];

    // имена — как у настоящих конфигов
    [Theory]
    [InlineData("AutoLootReforgedConfig.json", "autolootreforged")]
    [InlineData("Footprints-Client.json", "footprints")]
    [InlineData("primitivesurvival5.json", "primitivesurvival")]
    [InlineData("ProspectTogetherClient-17968c58-715c-415f-9e02-7eaf4be6730a.json", "prospecttogether")]
    [InlineData("canparty-hud.json", "canparty")]
    [InlineData("ElectricityConfig.json", "electricityaddon")]
    [InlineData("CarryOnLib.json", "carryonlib")]   // а не carryon: самое длинное совпадение
    [InlineData("carryon-server.json", "carryon")]
    [InlineData("Footprints/settings.json", "footprints")] // по имени папки
    [InlineData("imgui.ini", null)]
    [InlineData("inventorycolors.json", null)]
    public void GuessesTheModByTheFileName(string relativePath, string? expected) =>
        Assert.Equal(expected, ModConfigs.MatchMod(relativePath, Mods)?.ModId);

    [Fact]
    public void List_OnlyTextFiles_ModsFirstThenUnknown()
    {
        File.WriteAllText(Path.Combine(_cfg, "imgui.ini"), "[x]");
        File.WriteAllText(Path.Combine(_cfg, "Footprints-Client.json"), "{}");
        File.WriteAllText(Path.Combine(_cfg, "AutoLootReforgedConfig.json"), "{}");
        File.WriteAllBytes(Path.Combine(_cfg, "icon.png"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(_cfg, "sub"));
        File.WriteAllText(Path.Combine(_cfg, "sub", "carryon.json"), "{}");

        var list = ModConfigs.List(_data, Mods);

        Assert.Equal(["AutoLootReforgedConfig.json", "sub/carryon.json", "Footprints-Client.json", "imgui.ini"],
            list.Select(f => f.RelativePath));
        Assert.Equal(ModConfigKind.Text, list[^1].Kind);
        Assert.Null(list[^1].ModName);
        Assert.Equal("Carry On", list[1].ModName);
    }

    [Theory]
    [InlineData("""{ "a": 1, "b": [1, 2], }""", true)]        // хвостовая запятая — игра такое читает
    [InlineData("{ // комментарий\n  \"a\": true }", true)]
    [InlineData("""{ "a": 1 "b": 2 }""", false)]
    [InlineData("""{ "a": 1 } trailing""", false)]
    [InlineData("", false)]
    public void JsonCheck_IsAsLenientAsTheGame(string text, bool ok) =>
        Assert.Equal(ok, ModConfigs.JsonError(text) is null);

    [Fact]
    public void Comments_AreDetected()
    {
        Assert.True(ModConfigs.HasComments("{ // c\n \"a\": 1 }"));
        Assert.True(ModConfigs.HasComments("{ \"a\": /* c */ 1 }"));
        Assert.False(ModConfigs.HasComments("{ \"a\": \"// не комментарий\" }"));
    }

    [Theory]
    [InlineData("{\n    \"a\": 1\n}", "{\n    \"a\": 2\n}")]           // 4 пробела
    [InlineData("{\r\n\t\"a\": 1\r\n}", "{\r\n\t\"a\": 2\r\n}")]       // табы и CRLF
    [InlineData("{ \"a\": 1 }", "{\n  \"a\": 2\n}")]                   // в одну строку — по умолчанию 2 пробела
    public void Format_KeepsTheIndentOfTheOriginal(string original, string expected)
    {
        var root = ModConfigs.ParseJson(original);
        root["a"] = 2;
        Assert.Equal(expected, ModConfigs.Format(root, original));
    }

    [Fact]
    public void Format_KeepsNumberKinds()
    {
        var root = ModConfigs.ParseJson("{ \"f\": 1.0, \"i\": 3, \"s\": \"x\", \"n\": null }");
        Assert.Equal("{\n  \"f\": 1.0,\n  \"i\": 3,\n  \"s\": \"x\",\n  \"n\": null\n}", ModConfigs.Format(root, "{}"));
    }

    private ModConfigFile File1(string name, string content)
    {
        File.WriteAllText(Path.Combine(_cfg, name), content);
        return ModConfigs.List(_data, Mods).Single(f => f.RelativePath == name);
    }

    [Fact]
    public void Save_KeepsThePreviousVersion_AndUndoGoesBackStepByStep()
    {
        var f = File1("Footprints-Client.json", """{ "v": 1 }""");

        ModConfigs.Save(f, """{ "v": 2 }""", _backups);
        ModConfigs.Save(f, """{ "v": 3 }""", _backups);
        Assert.Equal("""{ "v": 3 }""", File.ReadAllText(f.Path));
        Assert.Equal(2, _backups.Versions(f.RelativePath).Count);

        Assert.True(_backups.Undo(f.Path, f.RelativePath));
        Assert.Equal("""{ "v": 2 }""", File.ReadAllText(f.Path));
        Assert.True(_backups.Undo(f.Path, f.RelativePath));
        Assert.Equal("""{ "v": 1 }""", File.ReadAllText(f.Path));
        Assert.False(_backups.Undo(f.Path, f.RelativePath)); // больше некуда
    }

    [Fact]
    public void Save_BrokenJson_IsNotWritten()
    {
        var f = File1("Footprints-Client.json", """{ "v": 1 }""");

        Assert.Throws<InvalidDataException>(() => ModConfigs.Save(f, """{ "v": """, _backups));

        Assert.Equal("""{ "v": 1 }""", File.ReadAllText(f.Path));
        Assert.Empty(_backups.Versions(f.RelativePath));
    }

    [Fact]
    public void Save_KeepsTheBomIfTheFileHadOne()
    {
        var path = Path.Combine(_cfg, "Footprints-Client.json");
        File.WriteAllText(path, """{ "v": 1 }""", new System.Text.UTF8Encoding(true));
        var f = ModConfigs.List(_data, Mods).Single();

        ModConfigs.Save(f, """{ "v": 2 }""", _backups);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    [Fact]
    public void Reset_RemovesTheFile_AndUndoBringsItBack()
    {
        var f = File1("AutoLootReforgedConfig.json", """{ "radius": 9 }""");

        ModConfigs.Reset(f, _backups);
        Assert.False(File.Exists(f.Path));

        Assert.True(_backups.Undo(f.Path, f.RelativePath));
        Assert.Equal("""{ "radius": 9 }""", File.ReadAllText(f.Path));
    }

    [Fact]
    public void Backups_KeepOnlyTheLastFew()
    {
        var f = File1("Footprints-Client.json", "{}");
        for (var i = 0; i < 6; i++)
        {
            ModConfigs.Save(f, $$"""{ "v": {{i}} }""", _backups);
            Thread.Sleep(5); // имена версий — по времени с миллисекундами
        }
        Assert.Equal(3, _backups.Versions(f.RelativePath).Count);
    }
}
