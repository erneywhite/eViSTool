using eViSTool.Core.Hosting;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Фоновый наблюдатель логов сервера у хостера (сторож оповещений окна): каждый удачный опрос, кто бы его ни начал,
/// приходит ему ровно один раз со своими строками и входами-выходами, а его собственный опрос не отнимает строки у
/// раздела «Сервер». Вылет отдаётся один раз — и наблюдатель узнаёт о нём, даже если искал раздел.
/// </summary>
public sealed class HostedLogsWatchTests : IDisposable
{
    private const string Main = "/Logs/server-main.log";
    private readonly HostedFixture _host = new();
    private readonly FakeTime _time = new(new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc));

    public HostedLogsWatchTests() => _host.GPortalLayout(log: false);

    public void Dispose() => _host.Dispose();

    private HostedLogs Logs() => new(_host.Target) { Time = _time };

    private static readonly string[] FatalLines =
    [
        "17.10.2026 13:40:00 [Fatal] Game Version: v1.22.7 (Stable)",
        "Critical error occurred in the following mod: badmod@1.0.0",
        "System.InvalidOperationException: boom",
        "   at BadMod.Boom.Now(String where)",
    ];

    private static string[] StoppedTail => HostedLogSamples.Stopped[HostedLogSamples.Running.Length..];

    private string PutLog(string remote, string[] lines)
    {
        var marks = new HostedRunMarks();
        foreach (var line in lines) marks.Add(line);
        var local = _host.Server.Put(remote, HostedLogSamples.Bytes(lines));
        File.SetLastWriteTimeUtc(local, DateTime.SpecifyKind(marks.LastTime!.Value.AddHours(-2), DateTimeKind.Utc));
        return local;
    }

    private static void Append(string local, IEnumerable<string> lines)
    {
        using var file = new FileStream(local, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        file.Write(HostedLogSamples.Bytes(lines));
    }

    private static string Moves(HostedLogPoll poll) =>
        string.Join(" ", poll.Moves.Select(m => (m.Joined ? "+" : "-") + m.Name + m.Online));

    [Fact]
    public async Task EveryPoll_ReachesTheWatcherOnce_AndItsOwnPoll_LeavesTheLinesToTheSection()
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        var polls = new List<HostedLogPoll>();
        await logs.WatchAsync(p => { lock (polls) polls.Add(p); }, _ => { }, default);

        Assert.Equal(HostedServerState.Running, await logs.RefreshAsync(default)); // опрос сторожа
        Append(local, StoppedTail);
        var section = await logs.PollAsync(default);                               // опрос раздела
        await logs.CheckNowAsync(default);                                         // проверка перед изменением модов

        // раздел получил всё, в том числе прочитанное сторожем: оно ждало его в стороне
        Assert.Equal(HostedLogSamples.Stopped, section.NewLines);
        // сторож — каждый опрос по разу, строки — без повторов и пропусков
        Assert.Equal(3, polls.Count);
        Assert.Equal(HostedLogSamples.Running, polls[0].Lines.Select(l => l.Text));
        Assert.Equal(StoppedTail, polls[1].Lines.Select(l => l.Text));
        Assert.Empty(polls[2].Lines);
        Assert.Equal([HostedServerState.Running, HostedServerState.Stopped, HostedServerState.Stopped], polls.Select(p => p.State));
        Assert.All(polls, p => Assert.Equal("2026-10-17_13_27_08", p.Run));
        Assert.Equal(new DateTime(2026, 10, 17, 13, 27, 8), polls[0].Lines[0].Time); // время строки — время сервера
    }

    [Fact]
    public async Task JoinsAndLeaves_AreTakenFromTheLines_EvenWhenBothHappenBetweenTwoPolls()
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        var polls = new List<HostedLogPoll>();
        await logs.WatchAsync(polls.Add, _ => { }, default);

        await logs.RefreshAsync(default);
        Append(local, StoppedTail);
        await logs.RefreshAsync(default);

        // вошёл и вышел, вошёл (на нём и обрывается лог запущенного сервера)
        Assert.Equal("+Erney1 -Erney0 +Erney1", Moves(polls[0]));
        // вышел, зашёл и вышел за один опрос; остановка — не «все вышли»
        Assert.Equal("-Erney0 +Erney1 -Erney0", Moves(polls[1]));
        Assert.Empty(polls[1].Players);
    }

    [Fact]
    public async Task LeavingWhileTheServerStops_IsNotAMove_AsWithTheAgent()
    {
        var local = PutLog(Main, HostedLogSamples.Running); // игрок на сервере
        var logs = Logs();
        var polls = new List<HostedLogPoll>();
        await logs.WatchAsync(polls.Add, _ => { }, default);
        await logs.RefreshAsync(default);

        // остановили из панели, пока игрок был на сервере: его «вышел» сервер пишет уже во время остановки
        Append(local,
        [
            "17.10.2026 13:31:53 [Notification] Server termination event SIGTERM received. Shutting down server",
            "17.10.2026 13:31:54 [Event] Player Erney left.",
            "17.10.2026 13:31:54 [Event] Client 3 disconnected.",
            "17.10.2026 13:31:58 [Event] Stopped the server!",
        ]);
        await logs.RefreshAsync(default);

        Assert.Equal(HostedServerState.Stopped, polls[1].State);
        Assert.Empty(polls[1].Moves);
        Assert.Empty(polls[1].Players);
    }

    [Fact]
    public async Task Restart_IsMarked_AndTheRunNameIsTheNewOne()
    {
        PutLog(Main, HostedLogSamples.Stopped);
        var logs = Logs();
        var polls = new List<HostedLogPoll>();
        await logs.WatchAsync(polls.Add, _ => { }, default);
        await logs.RefreshAsync(default);

        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", HostedLogSamples.Bytes(HostedLogSamples.Stopped));
        PutLog(Main, [.. HostedLogSamples.Starting.Select(l => l.Replace("17.10.2026 ", "18.10.2026 "))]);
        await logs.RefreshAsync(default);

        Assert.False(polls[0].Restarted);
        Assert.True(polls[1].Restarted);
        Assert.Equal(HostedServerState.Starting, polls[1].State);
        Assert.Equal("2026-10-18_13_27_08", polls[1].Run);
        Assert.Empty(polls[1].Moves); // прежние игроки ушли вместе с запуском — не «вышли»
    }

    [Fact]
    public async Task Crash_IsGivenOnce_AndTheWatcherHearsOfIt_WhoeverSearched()
    {
        PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines]);
        var logs = Logs();
        var crashes = new List<HostedCrash>();
        await logs.WatchAsync(_ => { }, crashes.Add, default);

        var found = await logs.FindCrashAsync(default); // искал раздел

        Assert.NotNull(found);
        Assert.Same(found, Assert.Single(crashes));
        Assert.Same(found, Assert.Single(logs.FoundCrashes)); // и для того, кто откроет раздел позже
        Assert.Equal("badmod", found.Finding.Culprit?.ModId);
        Assert.Null(await logs.FindCrashAsync(default));      // второй раз тот же вылет не отдаётся
        Assert.Single(crashes);
    }

    [Fact]
    public async Task Unwatch_StopsTheEvents()
    {
        PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        var polls = new List<HostedLogPoll>();
        void Polled(HostedLogPoll p) => polls.Add(p);
        void Crashed(HostedCrash _) { }
        await logs.WatchAsync(Polled, Crashed, default);
        await logs.RefreshAsync(default);

        logs.Unwatch(Polled, Crashed);
        await logs.PollAsync(default);

        Assert.Single(polls);
    }

    [Fact]
    public async Task NetworkFailure_ReachesTheCaller_AndTheWatcherGetsNothing()
    {
        PutLog(Main, HostedLogSamples.Running);
        var logs = Logs();
        var polls = new List<HostedLogPoll>();
        await logs.WatchAsync(polls.Add, _ => { }, default);
        _host.Server.FailOn = (op, _) => op == "list";
        _host.Server.FailKind = RemoteFailure.Timeout;

        await Assert.ThrowsAsync<RemoteFilesException>(() => logs.RefreshAsync(default));

        Assert.Empty(polls);
    }
}
