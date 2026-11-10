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

    // мод-папки в хранилище тоже вытесняются старшие: раньше чистились только файлы, и папки копились без конца
    [Fact]
    public void BackupStore_PrunesFolderModsToo()
    {
        var store = new ModBackupStore(Path.Combine(_root, "backups"), keepPerMod: 2);
        for (var i = 0; i < 4; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_mods, "devmod")).FullName;
            File.WriteAllText(Path.Combine(dir, "modinfo.json"), $$"""{ "modid": "devmod", "version": "0.{{i}}.0" }""");
            store.Keep(new LocalMod(dir, new ModInfo { ModId = "devmod", Version = $"0.{i}.0" }, null) { });
            Assert.False(Directory.Exists(dir));
        }
        Assert.Equal(2, store.List("devmod").Count);
    }

    // modinfo.json мода-папки не прочитать (его держат открытым) — этот мод с ошибкой, остальные на месте
    [WindowsFact]
    public void Scan_UnreadableFolderMod_DoesNotBreakTheScan()
    {
        // монопольное открытие файла есть только на Windows (пропуск — аудит, п. 6.12)
        var p = Server("""{ "ModPaths": [] }""");
        MakeZip(_mods, "carryon_1.0.0.zip", "carryon", "1.0.0");
        var dir = Directory.CreateDirectory(Path.Combine(_mods, "devmod")).FullName;
        var info = Path.Combine(dir, "modinfo.json");
        File.WriteAllText(info, """{ "modid": "devmod", "version": "0.1.0" }""");

        using (new FileStream(info, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var mods = ModUpdateService.ScanLocal(p);
            Assert.Contains(mods, m => m.Info?.ModId == "carryon");
            Assert.Contains(mods, m => m.Path == dir && m.Error is not null);
        }
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
        // вторая копия не уходит в хранилище — первая к этому моменту уже там. На Windows так бывает, когда игра держит
        // файл; на Linux открытый файл переносу не мешает, и сбой делаем иначе: вторая копия — в папке модов только для
        // чтения, её оттуда не унести (занятое место в хранилище больше не мешает: Keep берёт другое имя)
        var extra = Directory.CreateDirectory(Path.Combine(_root, "extra")).FullName;
        var p = Server(OperatingSystem.IsWindows() ? """{ "ModPaths": [] }""" : $$"""{ "ModPaths": ["{{extra}}"] }""");
        MakeZip(_mods, "alpha_1.0.0.zip", "alpha", "1.0.0");
        MakeZip(OperatingSystem.IsWindows() ? _mods : extra, "alpha_0.9.0.zip", "alpha", "0.9.0"); // две копии одного мода — обе будут убраны
        var before = Directory.GetFiles(_mods).Concat(Directory.GetFiles(extra)).ToDictionary(f => f, File.ReadAllBytes);
        var incoming = MakeZip(Directory.CreateDirectory(Path.Combine(_root, "dl")).FullName, "alpha_1.1.0.zip", "alpha", "1.1.0");
        var plan = ModInstaller.Plan(incoming, p, ModUpdateService.ScanLocal(p));
        Assert.Equal(2, plan.Replaces.Count);

        var backups = new ModBackupStore(Path.Combine(_root, "backups"));
        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(plan.Replaces[1].Path, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsAny<IOException>(() => ModInstaller.Apply(plan, backups));
        }
        else
        {
            Assert.StartsWith(extra, plan.Replaces[1].Path); // сбой — именно на второй копии
            File.SetUnixFileMode(extra, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { Assert.ThrowsAny<UnauthorizedAccessException>(() => ModInstaller.Apply(plan, backups)); }
            finally { File.SetUnixFileMode(extra, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }

        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_mods).Concat(Directory.GetFiles(extra)).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
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

    [Fact]
    public void ManyDisablesAtOnce_LoseNoChange()
    {
        // окно и агент выключают моды в одном serverconfig.json одновременно: без замка файла часть правок затиралась
        var p = Server("""{ "ServerName": "S", "WorldConfig": { "DisabledMods": [] } }""");
        var ids = Enumerable.Range(1, 16).Select(i => $"mod{i}").ToList();

        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 8 }, id => ModConfigEditor.SetEnabled(p, Mod(id), enabled: false));

        var disabled = JObject.Parse(File.ReadAllText(p.ConfigPath!))["WorldConfig"]!["DisabledMods"]!.Values<string>();
        Assert.Equal(ids.Order(), disabled.Order());
        Assert.Equal(["serverconfig.json", "serverconfig.json.evistool.bak"], Directory.GetFiles(_data).Select(Path.GetFileName).Order());
    }
}
