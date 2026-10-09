using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Оповещения о здоровье сервера: когда сообщать о перегрузках и о месте на диске.</summary>
public sealed class ServerHealthTests
{
    private const string Overload = "9.10.2026 18:08:53 [Server Warning] Server overloaded. A tick took 513ms to complete.";
    private static readonly DateTime T0 = new(2026, 10, 9, 18, 0, 0);

    [Fact]
    public void Overload_ManyInAShortTime_ReportedOnceAnHour()
    {
        var w = new OverloadWatch();
        for (var i = 0; i < OverloadWatch.Threshold - 1; i++)
            Assert.Null(w.Add(Overload, T0.AddSeconds(i * 10)));
        Assert.Null(w.Add("[Server Notification] something else", T0.AddSeconds(95)));

        Assert.Equal(OverloadWatch.Threshold, w.Add(Overload, T0.AddSeconds(100))); // десятое за 10 минут — пора
        Assert.Null(w.Add(Overload, T0.AddSeconds(110)));                       // дальше — тишина час
        // через час — снова: девять подряд ещё тихо, десятое сообщает
        for (var i = 0; i < OverloadWatch.Threshold - 1; i++) Assert.Null(w.Add(Overload, T0.AddMinutes(75).AddSeconds(i)));
        Assert.NotNull(w.Add(Overload, T0.AddMinutes(75).AddSeconds(20)));
    }

    [Fact]
    public void Overload_RareOnes_AreNotReported()
    {
        var w = new OverloadWatch();
        for (var i = 0; i < 30; i++) Assert.Null(w.Add(Overload, T0.AddMinutes(i * 3))); // раз в 3 минуты — норма
    }

    [Fact]
    public void LowDisk_OnceUntilSpaceIsFreed()
    {
        var gb = 1024L * 1024 * 1024;
        var w = new LowDiskWatch();
        Assert.False(w.Check(20 * gb));
        Assert.True(w.Check(4 * gb));
        Assert.False(w.Check(3 * gb));       // уже сообщили
        Assert.False(w.Check(5 * gb + 1));   // чуть выше порога — ещё не «освободилось»
        Assert.False(w.Check(4 * gb));
        Assert.False(w.Check(7 * gb));       // освободилось с запасом
        Assert.True(w.Check(2 * gb));        // и снова кончилось — сообщаем
    }

    [Fact]
    public void LowDisk_LooksAtThePartitionThePathIsOn()
    {
        string[] mounts = ["/", "/var", "/var/vintagestory", "/dev/shm", "/home/"];
        Assert.Equal("/var/vintagestory", LowDiskWatch.MountPointOf("/var/vintagestory/data/Saves", mounts));
        Assert.Equal("/var/vintagestory", LowDiskWatch.MountPointOf("/var/vintagestory", mounts));
        Assert.Equal("/var", LowDiskWatch.MountPointOf("/var/vintagestory2/data", mounts)); // не «/var/vintagestory»
        Assert.Equal("/", LowDiskWatch.MountPointOf("/variable", mounts));
        Assert.Equal("/home/", LowDiskWatch.MountPointOf("/home/vintagestory/server", mounts));
        Assert.Null(LowDiskWatch.MountPointOf("/opt/x", ["/var"]));

        // и на этой машине место узнаётся
        Assert.True(LowDiskWatch.FreeBytes(Path.GetTempPath()) > 0);
    }

    // настоящий /proc/meminfo с виртуалки (4 ГБ, сервер VS не запущен)
    private const string Meminfo = """
        MemTotal:        4009848 kB
        MemFree:         1399768 kB
        MemAvailable:    3238348 kB
        Buffers:           50712 kB
        Cached:          2004552 kB
        SwapCached:            0 kB
        HugePages_Total:       0
        Hugepagesize:       2048 kB
        """;

    [Fact]
    public void Memory_OnLinux_FromMeminfo_AsOnWindows()
    {
        // занято = всё, кроме доступного (MemAvailable с кэшем файлов): (4009848 − 3238348) / 4009848 = 19 %
        Assert.Equal((19, 3162L), SystemMemory.FromMeminfo(Meminfo));
        // старое ядро без MemAvailable — свободное, буферы и кэш
        var old = string.Join("\n", Meminfo.Split('\n').Where(l => !l.StartsWith("MemAvailable")));
        Assert.Equal((13, 3374L), SystemMemory.FromMeminfo(old));

        Assert.Equal((100, 0L), SystemMemory.FromMeminfo("MemTotal: 1000 kB\nMemAvailable: 0 kB"));
        Assert.True(SystemMemory.IsLow(SystemMemory.FromMeminfo("MemTotal: 8388608 kB\nMemAvailable: 524288 kB")!.Value)); // 512 МБ свободно
        Assert.Null(SystemMemory.FromMeminfo(""));
        Assert.Null(SystemMemory.FromMeminfo("MemFree: 100 kB"));

        // на этой машине — Windows или Linux — тоже узнаётся
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Assert.InRange(SystemMemory.Status()!.Value.LoadPercent, 0, 100);
    }
}
