using eViSTool.Core.Server;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests;

/// <summary>Статистика сервера: запись по дням, сеансы игроков, точки графиков, плитки.</summary>
public sealed class ServerStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-stats-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static StatsEntry S(DateTime t, int players, long mem = 3000, double cpu = 10) => new(t, StatsKind.Sample, players, mem, cpu);

    /// <summary>Замеры раз в минуту с from до to (включительно) с одним и тем же числом игроков.</summary>
    private static IEnumerable<StatsEntry> Minutes(DateTime from, DateTime to, int players)
    {
        for (var t = from; t <= to; t = t.AddMinutes(1)) yield return S(t, players);
    }

    [Fact]
    public void Store_WritesByDay_ReadsBack_AndPrunes()
    {
        var store = new StatsStore(_dir);
        store.Append(S(Now.AddDays(-40), 1));
        store.Append(S(Now.AddMinutes(-1), 2, 3120, 12.5));
        store.Append(new StatsEntry(Now, StatsKind.Join, Name: "toristarm"));
        File.AppendAllText(Path.Combine(_dir, "2026-10-09.log"), "мусор\n12:00:00\ts\tx\n");

        var read = store.Read(Now.AddHours(-1), Now);
        Assert.Equal([S(Now.AddMinutes(-1), 2, 3120, 12.5), new StatsEntry(Now, StatsKind.Join, Name: "toristarm")], read);

        store.Prune(Now);
        Assert.Single(Directory.GetFiles(_dir, "*.log")); // день 40 дней назад удалён
        Assert.Equal(2, store.Read(Now.AddDays(-41), Now).Count);
    }

    [Fact]
    public void Enabled_IsAMarkFile()
    {
        var store = new StatsStore(_dir);
        Assert.False(store.IsEnabled);
        store.SetEnabled(true);
        Assert.True(store.IsEnabled);
        store.Append(S(Now, 0));
        store.Clear();
        Assert.True(store.IsEnabled); // стереть данные ≠ выключить сбор
        Assert.Empty(store.Read(Now.AddDays(-1), Now));
        store.SetEnabled(false);
        Assert.False(store.IsEnabled);
    }

    [Fact]
    public void Sessions_PlaytimeAndOnline()
    {
        var start = Now.AddHours(-3);
        var entries = new List<StatsEntry> { new(start, StatsKind.Up) };
        entries.AddRange(Minutes(start, Now, 1));
        entries.Add(new(start.AddMinutes(10), StatsKind.Join, Name: "Erney"));
        entries.Add(new(start.AddMinutes(70), StatsKind.Leave, Name: "Erney"));
        entries.Add(new(start.AddMinutes(80), StatsKind.Join, Name: "Erney"));
        entries.Add(new(start.AddMinutes(150), StatsKind.Join, Name: "toristarm"));

        var r = StatsReport.Build(entries, StatsPeriod.Day, Now);

        var erney = r.Players.Single(p => p.Name == "Erney");
        Assert.Equal(TimeSpan.FromMinutes(60 + 100), erney.Played); // 60 и с 80-й минуты до сейчас (180)
        Assert.Equal(2, erney.Sessions);
        Assert.True(erney.Online);
        Assert.Equal(TimeSpan.FromMinutes(30), r.Players.Single(p => p.Name == "toristarm").Played);
        Assert.Equal("Erney", r.Players[0].Name); // больше всех наиграл — сверху
        Assert.Equal(TimeSpan.FromMinutes(190), r.TotalPlayed);
        Assert.Equal(1, r.Restarts);
    }

    [Fact]
    public void Sessions_ClosedByStopCrashAndAgentGap()
    {
        var t = Now.AddHours(-10);
        var entries = new List<StatsEntry>
        {
            new(t, StatsKind.Join, Name: "A"),
            new(t.AddMinutes(30), StatsKind.Down),
            new(t.AddMinutes(40), StatsKind.Up),
            new(t.AddMinutes(41), StatsKind.Join, Name: "A"),
            new(t.AddMinutes(51), StatsKind.Crash),
            new(t.AddMinutes(60), StatsKind.Join, Name: "A"),
            S(t.AddMinutes(61), 1),
            S(t.AddMinutes(62), 1),
            // агент пропал на часы — сеанс закрывается на последней записи
            S(t.AddHours(5), 0),
        };
        // пока сервер работал, замеры шли каждую минуту
        entries.AddRange(Minutes(t.AddMinutes(1), t.AddMinutes(29), 1));
        entries.AddRange(Minutes(t.AddMinutes(41), t.AddMinutes(50), 1));
        entries.AddRange(Minutes(t.AddMinutes(52), t.AddMinutes(59), 0));

        var r = StatsReport.Build(entries, StatsPeriod.Day, Now);

        var a = r.Players.Single();
        Assert.Equal(TimeSpan.FromMinutes(30 + 10 + 2), a.Played);
        Assert.Equal(3, a.Sessions);
        Assert.False(a.Online);
        Assert.Equal([t.AddMinutes(51)], r.Crashes);
    }

    [Fact]
    public void Session_StartedBeforePeriod_IsClipped()
    {
        var entries = Minutes(Now.AddHours(-25), Now, 1).ToList();
        entries.Insert(0, new(Now.AddHours(-25), StatsKind.Join, Name: "A"));
        var r = StatsReport.Build(entries, StatsPeriod.Day, Now);
        Assert.Equal(TimeSpan.FromHours(24), r.Players.Single().Played);
    }

    [Fact]
    public void Points_MaxPlayers_AverageResources_GapsAreNull()
    {
        var entries = new List<StatsEntry>
        {
            S(Now.AddMinutes(-58), 1, 2000, 10),
            S(Now.AddMinutes(-57), 3, 4000, 30),
        };
        var r = StatsReport.Build(entries, StatsPeriod.Day, Now);

        Assert.Equal(TimeSpan.FromMinutes(5), r.Step);
        Assert.InRange(r.Points.Count, 288, 289);
        var p = r.Points.Single(x => x.Players is not null);
        Assert.Equal(3, p.Players);
        Assert.Equal(3000, p.MemoryMb);
        Assert.Equal(20, p.Cpu);
        Assert.Null(r.Points[0].Players); // сервер не работал
        Assert.Equal(3, r.PeakPlayers);
        Assert.Equal(Now.AddMinutes(-57), r.PeakAt);
    }

    [Fact]
    public void Uptime_CountsFromStartOfCollection()
    {
        // сбор включили 2 часа назад, сервер работал первый час
        var entries = Minutes(Now.AddHours(-2), Now.AddHours(-1).AddMinutes(-1), 0).ToList();
        var r = StatsReport.Build(entries, StatsPeriod.Week, Now);
        Assert.Equal(0.5, r.Uptime!.Value, 2);
        Assert.Null(StatsReport.Build([], StatsPeriod.Week, Now).Uptime);
        Assert.False(StatsReport.Build([], StatsPeriod.Week, Now).HasData);
    }

    [Fact]
    public void Report_SurvivesJson()
    {
        var r = StatsReport.Build([.. Minutes(Now.AddHours(-1), Now, 2), new(Now.AddHours(-1), StatsKind.Join, Name: "A")], StatsPeriod.Day, Now);
        var back = JsonConvert.DeserializeObject<StatsReport>(JsonConvert.SerializeObject(r))!;
        Assert.Equal(r.Points.Count, back.Points.Count);
        Assert.Equal(r.Players, back.Players);
        Assert.Equal(r.TotalPlayed, back.TotalPlayed);
    }

    [Fact]
    public void CpuMeter_ShareOfWholeMachine()
    {
        var m = new CpuMeter();
        var t = Now;
        Assert.Null(m.Next(TimeSpan.FromSeconds(10), t));
        var load = m.Next(TimeSpan.FromSeconds(10 + 60.0 * Environment.ProcessorCount / 4), t.AddMinutes(1));
        Assert.Equal(25, load!.Value, 1);
        Assert.Null(m.Next(null, t.AddMinutes(2))); // сервер остановлен
        Assert.Null(m.Next(TimeSpan.FromSeconds(1), t.AddMinutes(3))); // новый процесс — снова с первого замера
    }
}
