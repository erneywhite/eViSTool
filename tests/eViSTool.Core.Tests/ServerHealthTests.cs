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
}
