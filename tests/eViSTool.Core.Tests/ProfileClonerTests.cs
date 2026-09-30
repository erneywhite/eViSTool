using eViSTool.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

public sealed class ProfileClonerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly GameProfile _source;

    public ProfileClonerTests()
    {
        // как на виртуалке: одна папка данных, два мира, пути в конфигах записаны под другим пользователем
        _data = Directory.CreateDirectory(Path.Combine(_root, "VintagestoryData")).FullName;
        Write("Mods/carryon.zip", "mod");
        Write("ModConfig/carryon.json", "{}");
        Write("Playerdata/playerdata.json", "[]");
        Write("Logs/server-main.log", "log");
        Write("Cache/x.bin", "cache");
        Write("Backups/default-2026-09-07_07-42-31.vcdbs", "backup");
        Write("Saves/duo/default.vcdbs", "duo-world");
        Write("Saves/duo/default.vcdbs-wal", "wal");
        Write("Saves/bigcoop/default.vcdbs", "bigcoop-world!");
        Write("Saves/XLeveling/world.json", "{}");
        Write("servermagicnumbers.json", "{}");
        Directory.CreateDirectory(Path.Combine(_data, "Macros")); // пустая папка
        WriteConfig("serverconfig.json", "duo", "Erney&Tori", "12345");
        WriteConfig("serverconfig-ccop.json", "bigcoop", "Big coop", "777");

        _source = new GameProfile { Name = "Сервер", Kind = ProfileKind.Server, GameDir = Path.Combine(_root, "game"), DataDir = _data };
        _source.PinnedMods["carryon"] = "1.0.0";
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_data, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void WriteConfig(string file, string world, string worldName, string seed)
    {
        var foreign = @"C:\Users\Administrator\AppData\Roaming\VintagestoryData";
        File.WriteAllText(Path.Combine(_data, file), new JObject
        {
            ["ServerName"] = "Erney Server",
            ["Port"] = 42420,
            ["ModPaths"] = new JArray("Mods", foreign + @"\Mods"),
            ["WorldConfig"] = new JObject
            {
                ["Seed"] = seed,
                ["SaveFileLocation"] = foreign + $@"\Saves\{world}\default.vcdbs",
                ["WorldName"] = worldName,
                ["PlayStyle"] = "surviveandbuild",
            },
        }.ToString());
    }

    private string Target(string name) => Path.Combine(_root, name);
    private static JObject Config(string dir) => JObject.Parse(File.ReadAllText(Path.Combine(dir, "serverconfig.json")));

    [Fact]
    public void FindsConfigsAndTheirWorlds_DespiteForeignUserInPaths()
    {
        var configs = ProfileCloner.FindConfigs(_data);

        Assert.Equal(["serverconfig.json", "serverconfig-ccop.json"], configs.Select(c => c.FileName));
        Assert.Equal("Erney&Tori", configs[0].WorldName);
        Assert.Equal(Path.Combine(_data, "Saves", "duo", "default.vcdbs"), configs[0].SaveFile);
        Assert.True(configs[0].SaveExists);
        Assert.Equal(Path.Combine(_data, "Saves", "bigcoop", "default.vcdbs"), configs[1].SaveFile);
    }

    [Fact]
    public async Task SameWorld_CopiesOnlyThatWorld_AndRebasesPaths()
    {
        var to = Target("duo");
        var plan = ProfileCloner.Plan(_source, new CloneOptions { Name = "Дуо", TargetDir = to });
        var profile = await ProfileCloner.ApplyAsync(plan);

        Assert.Equal("Дуо", profile.Name);
        Assert.Equal(to, profile.DataDir);
        Assert.Equal(_source.GameDir, profile.GameDir);
        Assert.Equal("1.0.0", profile.PinnedMods["carryon"]);

        Assert.True(File.Exists(Path.Combine(to, "Mods", "carryon.zip")));
        Assert.True(File.Exists(Path.Combine(to, "ModConfig", "carryon.json")));
        Assert.True(File.Exists(Path.Combine(to, "Playerdata", "playerdata.json")));
        Assert.True(File.Exists(Path.Combine(to, "servermagicnumbers.json")));
        Assert.Equal("duo-world", File.ReadAllText(Path.Combine(to, "Saves", "duo", "default.vcdbs")));
        Assert.True(File.Exists(Path.Combine(to, "Saves", "duo", "default.vcdbs-wal")));
        Assert.True(File.Exists(Path.Combine(to, "Saves", "XLeveling", "world.json"))); // данные модов рядом с мирами

        // чужой мир, журналы, кэш, бэкапы и второй конфиг — не копируются
        Assert.False(Directory.Exists(Path.Combine(to, "Saves", "bigcoop")));
        Assert.False(Directory.Exists(Path.Combine(to, "Logs")));
        Assert.False(Directory.Exists(Path.Combine(to, "Cache")));
        Assert.False(Directory.Exists(Path.Combine(to, "Backups")));
        Assert.False(File.Exists(Path.Combine(to, "serverconfig-ccop.json")));

        var config = Config(to);
        Assert.Equal(Path.Combine(to, "Saves", "duo", "default.vcdbs"), config["WorldConfig"]!["SaveFileLocation"]!.ToString());
        Assert.Equal("12345", config["WorldConfig"]!["Seed"]!.ToString());
        Assert.Equal("Erney&Tori", config["WorldConfig"]!["WorldName"]!.ToString());
        Assert.Equal(["Mods", Path.Combine(to, "Mods")], config["ModPaths"]!.Select(t => t.ToString()));
        Assert.Equal(42420, (int)config["Port"]!); // остальное — как было

        // исходная папка не тронута
        Assert.True(File.Exists(Path.Combine(_data, "Saves", "bigcoop", "default.vcdbs")));
        Assert.True(File.Exists(Path.Combine(_data, "serverconfig-ccop.json")));
    }

    [Fact]
    public async Task OtherConfig_BecomesMainConfigOfTheClone()
    {
        var to = Target("bigcoop");
        var plan = ProfileCloner.Plan(_source, new CloneOptions { Name = "Big", TargetDir = to, ConfigFile = "serverconfig-ccop.json" });
        await ProfileCloner.ApplyAsync(plan);

        Assert.Equal("bigcoop-world!", File.ReadAllText(Path.Combine(to, "Saves", "bigcoop", "default.vcdbs")));
        Assert.False(Directory.Exists(Path.Combine(to, "Saves", "duo")));
        var config = Config(to);
        Assert.Equal("Big coop", config["WorldConfig"]!["WorldName"]!.ToString());
        Assert.Equal(Path.Combine(to, "Saves", "bigcoop", "default.vcdbs"), config["WorldConfig"]!["SaveFileLocation"]!.ToString());
    }

    [Fact]
    public async Task NewWorld_CopiesEverythingButSaves_AndResetsWorld()
    {
        var to = Target("fresh");
        var plan = ProfileCloner.Plan(_source, new CloneOptions { Name = "Новый", TargetDir = to, NewWorld = true, WorldName = "Свежий мир", IncludeBackups = true });
        await ProfileCloner.ApplyAsync(plan);

        Assert.True(File.Exists(Path.Combine(to, "Mods", "carryon.zip")));
        Assert.True(File.Exists(Path.Combine(to, "ModConfig", "carryon.json")));
        Assert.True(File.Exists(Path.Combine(to, "Playerdata", "playerdata.json")));
        Assert.False(Directory.Exists(Path.Combine(to, "Saves")));
        Assert.True(Directory.Exists(Path.Combine(to, "Macros"))); // пустые папки тоже переезжают
        Assert.False(Directory.Exists(Path.Combine(to, "Backups"))); // бэкапы старого мира новому не нужны

        var world = Config(to)["WorldConfig"]!;
        Assert.Equal(Path.Combine(to, "Saves", "default.vcdbs"), world["SaveFileLocation"]!.ToString());
        Assert.Equal("", world["Seed"]!.ToString());
        Assert.Equal("Свежий мир", world["WorldName"]!.ToString());
        Assert.Equal("surviveandbuild", world["PlayStyle"]!.ToString());
    }

    [Fact]
    public async Task Backups_AreCopiedOnlyWhenAsked()
    {
        var to = Target("with-backups");
        var plan = ProfileCloner.Plan(_source, new CloneOptions { Name = "B", TargetDir = to, IncludeBackups = true });
        await ProfileCloner.ApplyAsync(plan);
        Assert.True(File.Exists(Path.Combine(to, "Backups", "default-2026-09-07_07-42-31.vcdbs")));
    }

    [Fact]
    public void Plan_ReportsSizes_AndRefusesBadTargets()
    {
        var plan = ProfileCloner.Plan(_source, new CloneOptions { Name = "X", TargetDir = Target("x") });
        Assert.Equal(plan.Files.Sum(f => new FileInfo(f.Source).Length), plan.TotalBytes);
        Assert.True(plan.TotalBytes > 0);

        // в саму себя, внутрь себя, в непустую папку, без имени
        Assert.Throws<InvalidOperationException>(() => ProfileCloner.Plan(_source, new CloneOptions { Name = "X", TargetDir = _data }));
        Assert.Throws<InvalidOperationException>(() => ProfileCloner.Plan(_source, new CloneOptions { Name = "X", TargetDir = Path.Combine(_data, "inner") }));
        Assert.Throws<InvalidOperationException>(() => ProfileCloner.Plan(_source, new CloneOptions { Name = "X", TargetDir = _root }));
        Assert.Throws<InvalidOperationException>(() => ProfileCloner.Plan(_source, new CloneOptions { Name = " ", TargetDir = Target("y") }));
    }

    [Fact]
    public async Task Cancelled_LeavesNoHalfCopiedFolder()
    {
        var to = Target("cancelled");
        var plan = ProfileCloner.Plan(_source, new CloneOptions { Name = "C", TargetDir = to });
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cts.Cancel()); // отмена после первого же файла

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProfileCloner.ApplyAsync(plan, progress, cts.Token));
        Assert.False(Directory.Exists(to));
    }

    [Fact]
    public void RebasePath_KeepsRelativeAndForeignPaths()
    {
        var to = Target("t");
        Assert.Equal("Mods", ProfileCloner.RebasePath("Mods", _data, to));
        Assert.Equal(Path.Combine(to, "Mods"), ProfileCloner.RebasePath(Path.Combine(_data, "Mods"), _data, to));
        Assert.Equal(_root, ProfileCloner.RebasePath(_root, _data, to)); // существующая посторонняя папка
        Assert.Equal(@"D:\Shared\Mods", ProfileCloner.RebasePath(@"D:\Shared\Mods", _data, to));
    }

    [Fact]
    public void SuggestTargetDir_IsNextToSource_AndFree()
    {
        var first = ProfileCloner.SuggestTargetDir(_data, "Мой мир");
        Assert.Equal(_data + "-Мой_мир", first);
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "x"), "");
        Assert.Equal(_data + "-Мой_мир-2", ProfileCloner.SuggestTargetDir(_data, "Мой мир"));
    }

    private sealed class SyncProgress(Action<CloneProgress> action) : IProgress<CloneProgress>
    {
        public void Report(CloneProgress value) => action(value);
    }
}
