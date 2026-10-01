using System.IO.Compression;
using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Данные модов рядом с миром (Saves без миров, ModData) — в копии, при ротации и при восстановлении.</summary>
public sealed class WorldModDataTests : IDisposable
{
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-moddata-" + Guid.NewGuid().ToString("N"))).FullName;
    private static readonly DateTime T0 = new(2026, 10, 1, 22, 8, 0);

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private string SaveFile => Path.Combine(_data, "Saves", "default.vcdbs");

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_data, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_data, rel));

    /// <summary>Мир, его журнал и данные модов «на момент копии».</summary>
    private void World(string state)
    {
        Write("Saves/default.vcdbs", "world " + state);
        Write("Saves/XLeveling/Erney.json", "skills " + state);
        Write("Saves/notes.txt", "notes " + state);
        Write("ModData/7c8aaf01/prospect.json", "prospect " + state);
    }

    [Fact]
    public void Copy_PacksModDataButNotWorlds()
    {
        World("A");
        Write("Saves/default.vcdbs-wal", "");          // пустой журнал — копию не запрещает
        Write("Saves/other-world.vcdbs", "чужой мир");  // другие миры — не данные модов
        Write("Playerdata/playerdata.json", "роли");    // настройки сервера — не трогаем

        var store = new BackupStore(_data, "Survival");
        var made = store.CopySave(SaveFile, T0);

        Assert.True(made.ModDataSize > 0);
        using var zip = ZipFile.OpenRead(WorldModData.ArchiveFor(made.Path));
        Assert.Equal(["ModData/7c8aaf01/prospect.json", "Saves/XLeveling/Erney.json", "Saves/notes.txt"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(made.ModDataSize, store.List().Single().ModDataSize);
    }

    [Fact]
    public void NothingToPack_NoArchive()
    {
        Write("Saves/default.vcdbs", "world");
        var made = new BackupStore(_data, "Survival").CopySave(SaveFile, T0);
        Assert.Equal(0, made.ModDataSize);
        Assert.False(File.Exists(WorldModData.ArchiveFor(made.Path)));
    }

    [Fact]
    public void Prune_RemovesArchiveWithItsBackup()
    {
        World("A");
        var store = new BackupStore(_data, "Survival");
        var first = store.CopySave(SaveFile, T0);
        store.CopySave(SaveFile, T0.AddHours(1));

        store.Prune(1);

        Assert.False(File.Exists(first.Path));
        Assert.False(File.Exists(WorldModData.ArchiveFor(first.Path)));
        Assert.Single(Directory.GetFiles(store.Dir, "*" + WorldModData.Suffix));
    }

    [Fact]
    public void Restore_BringsModDataBack_AndKeepsCurrentAside()
    {
        World("A");
        var store = new BackupStore(_data, "Survival");
        var backup = store.CopySave(SaveFile, T0);

        // поиграли дальше: мир и навыки ушли вперёд, мод завёл новый файл
        World("B");
        Write("Saves/XLeveling/New.json", "new player");

        var safety = store.Restore(store.Find(backup.Name)!, SaveFile, T0.AddHours(1));

        Assert.Equal("world A", Read("Saves/default.vcdbs"));
        Assert.Equal("skills A", Read("Saves/XLeveling/Erney.json"));
        Assert.Equal("prospect A", Read("ModData/7c8aaf01/prospect.json"));
        Assert.False(File.Exists(Path.Combine(_data, "Saves/XLeveling/New.json"))); // данные «из будущего» ушли вместе с ним

        // прежнее состояние отложено целиком — восстановление можно откатить
        using var aside = ZipFile.OpenRead(WorldModData.ArchiveFor(safety!.Path));
        Assert.Contains(aside.Entries, e => e.FullName == "Saves/XLeveling/New.json");
        using var reader = new StreamReader(aside.GetEntry("Saves/XLeveling/Erney.json")!.Open());
        Assert.Equal("skills B", reader.ReadToEnd());
    }

    [Fact]
    public void OldBackupWithoutArchive_LeavesModDataAsIs()
    {
        Write("Backups/Survival-2026-09-30_10-00-00.vcdbs", "old world");
        World("B");
        var store = new BackupStore(_data, "Survival");

        store.Restore(store.Find("Survival-2026-09-30_10-00-00.vcdbs")!, SaveFile, T0);

        Assert.Equal("old world", Read("Saves/default.vcdbs"));
        Assert.Equal("skills B", Read("Saves/XLeveling/Erney.json"));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("Saves/../../evil.txt")]
    [InlineData("Mods/evil.zip")]               // только Saves и ModData
    [InlineData("Saves/default.vcdbs")]         // мир возвращается из самой копии, не из архива данных модов
    public void RejectsArchiveWithForeignPaths_WithoutTouchingAnything(string entry)
    {
        World("B");
        var archive = Path.Combine(_data, "evil" + WorldModData.Suffix);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write("evil");
        }

        Assert.Throws<InvalidDataException>(() => WorldModData.Restore(_data, archive));
        Assert.Equal("skills B", Read("Saves/XLeveling/Erney.json")); // ничего не стёрто
        Assert.Equal("world B", Read("Saves/default.vcdbs"));
    }
}
