using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Копия файла мира: цельная или никакой — недописанная копия не остаётся под именем копии.</summary>
public sealed class WorldDbTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-worlddb-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Snapshot_CopiesTheWorld_WithoutLeftovers()
    {
        var source = Path.Combine(_dir, "world.vcdbs");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var target = Path.Combine(_dir, "copy.vcdbs");

        WorldDb.Snapshot(source, target);

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(target));
        Assert.False(File.Exists(target + WorldDb.PartSuffix));
        Assert.Throws<IOException>(() => WorldDb.Snapshot(source, target)); // поверх готовой копии — нет
    }

    // копия оборвалась (здесь: исходник не прочитать) — нет ни копии, ни недописанного файла
    [Fact]
    public void Snapshot_ThatFails_LeavesNothingBehind()
    {
        if (!OperatingSystem.IsWindows()) return; // монопольное открытие файла есть только на Windows (пропуск — аудит, п. 6.12)
        var source = Path.Combine(_dir, "world.vcdbs");
        File.WriteAllBytes(source, new byte[1024]);
        var target = Path.Combine(_dir, "copy.vcdbs");

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => WorldDb.Snapshot(source, target));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + WorldDb.PartSuffix));
    }
}
