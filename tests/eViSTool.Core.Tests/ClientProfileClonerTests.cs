using eViSTool.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

public sealed class ClientProfileClonerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly string _data;
    private readonly GameProfile _source;

    public ClientProfileClonerTests()
    {
        _game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        _data = Directory.CreateDirectory(Path.Combine(_root, "VintagestoryData")).FullName;
        Write("Mods/carryon_1.0.0.zip", "carryon");
        Write("Mods/unpacked/modinfo.json", "{}");
        Write("ModConfig/carryon.json", "{ \"a\": 1 }");
        Write("Macros/m1.json", "{}");
        Write("Saves/мой мир.vcdbs", "world");
        Write("Maps/abc.db", "map");
        Write("Playerdata/playerdata.json", "{}");
        Write("Logs/client-main.log", "log");
        Write("Cache/unpack/x.bin", "cache");
        Write("BackupSaves/old.vcdbs", "backup");
        Write("ModsByServer/srv/mod.zip", "x");
        Write("ServerProfiles/duo/serverconfig.json", "{}");
        Write("ClientProfiles/other/clientsettings.json", "{}");
        Write("Mods.rar", "archive");
        Write("waypoints-names.json", "{}");
        Write("myclientsettings.bkp", "{}");
        Write("myclientsettings.json", $$"""
        {
          "stringSettings": { "playername": "Erney", "sessionkey": "secret" },
          "floatSettings": { "guiScale": 1.125 },
          "stringListSettings": {
            "disabledMods": ["shelfish@1.5.0"],
            "modPaths": ["Mods", "{{Json(Path.Combine(_data, "Mods"))}}"]
          }
        }
        """);
        Write("serverconfig.json", $$"""
        {
          "ModPaths": ["Mods", "{{Json(Path.Combine(_data, "Mods"))}}"],
          "WorldConfig": { "SaveFileLocation": "{{Json(Path.Combine(_data, "Saves", "мой мир.vcdbs"))}}" }
        }
        """);
        _source = new GameProfile { Name = "Клиент", Kind = ProfileKind.Client, GameDir = _game, DataDir = _data };
        _source.PinnedMods["carryon"] = "1.0.0";
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Json(string s) => s.Replace(@"\", @"\\");

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_data, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<(GameProfile Profile, string Dir)> Clone(string name, ClientModsMode mods, bool settings = true, bool worlds = false, GameProfile? source = null)
    {
        source ??= _source;
        var dir = ClientProfileLayout.SuggestDir(source.DataDir!, name);
        var plan = ClientProfileCloner.Plan(source, new ClientCloneOptions { Name = name, TargetDir = dir, Mods = mods, CopySettings = settings, CopyWorlds = worlds });
        return (await ClientProfileCloner.ApplyAsync(plan), dir);
    }

    private static List<string> ModPaths(string dir, string file = "myclientsettings.json") =>
        JObject.Parse(File.ReadAllText(Path.Combine(dir, file)))["stringListSettings"]!["modPaths"]!.Select(t => t.ToString()).ToList();

    [Fact]
    public async Task FileThatAppearedAfterThePlan_IsKept_AndTheCloneIsRefused()
    {
        var dir = ClientProfileLayout.SuggestDir(_data, "appeared");
        var plan = ClientProfileCloner.Plan(_source, new ClientCloneOptions { Name = "appeared", TargetDir = dir, Mods = ClientModsMode.Copy });
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mine.txt"), "моё");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ClientProfileCloner.ApplyAsync(plan));

        Assert.Equal(["mine.txt"], Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(dir)!, ".appeared.evistool-clone-*"));
    }

    [Fact]
    public async Task UnreadableSourceFile_FailsWithoutLeavingAnything()
    {
        var dir = ClientProfileLayout.SuggestDir(_data, "locked");
        var plan = ClientProfileCloner.Plan(_source, new ClientCloneOptions { Name = "locked", TargetDir = dir, Mods = ClientModsMode.Copy });

        using (new FileStream(Path.Combine(_data, "Mods", "carryon_1.0.0.zip"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => ClientProfileCloner.ApplyAsync(plan));

        Assert.False(Directory.Exists(dir));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(dir)!, ".locked.evistool-clone-*"));
    }

    [Fact]
    public async Task SharedMods_CopiedSettings_NoWorlds()
    {
        var (profile, dir) = await Clone("Тест модов", ClientModsMode.Shared);

        Assert.Equal(Path.Combine(_data, "ClientProfiles", "Тест модов"), dir);
        Assert.Equal((ProfileKind.Client, "Тест модов", dir, _game), (profile.Kind, profile.Name, profile.DataDir, profile.GameDir));
        Assert.Equal("1.0.0", profile.PinnedMods["CarryOn"]);

        // моды общие: своей папки нет, список папок — как у исходного профиля
        Assert.False(Directory.Exists(Path.Combine(dir, "Mods")));
        Assert.Equal(["Mods", Path.Combine(_data, "Mods")], ModPaths(dir));
        Assert.Equal([Path.Combine(_data, "Mods")], ProfileResolver.Resolve(profile).ModDirs);

        // настройки игры — копия, до последней дроби
        var settings = File.ReadAllText(Path.Combine(dir, "myclientsettings.json"));
        Assert.Contains("\"sessionkey\": \"secret\"", settings);
        Assert.Contains("1.125", settings);
        Assert.Contains("shelfish@1.5.0", settings);
        Assert.False(File.Exists(Path.Combine(dir, "myclientsettings.json.evistool.bak")));
        Assert.True(File.Exists(Path.Combine(dir, "ModConfig", "carryon.json")));
        Assert.True(File.Exists(Path.Combine(dir, "Macros", "m1.json")));
        Assert.True(File.Exists(Path.Combine(dir, "waypoints-names.json")));

        // миры не просили; бэкапы, журналы, кэш, чужие профили и посторонние файлы не копируются никогда
        foreach (var skipped in new[] { "Saves", "Maps", "Playerdata", "Logs", "Cache", "BackupSaves", "ModsByServer", "ServerProfiles", "ClientProfiles" })
            Assert.False(Directory.Exists(Path.Combine(dir, skipped)), skipped);
        Assert.False(File.Exists(Path.Combine(dir, "Mods.rar")));
        Assert.False(File.Exists(Path.Combine(dir, "myclientsettings.bkp")));

        // конфиг одиночной игры: моды — те же, путь к миру — уже в новой папке
        var single = JObject.Parse(File.ReadAllText(Path.Combine(dir, "serverconfig.json")));
        Assert.Equal(["Mods", Path.Combine(_data, "Mods")], single["ModPaths"]!.Select(t => t.ToString()));
        Assert.Equal(Path.Combine(dir, "Saves", "мой мир.vcdbs"), single["WorldConfig"]!["SaveFileLocation"]!.ToString());

        // исходный профиль не тронут
        Assert.Equal(["Mods", Path.Combine(_data, "Mods")], ModPaths(_data));
        Assert.False(File.Exists(Path.Combine(_data, "myclientsettings.json.evistool.bak")));
    }

    [Fact]
    public async Task OwnCopyOfMods_AndWorlds()
    {
        var (profile, dir) = await Clone("Копия", ClientModsMode.Copy, worlds: true);

        Assert.Equal("carryon", File.ReadAllText(Path.Combine(dir, "Mods", "carryon_1.0.0.zip")));
        Assert.True(File.Exists(Path.Combine(dir, "Mods", "unpacked", "modinfo.json")));
        Assert.Equal(["Mods", Path.Combine(dir, "Mods")], ModPaths(dir));
        Assert.Equal(Path.Combine(dir, "Mods"), ProfileResolver.Resolve(profile).InstallDir);

        Assert.Equal("world", File.ReadAllText(Path.Combine(dir, "Saves", "мой мир.vcdbs")));
        Assert.True(File.Exists(Path.Combine(dir, "Maps", "abc.db")));
        Assert.True(File.Exists(Path.Combine(dir, "Playerdata", "playerdata.json")));
        Assert.False(Directory.Exists(Path.Combine(dir, "BackupSaves")));
    }

    [Fact]
    public async Task OwnCopy_GathersModsFromAllFoldersOfTheSource()
    {
        // исходный профиль сам с общими модами: его моды лежат не в его папке данных
        var (shared, _) = await Clone("Общий", ClientModsMode.Shared);
        var (profile, dir) = await Clone("Свой", ClientModsMode.Copy, source: shared);

        Assert.Equal(Path.Combine(_data, "ClientProfiles", "Свой"), dir); // «дом» — тот же, профили лежат рядом
        Assert.True(File.Exists(Path.Combine(dir, "Mods", "carryon_1.0.0.zip")));
        Assert.Equal(["Mods", Path.Combine(dir, "Mods")], ModPaths(dir));
        Assert.Equal([Path.Combine(dir, "Mods")], ProfileResolver.Resolve(profile).ModDirs);
    }

    [Fact]
    public async Task OwnCopy_ADifferentModWithTheSameName_IsKeptUnderAnotherName()
    {
        // вторая папка модов источника: тот же carryon (одинаковый) и другой файл под именем, что уже есть
        var extra = Directory.CreateDirectory(Path.Combine(_root, "more-mods")).FullName;
        File.WriteAllText(Path.Combine(extra, "carryon_1.0.0.zip"), "carryon");
        File.WriteAllText(Path.Combine(extra, "Mods.rar"), "other");
        Directory.CreateDirectory(Path.Combine(extra, "unpacked"));
        File.WriteAllText(Path.Combine(extra, "unpacked", "modinfo.json"), "{ \"other\": true }");
        var settings = JObject.Parse(File.ReadAllText(Path.Combine(_data, "myclientsettings.json")));
        ((JArray)settings["stringListSettings"]!["modPaths"]!).Add(extra);
        File.WriteAllText(Path.Combine(_data, "myclientsettings.json"), settings.ToString());

        var dir = ClientProfileLayout.SuggestDir(_data, "clash");
        var plan = ClientProfileCloner.Plan(_source, new ClientCloneOptions { Name = "clash", TargetDir = dir, Mods = ClientModsMode.Copy });
        Assert.Equal(2, plan.ModNotes.Count); // carryon — дубль; unpacked — другая папка с тем же именем
        await ClientProfileCloner.ApplyAsync(plan);

        var mods = Path.Combine(dir, "Mods");
        Assert.Equal(["Mods.rar", "carryon_1.0.0.zip"], Directory.GetFiles(mods).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(mods, "unpacked", "modinfo.json")));
        Assert.Contains("other", File.ReadAllText(Path.Combine(mods, "unpacked (2)", "modinfo.json")));
    }

    [Fact]
    public async Task EmptyMods_CleanSettings_LeavesOnlyAnEmptyModsFolder()
    {
        var (profile, dir) = await Clone("С нуля", ClientModsMode.Empty, settings: false);

        // игра сама создаст настройки по умолчанию и найдёт свою папку Mods
        Assert.Equal(["Mods"], Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(dir, "Mods")));
        Assert.Equal(Path.Combine(dir, "Mods"), ProfileResolver.Resolve(profile).InstallDir);
    }

    [Fact]
    public async Task SharedMods_CleanSettings_WritesOnlyTheModFolderList()
    {
        var (_, dir) = await Clone("Чистый", ClientModsMode.Shared, settings: false);

        // имя файла — как у этой сборки игры; внутри только список папок модов
        Assert.Equal(["myclientsettings.json"], Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName));
        var root = JObject.Parse(File.ReadAllText(Path.Combine(dir, "myclientsettings.json")));
        Assert.Equal(["stringListSettings"], root.Properties().Select(p => p.Name));
        Assert.Equal(["Mods", Path.Combine(_data, "Mods")], ModPaths(dir));
    }

    [Fact]
    public async Task SourceThatNeverRanTheGame_GivesAnEmptyProfile()
    {
        var fresh = new GameProfile { Name = "Нет", Kind = ProfileKind.Client, GameDir = _game, DataDir = Path.Combine(_root, "нет такой") };
        var (profile, dir) = await Clone("Новый", ClientModsMode.Shared, source: fresh);

        Assert.Equal(Path.Combine(_root, "нет такой", "ClientProfiles", "Новый"), dir);
        Assert.Equal(["Mods"], Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName)); // делить моды не с кем — своя папка
        Assert.Equal(ProfileKind.Client, profile.Kind);
    }

    [Fact]
    public void Plan_RefusesBadTargets_AndCountsBytes()
    {
        ClientCloneOptions To(string dir, string name = "x") => new() { Name = name, TargetDir = dir, Mods = ClientModsMode.Copy, CopyWorlds = true };

        Assert.Throws<InvalidOperationException>(() => ClientProfileCloner.Plan(_source, To(Path.Combine(_root, "new"), name: " ")));
        Assert.Throws<InvalidOperationException>(() => ClientProfileCloner.Plan(_source, To(_data)));
        Assert.Throws<InvalidOperationException>(() => ClientProfileCloner.Plan(_source, To(Path.Combine(_data, "Saves", "inside"))));
        Assert.Throws<InvalidOperationException>(() => ClientProfileCloner.Plan(_source, To(_root))); // содержит исходную
        Assert.Throws<InvalidOperationException>(() => ClientProfileCloner.Plan(_source, To(Path.Combine(_data, "ClientProfiles", "other")))); // не пустая

        var plan = ClientProfileCloner.Plan(_source, To(Path.Combine(_data, "ClientProfiles", "ok")));
        Assert.Equal(plan.Files.Sum(f => new FileInfo(f.Source).Length), plan.TotalBytes);
        Assert.Contains(plan.Files, f => f.Relative == Path.Combine("Saves", "мой мир.vcdbs"));
        Assert.DoesNotContain(plan.Files, f => f.Relative.StartsWith("Logs"));
        Assert.False(Directory.Exists(Path.Combine(_data, "ClientProfiles", "ok"))); // план ничего не создаёт
    }

    [Fact]
    public void Layout_KeepsClientProfilesNextToEachOther()
    {
        var first = ClientProfileLayout.SuggestDir(_data, "Мой набор");
        Assert.Equal(Path.Combine(_data, "ClientProfiles", "Мой набор"), first);
        Assert.True(ClientProfileLayout.IsInContainer(first));
        Assert.False(ClientProfileLayout.IsInContainer(_data));
        Assert.False(ServerProfileLayout.IsInContainer(first));
        Assert.Equal(_data, ClientProfileLayout.HomeOf(first));
        Assert.Equal(Path.Combine(_data, "ClientProfiles", "client"), ClientProfileLayout.SuggestDir(_data, " ? "));
        // занятое имя — следующее свободное
        Assert.Equal(Path.Combine(_data, "ClientProfiles", "other-2"), ClientProfileLayout.SuggestDir(_data, "other"));
    }
}
