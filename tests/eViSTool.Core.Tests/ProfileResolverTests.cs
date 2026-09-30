using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Settings;

namespace eViSTool.Core.Tests;

public sealed class ProfileResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly string _data;

    public ProfileResolverTests()
    {
        _game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        _data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        // встроенная папка модов игры
        Directory.CreateDirectory(Path.Combine(_game, "Mods"));
        File.WriteAllText(Path.Combine(_game, "Mods", "VSSurvivalMod.dll"), "");
        Directory.CreateDirectory(Path.Combine(_data, "Mods"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Json(string s) => s.Replace(@"\", @"\\");

    [Fact]
    public void ClientReadsModPathsAndDisabled_FromPrefixedSettings()
    {
        var extra = Directory.CreateDirectory(Path.Combine(_root, "extra")).FullName;
        File.WriteAllText(Path.Combine(_data, "myclientsettings.json"), $$"""
        {
          "stringListSettings": {
            "disabledMods": ["carryon", "footprints@1.2.11"],
            "modPaths": ["Mods", "{{Json(Path.Combine(_data, "Mods"))}}", "{{Json(extra)}}"]
          }
        }
        """);

        var r = ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Client, GameDir = _game, DataDir = _data });

        Assert.EndsWith("myclientsettings.json", r.ConfigPath);
        // встроенная папка игры ("Mods" относительно игры) отброшена
        Assert.Equal([Path.Combine(_data, "Mods"), extra], r.ModDirs);
        Assert.Equal(Path.Combine(_data, "Mods"), r.InstallDir);
        Assert.Equal(["carryon", "footprints@1.2.11"], r.DisabledMods);
    }

    [Fact]
    public void ServerReadsServerConfig()
    {
        File.WriteAllText(Path.Combine(_data, "serverconfig.json"), $$"""
        {
          "ModPaths": ["Mods", "{{Json(Path.Combine(_data, "Mods"))}}"],
          "WorldConfig": { "DisabledMods": null }
        }
        """);

        var r = ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Server, GameDir = _game, DataDir = _data });

        Assert.EndsWith("serverconfig.json", r.ConfigPath);
        Assert.Equal([Path.Combine(_data, "Mods")], r.ModDirs);
        Assert.Empty(r.DisabledMods);
    }

    [Fact]
    public void SharedMods_OwnUnlistedModsFolderDoesNotCount()
    {
        // профиль с общими модами: игра завела в его папке данных пустую Mods, но в списке её нет — она её не читает
        var shared = Directory.CreateDirectory(Path.Combine(_root, "home", "Mods")).FullName;
        File.WriteAllText(Path.Combine(_data, "serverconfig.json"), $$"""
        { "ModPaths": ["Mods", "{{Json(shared)}}"] }
        """);

        var r = ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Server, GameDir = _game, DataDir = _data });

        Assert.Equal([shared], r.ModDirs);
        Assert.Equal(shared, r.InstallDir); // новые моды — в общую папку, а не в свою пустую
    }

    [Fact]
    public void MissingConfigStillGivesDefaultModsDir()
    {
        var r = ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Server, GameDir = _game, DataDir = _data });
        Assert.Null(r.ConfigPath);
        Assert.Equal([Path.Combine(_data, "Mods")], r.ModDirs);
        Assert.Contains(r.Warnings, w => w.Contains("serverconfig.json"));
    }

    [Theory]
    [InlineData("CarryOn", true)]
    [InlineData("CarryOn@2.0.0", true)]
    [InlineData("CarryOn@1.0.0", false)]
    [InlineData("carryon", false)] // игра сравнивает с учётом регистра
    public void DisabledMatchesLikeGame(string entry, bool expected)
    {
        var mod = new LocalMod("x.zip", new ModInfo { ModId = "carryon", OriginalModId = "CarryOn", Version = "2.0.0" }, null);
        Assert.Equal(expected, ModUpdateService.IsDisabled(mod, new HashSet<string> { entry }));
    }

    [Fact]
    public void LegacySettingsMigrateToProfile()
    {
        var s = new AppSettings { GameDir = _game };
        s.EnsureProfiles();
        Assert.Single(s.Profiles);
        Assert.Equal(_game, s.ActiveProfile!.GameDir);
        Assert.Null(s.GameDir);
    }
}
