using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Восстановление мира — всё или ничего (аудит, пункт 4): при любом сбое рабочие файлы остаются прежними.</summary>
public sealed class WorldRestoreTests : IDisposable
{
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-restore-" + Guid.NewGuid().ToString("N"))).FullName;
    private static readonly DateTime T0 = new(2026, 10, 6, 21, 0, 0);

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private sealed class Crash : Exception;

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_data, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Всё в папке данных, кроме Backups: путь → содержимое.</summary>
    private Dictionary<string, string> State() =>
        Directory.EnumerateFiles(_data, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_data, f).Replace('\\', '/'))
            .Where(rel => !rel.StartsWith("Backups/", StringComparison.Ordinal))
            .ToDictionary(rel => rel, rel => File.ReadAllText(Path.Combine(_data, rel)));

    /// <summary>Копия «A» с данными модов; потом мир живёт дальше до состояния «B».</summary>
    private (BackupStore Store, BackupFile Copy, string Save, Dictionary<string, string> B) Setup(string worldDir = "Saves")
    {
        var save = Path.Combine(_data, worldDir, "default.vcdbs");
        Write($"{worldDir}/default.vcdbs", "world A");
        Write("Saves/XLeveling/Erney.json", "skills A");
        Write("Saves/notes.txt", "notes A");
        Write("ModData/7c8aaf01/prospect.json", "prospect A");
        if (worldDir != "Saves") Write($"{worldDir}/extra.json", "extra A");
        var store = new BackupStore(_data, "Survival");
        var copy = store.CopySave(save, T0);
        Assert.True(copy.ModDataSize > 0);

        Write($"{worldDir}/default.vcdbs", "world B");
        Write("Saves/XLeveling/Erney.json", "skills B");
        Write("Saves/XLeveling/Tori.json", "skills Tori B");
        Write("Saves/notes.txt", "notes B");
        Write("ModData/7c8aaf01/prospect.json", "prospect B");
        Write("ModData/new-mod/data.json", "new B");
        if (worldDir != "Saves") Write($"{worldDir}/extra.json", "extra B");
        return (store, copy, save, State());
    }

    private void AssertNoLeftovers(BackupStore store, BackupFile copy)
    {
        Assert.False(Directory.Exists(WorldRestore.StageFor(_data)));
        Assert.DoesNotContain(State().Keys, k => k.Contains(".evistool", StringComparison.Ordinal));
        Assert.Equal([copy.Name], store.List().Select(b => b.Name)); // страховочная копия не осталась
    }

    [Fact]
    public void Success_PutsTheWholeSetInPlace()
    {
        var (store, copy, save, _) = Setup();

        var safety = store.Restore(copy, save, T0.AddMinutes(1));

        Assert.Equal(new Dictionary<string, string>
        {
            ["Saves/default.vcdbs"] = "world A",
            ["Saves/XLeveling/Erney.json"] = "skills A",
            ["Saves/notes.txt"] = "notes A",
            ["ModData/7c8aaf01/prospect.json"] = "prospect A",
        }, State());
        Assert.False(Directory.Exists(WorldRestore.StageFor(_data)));
        Assert.Equal("world B", File.ReadAllText(safety!.Path));
    }

    [Fact]
    public void WorldInASubfolder_StaysInPlace_AndItsNeighboursAreRestored()
    {
        var (store, copy, save, _) = Setup("Saves/duo");

        store.Restore(copy, save, T0.AddMinutes(1));

        var state = State();
        Assert.Equal("world A", state["Saves/duo/default.vcdbs"]);
        Assert.Equal("extra A", state["Saves/duo/extra.json"]);
        Assert.Equal("skills A", state["Saves/XLeveling/Erney.json"]);
    }

    [Fact]
    public void BrokenArchive_ChangesNothing()
    {
        var (store, copy, save, b) = Setup();
        var archive = WorldModData.ArchiveFor(copy.Path);
        var bytes = File.ReadAllBytes(archive);
        File.WriteAllBytes(archive, bytes[..(bytes.Length / 2)]); // оборванная запись

        var ex = Assert.Throws<IOException>(() => store.Restore(copy, save, T0.AddMinutes(1)));
        Assert.IsType<InvalidDataException>(ex.InnerException);

        Assert.Equal(b, State());
        AssertNoLeftovers(store, copy);
    }

    [Fact]
    public void NotEnoughSpace_ChangesNothing()
    {
        var (store, copy, save, b) = Setup();

        var ex = Assert.Throws<IOException>(() => store.Restore(copy, save, T0.AddMinutes(1), new RestoreHooks { FreeSpace = _ => 1000 }));

        Assert.Contains(Path.GetPathRoot(_data)!, ex.InnerException!.Message); // про место на этом диске, а не что-то другое
        Assert.Equal(b, State());
        AssertNoLeftovers(store, copy);
    }

    [Fact]
    public void ALockedFile_MidSwap_PutsEverythingBack()
    {
        var (store, copy, save, b) = Setup();

        // мод держит свой файл открытым: папку XLeveling не перенести — к этому моменту notes.txt уже в стороне
        IOException ex;
        using (new FileStream(Path.Combine(_data, "Saves", "XLeveling", "Erney.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            ex = Assert.Throws<IOException>(() => store.Restore(copy, save, T0.AddMinutes(1)));

        Assert.Contains(Path.Combine("Saves", "XLeveling"), ex.Message); // понятно, что именно занято

        Assert.Equal(b, State());
        AssertNoLeftovers(store, copy);
    }

    public static TheoryData<int> Steps => [0, 1, 2, 3, 4, 5, 6, 7];

    [Theory]
    [MemberData(nameof(Steps))]
    public void Interrupted_AtAnyStep_IsRolledBackOnTheNextStart(int step)
    {
        var (store, copy, save, b) = Setup();
        var hooks = new RestoreHooks { BeforeMove = i => { if (i == step) throw new Crash(); }, IsCrash = e => e is Crash };

        Assert.Throws<Crash>(() => store.Restore(copy, save, T0.AddMinutes(1), hooks)); // «процесс убит»
        Assert.True(Directory.Exists(WorldRestore.StageFor(_data)));

        Assert.True(WorldRestore.Recover(_data)); // так делает запуск сервера и открытие копий
        Assert.Equal(b, State());
        Assert.False(Directory.Exists(WorldRestore.StageFor(_data)));
        Assert.False(WorldRestore.Recover(_data));
    }

    [Fact]
    public void ARestoreRunningInAnotherProcess_IsNotTakenForAnInterruptedOne()
    {
        var (_, _, _, b) = Setup();
        var stage = Directory.CreateDirectory(WorldRestore.StageFor(_data)).FullName;
        File.WriteAllText(Path.Combine(stage, "restore.json"), """{ "Swapping": true, "Moves": [] }""");

        using (new FileStream(Path.Combine(stage, "restore.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.False(WorldRestore.Recover(_data));

        Assert.True(Directory.Exists(stage));
        Assert.Equal(b, State().Where(kv => !kv.Key.StartsWith(WorldRestore.StageName)).ToDictionary());
    }
}
