using System.IO.Compression;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

public sealed class ModManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _mods;

    public ModManagementTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _mods = Directory.CreateDirectory(Path.Combine(_data, "Mods")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ModInfo Mod(string id, string version = "1.0.0") => new() { ModId = id.ToLowerInvariant(), OriginalModId = id, Version = version };

    private string MakeZip(string dir, string fileName, string modId, string version, string deps = "")
    {
        var path = Path.Combine(dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
        w.Write($$"""{ "modid": "{{modId}}", "name": "{{modId}}", "version": "{{version}}", "dependencies": { {{deps}} } }""");
        return path;
    }

    private ResolvedProfile Server(string json)
    {
        File.WriteAllText(Path.Combine(_data, "serverconfig.json"), json);
        return ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Server, DataDir = _data });
    }

    [Fact]
    public void ServerDisableWritesWorldConfigAndKeepsOtherFields()
    {
        var p = Server("""
        {
          "ServerName": "Erney Server",
          "TickTime": 33.333332,
          "AntiAbuseBlockBurstAbuseBanDays": 14.0,
          "LastLaunchMods": "[{\"id\":\"game\",\"version\":\"1.22.7\"}]",
          "SomeDate": "2026-09-07T07:42:31",
          "WorldConfig": { "SaveFileLocation": "C:\\Saves\\duo\\default.vcdbs", "DisabledMods": null }
        }
        """);

        Assert.True(ModConfigEditor.SetEnabled(p, Mod("CarryOn"), enabled: false));

        var text = File.ReadAllText(p.ConfigPath!);
        var root = JObject.Parse(text);
        Assert.Equal(["CarryOn"], root["WorldConfig"]!["DisabledMods"]!.Values<string>());
        Assert.Contains("33.333332", text);
        Assert.Contains("14.0", text);
        Assert.Contains("\"2026-09-07T07:42:31\"", text); // строка не превратилась в дату другого формата
        Assert.Equal("[{\"id\":\"game\",\"version\":\"1.22.7\"}]", root["LastLaunchMods"]!.ToString());
        Assert.True(File.Exists(p.ConfigPath + ".evistool.bak"));
    }

    [Fact]
    public void EnableRemovesBothForms()
    {
        File.WriteAllText(Path.Combine(_data, "clientsettings.json"),
            """{ "stringListSettings": { "disabledMods": ["CarryOn", "carryon@2.0.0", "other"] } }""");
        var p = ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Client, DataDir = _data });

        Assert.True(ModConfigEditor.SetEnabled(p, Mod("CarryOn"), enabled: true));
        var list = JObject.Parse(File.ReadAllText(p.ConfigPath!))["stringListSettings"]!["disabledMods"]!.Values<string>();
        Assert.Equal(["other"], list);
    }

    [Fact]
    public void DisableNormalizesVersionedEntry()
    {
        File.WriteAllText(Path.Combine(_data, "clientsettings.json"),
            """{ "stringListSettings": { "disabledMods": ["CarryOn@1.0.0"] } }""");
        var p = ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Client, DataDir = _data });

        Assert.True(ModConfigEditor.SetEnabled(p, Mod("CarryOn"), enabled: false));
        var list = JObject.Parse(File.ReadAllText(p.ConfigPath!))["stringListSettings"]!["disabledMods"]!.Values<string>();
        Assert.Equal(["CarryOn"], list);
        Assert.False(ModConfigEditor.SetEnabled(ProfileResolver.Resolve(p.Profile), Mod("CarryOn"), enabled: false));
    }

    [Fact]
    public void InstallReplacesOldVersionAndKeepsBackup()
    {
        var p = Server("""{ "ModPaths": [] }""");
        MakeZip(_mods, "carryon_1.0.0.zip", "carryon", "1.0.0");
        var incoming = MakeZip(Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName, "carryon_1.1.0.zip", "carryon", "1.1.0",
            "\"carryonlib\": \"1.0.0\", \"game\": \"1.22.0\"");

        var plan = ModInstaller.Plan(incoming, p, ModUpdateService.ScanLocal(p));
        Assert.True(plan.IsReplace);
        Assert.False(plan.IsDowngrade);
        Assert.Equal(["carryonlib"], plan.MissingDependencies);

        var store = new ModBackupStore(Path.Combine(_root, "backups"));
        ModInstaller.Apply(plan, store);

        Assert.Equal(["carryon_1.1.0.zip"], Directory.GetFiles(_mods).Select(Path.GetFileName));
        Assert.Single(store.List("carryon"));
    }

    [Fact]
    public void BackupStore_KeepsOneCopyPerVersion()
    {
        // мод гоняли туда-обратно: 1.2.12 → 1.2.13 → 1.2.12 → 1.2.13 — в хранилище по одной копии каждой версии
        var p = Server("""{ "ModPaths": [] }""");
        var dl = Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName;
        var store = new ModBackupStore(Path.Combine(_root, "backups"));
        MakeZip(_mods, "Footprints-v1.2.12.zip", "footprints", "1.2.12");
        foreach (var version in new[] { "1.2.13", "1.2.12", "1.2.13" })
        {
            var zip = MakeZip(dl, $"Footprints-v{version}.zip", "footprints", version);
            ModInstaller.Apply(ModInstaller.Plan(zip, p, ModUpdateService.ScanLocal(p)), store);
            File.Delete(zip);
        }

        Assert.Equal(["1.2.12", "1.2.13"], store.List("footprints").Select(f => ModScanner.ReadZip(f).Info!.Version).Order());
    }

    [Theory]
    [InlineData("1.22.7", "1.22.8")] // игра старее, чем нужно моду, — план это видит
    [InlineData("1.22.8", null)]
    [InlineData(null, null)]         // версия игры неизвестна — не спрашиваем
    public void InstallPlan_KnowsTheModNeedsANewerGame(string? game, string? expected)
    {
        var p = Server("""{ "ModPaths": [] }""") with { GameVersion = Versioning.ModVersion.ParseOrNull(game) };
        var incoming = MakeZip(Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName, "fresh_2.0.0.zip", "fresh", "2.0.0",
            "\"game\": \"1.22.8\"");

        var plan = ModInstaller.Plan(incoming, p, ModUpdateService.ScanLocal(p));
        Assert.Equal(expected, plan.NeedsGame?.ToString());
    }

    // аудит, пункт 2: конфликт имён не должен затирать посторонний мод
    [Fact]
    public void Install_FindsAFreeName_WhenTheNameAndSeveralFallbacksAreTaken()
    {
        var p = Server("""{ "ModPaths": [] }""");
        MakeZip(_mods, "common.zip", "other", "1.0.0");
        MakeZip(_mods, "common_alpha.zip", "beta", "1.0.0");
        MakeZip(_mods, "common_alpha_2.zip", "gamma", "1.0.0");
        var before = Directory.GetFiles(_mods).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
        var incoming = MakeZip(Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName, "common.zip", "alpha", "1.0.0");

        var plan = ModInstaller.Plan(incoming, p, ModUpdateService.ScanLocal(p));
        Assert.Equal("common_alpha_3.zip", Path.GetFileName(plan.TargetPath));
        ModInstaller.Apply(plan, new ModBackupStore(Path.Combine(_root, "backups")));

        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_mods, name!)));
        Assert.Equal(["alpha", "beta", "gamma", "other"],
            ModUpdateService.ScanLocal(p).Select(m => m.Info!.ModId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Install_DoesNotOverwriteAFileThatAppearedAfterPlanning()
    {
        var p = Server("""{ "ModPaths": [] }""");
        var incoming = MakeZip(Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName, "common.zip", "alpha", "1.0.0");
        var plan = ModInstaller.Plan(incoming, p, ModUpdateService.ScanLocal(p));
        var stranger = MakeZip(_mods, "common.zip", "other", "1.0.0"); // появился, пока шла загрузка
        var bytes = File.ReadAllBytes(stranger);

        Assert.Throws<IOException>(() => ModInstaller.Apply(plan, new ModBackupStore(Path.Combine(_root, "backups"))));
        Assert.Equal(bytes, File.ReadAllBytes(stranger));
        Assert.Equal(["common.zip"], Directory.GetFiles(_mods).Select(Path.GetFileName));
    }

    [Fact]
    public void Install_DoesNotOverwriteAReplacedFileThatNowHoldsAnotherMod()
    {
        var p = Server("""{ "ModPaths": [] }""");
        MakeZip(_mods, "alpha.zip", "alpha", "1.0.0");
        var incoming = MakeZip(Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName, "alpha.zip", "alpha", "1.1.0");
        var plan = ModInstaller.Plan(incoming, p, ModUpdateService.ScanLocal(p));
        File.Delete(Path.Combine(_mods, "alpha.zip"));
        var swapped = MakeZip(_mods, "alpha.zip", "other", "1.0.0"); // файл подменили после планирования
        var bytes = File.ReadAllBytes(swapped);

        Assert.Throws<IOException>(() => ModInstaller.Apply(plan, new ModBackupStore(Path.Combine(_root, "backups"))));
        Assert.Equal(bytes, File.ReadAllBytes(swapped));
    }

    [Fact]
    public void Install_PutsTheOldVersionsBack_WhenSomethingFailsMidway()
    {
        var p = Server("""{ "ModPaths": [] }""");
        MakeZip(_mods, "alpha_1.0.0.zip", "alpha", "1.0.0");
        MakeZip(_mods, "alpha_0.9.0.zip", "alpha", "0.9.0"); // две копии одного мода — обе будут убраны
        var before = Directory.GetFiles(_mods).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
        var incoming = MakeZip(Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName, "alpha_1.1.0.zip", "alpha", "1.1.0");
        var plan = ModInstaller.Plan(incoming, p, ModUpdateService.ScanLocal(p));
        Assert.Equal(2, plan.Replaces.Count);

        // вторая копия не уходит в хранилище — первая к этому моменту уже там. На Windows так бывает, когда игра держит
        // файл; на Linux открытый файл переносу не мешает, и сбой делаем иначе: место в хранилище занято папкой
        var backups = new ModBackupStore(Path.Combine(_root, "backups"));
        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(plan.Replaces[1].Path, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => ModInstaller.Apply(plan, backups));
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(backups.DirFor("alpha"), plan.Replaces[1].FileName));
            Assert.ThrowsAny<IOException>(() => ModInstaller.Apply(plan, backups));
        }

        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_mods).Select(Path.GetFileName).Order());
        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_mods, name!)));
    }

    [Fact]
    public void DetectsDowngrade()
    {
        var p = Server("""{ "ModPaths": [] }""");
        MakeZip(_mods, "a_2.0.0.zip", "a", "2.0.0");
        var incoming = MakeZip(Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName, "a_1.0.0.zip", "a", "1.0.0");
        Assert.True(ModInstaller.Plan(incoming, p, ModUpdateService.ScanLocal(p)).IsDowngrade);
    }

    [Fact]
    public void RejectsZipWithoutModInfo()
    {
        var p = Server("""{ "ModPaths": [] }""");
        var bad = Path.Combine(_root, "bad.zip");
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
        Assert.Throws<InvalidDataException>(() => ModInstaller.Plan(bad, p, []));
    }
}
