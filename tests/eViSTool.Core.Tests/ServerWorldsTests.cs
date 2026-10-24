using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>
/// Миры сервера: файлы Saves/*.vcdbs, путь нового мира (с разделителем системы сервера — он может быть на Linux) и
/// какой мир сейчас в конфиге (путь в конфиге бывает и относительным).
/// </summary>
public sealed class ServerWorldsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-worlds-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void List_SavesWorlds_FreshFirst()
    {
        Assert.Empty(ServerWorlds.List(_root).Worlds); // Saves ещё нет
        var saves = Directory.CreateDirectory(Path.Combine(_root, "Saves")).FullName;
        File.WriteAllText(Path.Combine(saves, "old.vcdbs"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(saves, "old.vcdbs"), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(saves, "survival.vcdbs"), "xyz");
        File.WriteAllText(Path.Combine(saves, "notes.txt"), "");

        var list = ServerWorlds.List(_root);
        Assert.Equal(["survival", "old"], list.Worlds.Select(w => w.Name));
        Assert.Equal(Path.Combine(saves, "survival.vcdbs"), list.Worlds[0].Path);
        Assert.Equal(saves, list.SavesDir);
    }

    [Theory]
    [InlineData(@"C:\vs\data\Saves", "новый", @"C:\vs\data\Saves\новый.vcdbs")]
    [InlineData("/home/vs/data/Saves", "world2", "/home/vs/data/Saves/world2.vcdbs")]
    [InlineData("/home/vs/data/Saves/", "  world2.vcdbs ", "/home/vs/data/Saves/world2.vcdbs")]
    public void NewWorldPath_UsesTheServersSeparator(string saves, string name, string expected)
    {
        var (path, error) = ServerWorlds.NewWorldPath(new ServerWorldList(saves, []), name);
        Assert.Null(error);
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("what?")]
    [InlineData("Survival")] // уже есть (регистр не важен)
    public void NewWorldPath_RejectsBadOrTakenNames(string name)
    {
        var list = new ServerWorldList("/srv/Saves", [new ServerWorld("survival", "/srv/Saves/survival.vcdbs", 1, DateTime.UtcNow)]);
        var (path, error) = ServerWorlds.NewWorldPath(list, name);
        Assert.Null(path);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(@"C:\vs\data\Saves\survival.vcdbs")]
    [InlineData(@"c:/VS/data/saves/SURVIVAL.vcdbs")]
    [InlineData(@"Saves\survival.vcdbs")] // относительно папки данных
    [InlineData("Saves/survival.vcdbs")]
    public void Find_MatchesTheConfigPath(string inConfig)
    {
        var list = new ServerWorldList(@"C:\vs\data\Saves", [new ServerWorld("survival", @"C:\vs\data\Saves\survival.vcdbs", 1, DateTime.UtcNow)]);
        Assert.Equal("survival", ServerWorlds.Find(list, inConfig)?.Name);
        Assert.Null(ServerWorlds.Find(list, @"C:\vs\data\Saves\new.vcdbs")); // файла ещё нет — будет создан
    }
}
