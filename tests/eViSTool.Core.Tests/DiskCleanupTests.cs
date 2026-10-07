using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

public sealed class DiskCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-clean-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _app;
    private readonly GameProfile _profile = new() { Id = "main", Name = "Main", Kind = ProfileKind.Client };
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private const string WorldA = "e67c2590-bb4f-439b-abfb-ef8b97656dee";

    public DiskCleanupTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Put(string relative, int bytes, DateTime? changedUtc = null, string? root = null)
    {
        var path = Path.Combine(root ?? _data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, changedUtc ?? Now);
        return path;
    }

    private static LocalMod Mod(string modId) =>
        new($"{modId}.zip", new ModInfo { ModId = modId, OriginalModId = modId, Name = modId, Version = "1.0.0" }, null);

    private static readonly LocalMod[] Mods = [Mod("carryon"), Mod("footprints")];

    private IReadOnlyList<CleanupGroup> Scan() => DiskCleanup.Scan(_profile, _data, Mods, _app, Now);

    [Fact]
    public void FindsWhatTheGameAndEViSToolPileUp_WithSafeDefaults()
    {
        Put("Cache/unpack/carryon-1.0/a.png", 1000);
        Put("ModsByServer/178.172.138.142-42420/old.zip", 500, Now.AddDays(-160));
        Put("ModsByServer/192.168.31.31-42420/fresh.zip", 400, Now.AddDays(-3));
        Put("Logs/Archive/client-main-1.log", 300);
        Put($"Maps/{WorldA}.db", 2000, Now.AddDays(-160));
        Put($"Maps/{WorldA}-geology.db", 1500, Now.AddDays(-160));
        Put("ModData/distantvistas/lod.bin", 800);          // мода нет в профиле
        Put("ModData/footprints/x.json", 50);               // мод стоит — не предлагаем
        Put($"ModData/{WorldA}/x.json", 20);
        Put("ModConfig/imgui.ini", 10);                     // не угадан мод
        Put("ModConfig/Footprints-Client.json", 10);        // свой мод — не предлагаем
        Put("ModBackups/main/oldmod/oldmod_1.0.zip", 600, root: _app);
        Put("ModBackups/main/carryon/carryon_0.9.zip", 700, root: _app);
        Put("ModBackups/other/oldmod/x.zip", 99, root: _app); // чужой профиль — не трогаем
        Put("Downloads/stuck.zip", 90, root: _app);

        var groups = Scan().ToDictionary(g => g.Kind);

        Assert.True(groups[CleanupKind.UnpackedMods].Items.Single().Default);
        var servers = groups[CleanupKind.ServerMods].Items;
        Assert.Equal(["178.172.138.142:42420", "192.168.31.31:42420"], servers.Select(i => i.Title));
        Assert.True(servers[0].Default);   // не заходили 160 дней
        Assert.False(servers[1].Default);  // заходили на днях
        Assert.True(groups[CleanupKind.OldLogs].Items.Single().Default);

        // карта мира — файлы мира одним пунктом, по умолчанию не выбрана
        var map = groups[CleanupKind.WorldMaps].Items.Single();
        Assert.Equal((WorldA, 3500L, 2), (map.Title, map.Size, map.Paths.Count));
        Assert.False(map.Default);
        Assert.Equal(CleanupSafety.Careful, groups[CleanupKind.WorldMaps].Safety);

        Assert.Equal(["distantvistas"], groups[CleanupKind.RemovedModData].Items.Select(i => i.Title));
        Assert.Equal([WorldA], groups[CleanupKind.WorldModData].Items.Select(i => i.Title));
        Assert.Equal(["imgui.ini"], groups[CleanupKind.OrphanConfigs].Items.Select(i => i.Title));

        var removed = groups[CleanupKind.RemovedModVersions].Items.Single();
        Assert.Equal(("oldmod", 600L, true), (removed.Title, removed.Size, removed.Default));
        Assert.False(groups[CleanupKind.InstalledModVersions].Items.Single().Default);
        Assert.True(groups[CleanupKind.Downloads].Items.Single().Default);
    }

    [Fact]
    public void NothingThere_NoGroups()
    {
        Assert.Empty(Scan());
        Directory.CreateDirectory(Path.Combine(_data, "Cache", "unpack")); // пустая папка — нечего убирать
        Assert.Empty(Scan());
    }

    [Fact]
    public void Remove_GoesThroughTheGivenWay_AndCountsWhatWasFreed()
    {
        Put("Logs/Archive/a.log", 300);
        Put($"Maps/{WorldA}.db", 2000);
        Put($"Maps/{WorldA}-geology.db", 1500);
        var items = Scan().SelectMany(g => g.Items).ToList();
        var removed = new List<string>();

        var (freed, problems) = DiskCleanup.Remove(items, p =>
        {
            if (p.EndsWith("-geology.db")) throw new IOException("занят");
            removed.Add(p);
            if (Directory.Exists(p)) Directory.Delete(p, true); else File.Delete(p);
        });

        Assert.Equal(2300, freed);           // лог и карта, без занятого файла
        Assert.Single(problems);
        Assert.Contains("geology", problems[0]);
        Assert.Equal(2, removed.Count);
        Assert.True(File.Exists(Path.Combine(_data, "Maps", WorldA + "-geology.db")));
    }
}
