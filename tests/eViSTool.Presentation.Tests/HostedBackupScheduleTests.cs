using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Notifications;
using eViSTool.Core.Tests;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Копии мира у хостера по расписанию — сторожем оповещений (<see cref="HostedWatch"/>) с подменёнными часами,
/// поддельным FTP «как G-Portal» и настоящими логами: пора и не пора (по копиям в папке, в том числе после
/// перезапуска окна), копии игры нет — молча через час, а целый срок — одно оповещение; неудача — «Копия мира не
/// получилась» ровно один раз до удачной копии; кнопка и расписание не качают две копии сразу.
/// </summary>
public sealed class HostedBackupScheduleTests : IDisposable
{
    private const string Main = "/Logs/server-main.log";
    private const string World = "/Saves/MyVintageStoryWorld.vcdbs";

    private readonly HostedFixture _host = new();
    private readonly string _agents = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-hbsched-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _copies;
    private readonly Clock _clock = new(new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc));
    private readonly List<NotifyMessage> _sent = [];
    private readonly List<HostedWatch> _watches = [];
    private readonly List<HostedWorldJob> _started = [];

    public HostedBackupScheduleTests()
    {
        _host.GPortalLayout(log: false);
        _copies = Path.Combine(_host.Dir, "copies");
        _host.Settings.BackupDir = _copies;
        _host.Settings.BackupSchedule = HostedBackupSchedule.Daily;
        // оповещение «Копия мира не получилась» включено в Telegram
        new ServerNotifySettings
        {
            ServerName = _host.Profile.Name,
            Channels = [new NotifyChannel { Id = "tg", Name = "Telegram", Kind = NotifyKind.Telegram }],
            Routes = new() { [NotifyEvent.BackupFailed] = ["tg"] },
        }.Save(_host.Profile.Id, _agents);
    }

    public void Dispose()
    {
        foreach (var watch in _watches) watch.Stop();
        _host.Dispose();
        try { Directory.Delete(_agents, recursive: true); } catch (IOException) { }
    }

    // ---------- стенд

    private sealed class Clock(DateTime utc) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }

    private string ServerLocal(string remote) => Path.Combine([_host.Server.Root, .. remote.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    private void PutWorld(string remote, params string[] rows)
    {
        var local = ServerLocal(remote);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        foreach (var f in new[] { local, local + "-wal", local + "-shm" }) File.Delete(f);
        SqliteWorld.WriteClean(local, rows);
    }

    private void PutLog(string[] lines)
    {
        var marks = new HostedRunMarks();
        foreach (var line in lines) marks.Add(line);
        var local = _host.Server.Put(Main, HostedLogSamples.Bytes(lines));
        File.SetLastWriteTimeUtc(local, DateTime.SpecifyKind(marks.LastTime!.Value.AddHours(-2), DateTimeKind.Utc));
    }

    private (HostedWatch Watch, HostedWorldJobs Jobs) Watch(HostedWorldJobs? jobs = null)
    {
        jobs ??= new HostedWorldJobs(_clock);
        jobs.Started += _started.Add;
        var watch = new HostedWatch([new ProfileViewModel(_host.Profile, _ => { })], new HostedModUpdates((_, _) => Task.FromResult<IReadOnlyList<ModCheckResult>>([])),
            _clock, _agents, jobs)
        {
            Send = (_, message) =>
            {
                lock (_sent) _sent.Add(message);
                return Task.CompletedTask;
            },
        };
        _watches.Add(watch);
        Sta.Wait(watch.TickAsync()); // окно открылось: с этого прохода считается пауза перед первой копией
        return (watch, jobs);
    }

    /// <summary>Проход сторожа и всё, что он начал: копия до конца, её итог разобран, оповещения ушли.</summary>
    private void Tick(HostedWatch watch, HostedWorldJobs jobs)
    {
        Sta.Wait(watch.TickAsync());
        Sta.WaitFor(() => jobs.Running(_host.Profile.Id) is null);
        Sta.Wait(watch.BackupFollowedAsync(_host.Profile.Id));
        Sta.Wait(watch.SentAsync(_host.Profile.Id));
    }

    private string[] Copies() =>
        Directory.Exists(_copies) ? [.. Directory.GetFiles(_copies, "*.vcdbs").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)] : [];

    private string[] Titles()
    {
        lock (_sent) return [.. _sent.Select(m => m.Title)];
    }

    // ---------- пора и не пора

    [Fact]
    public void Daily_NotRightAfterTheWindowOpens_ThenOnce_ThenAgainOnlyADayLater() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "one");
        var (watch, jobs) = Watch();

        Tick(watch, jobs);
        Assert.Empty(_started); // окно только открылось

        _clock.UtcNow += HostedWatch.BackupFirstDelay;
        Tick(watch, jobs);
        var first = Assert.Single(_started);
        Assert.True(first.Scheduled);
        Assert.Single(Copies());

        _clock.UtcNow += TimeSpan.FromHours(23);
        Tick(watch, jobs);
        Assert.Single(_started); // суток ещё нет

        _clock.UtcNow += TimeSpan.FromHours(1);
        Tick(watch, jobs);
        Assert.Equal(2, _started.Count);
        Assert.Equal(2, Copies().Length);
        Assert.Empty(Titles()); // удачная копия — без оповещения
    });

    [Fact]
    public void TheLastCopyInTheFolder_CountsAfterTheWindowIsRestarted() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "one");
        // копия, сделанная окном вчера вечером (или кнопкой)
        var made = new HostedWorldBackups(_host.Target) { Time = new Clock(_clock.UtcNow.AddHours(-20)) };
        Task.Run(() => made.CreateAsync(serverStopped: true, null, default)).GetAwaiter().GetResult();
        var (watch, jobs) = Watch();

        _clock.UtcNow += HostedWatch.BackupFirstDelay;
        Tick(watch, jobs);
        Assert.Empty(_started);

        _clock.UtcNow += TimeSpan.FromHours(4);
        Tick(watch, jobs);
        Assert.Single(_started);

        // раз в неделю — следующая не раньше чем через неделю после этой
        _host.Settings.BackupSchedule = HostedBackupSchedule.Weekly;
        _clock.UtcNow += TimeSpan.FromDays(6);
        Tick(watch, jobs);
        Assert.Single(_started);
        _clock.UtcNow += TimeSpan.FromDays(1);
        Tick(watch, jobs);
        Assert.Equal(2, _started.Count);
    });

    [Fact]
    public void ScheduleOff_DownloadsNothing() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "one");
        _host.Settings.BackupSchedule = HostedBackupSchedule.Off;
        var (watch, jobs) = Watch();

        _clock.UtcNow += TimeSpan.FromDays(3);
        Tick(watch, jobs);

        Assert.Empty(_started);
        Assert.Empty(Copies());
    });

    // ---------- скачать нечего и неудача

    [Fact]
    public void RunningServerWithoutGameCopies_RetriesInAnHour_Silently_AndSaysSoOnceAfterAWholeDay() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Running);
        PutWorld(World, "live");
        var (watch, jobs) = Watch();
        _clock.UtcNow += HostedWatch.BackupFirstDelay;

        Tick(watch, jobs);
        Assert.Single(_started);
        Assert.True(jobs.Last(_host.Profile.Id)!.NoSource);
        Assert.Empty(Titles()); // это не сбой: сервер работает, копии игры нет

        _clock.UtcNow += TimeSpan.FromMinutes(30);
        Tick(watch, jobs);
        Assert.Single(_started); // не долбим: через час, не раньше

        _clock.UtcNow += TimeSpan.FromMinutes(31);
        Tick(watch, jobs);
        Assert.Equal(2, _started.Count);
        Assert.Empty(Titles());

        // сутки так и не вышло скачать — одно оповещение
        _clock.UtcNow += TimeSpan.FromDays(1);
        Tick(watch, jobs);
        Assert.Equal([Loc.T("notify.ev.backupFailed")], Titles());
        _clock.UtcNow += HostedWatch.BackupRetry;
        Tick(watch, jobs);
        Assert.Equal(4, _started.Count);
        Assert.Single(Titles()); // не повторяется

        // /genbackup сделали — следующая попытка берёт копию игры
        PutWorld("/Backups/genbackup.vcdbs", "from genbackup");
        _clock.UtcNow += HostedWatch.BackupRetry;
        Tick(watch, jobs);
        Assert.True(jobs.Last(_host.Profile.Id)!.Ok);
        Assert.Single(Copies());
    });

    [Fact]
    public void Failure_SendsBackupFailedExactlyOnce_RetriesInAnHour_AndASuccessStartsOver() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "one");
        _host.Server.FailOn = (op, path) => op == "download" && path == World;
        var (watch, jobs) = Watch();
        _clock.UtcNow += HostedWatch.BackupFirstDelay;

        Tick(watch, jobs);
        var sent = Assert.Single(_sent.ToList());
        Assert.Equal(Loc.T("notify.ev.backupFailed"), sent.Title);
        Assert.Contains(sent.Details, l => l.Text == Loc.T("notify.ev.backupToPc", Environment.MachineName));
        Assert.Contains(sent.Details, l => l.Label == Loc.T("notify.lbl.reason") && l.Text.StartsWith(Loc.T("hostedbk.failed", "")[..10], StringComparison.Ordinal));

        _clock.UtcNow += TimeSpan.FromMinutes(59);
        Tick(watch, jobs);
        Assert.Single(_started); // повтор — через час

        _clock.UtcNow += TimeSpan.FromMinutes(2);
        Tick(watch, jobs);
        Assert.Equal(2, _started.Count);
        Assert.Single(Titles()); // та же полоса неудач — молча

        _host.Server.FailOn = null;
        _clock.UtcNow += HostedWatch.BackupRetry;
        Tick(watch, jobs);
        Assert.True(jobs.Last(_host.Profile.Id)!.Ok);
        Assert.Single(Titles());

        // после удачной копии новая неудача — снова одно оповещение
        _host.Server.FailOn = (op, path) => op == "download" && path == World;
        _clock.UtcNow += TimeSpan.FromDays(1);
        Tick(watch, jobs);
        Assert.Equal(2, Titles().Length);
    });

    // ---------- одна копия за раз

    [Fact]
    public void ButtonAndSchedule_NeverDownloadTwoCopiesOfOneServerAtOnce() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "one");
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _host.Server.OnBefore = (op, path) =>
        {
            if (op != "download" || path != World) return;
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        var (watch, jobs) = Watch();
        _clock.UtcNow += HostedWatch.BackupFirstDelay;

        var button = jobs.StartBackup(_host.Target, scheduled: false);
        Assert.NotNull(button);
        Assert.Null(jobs.StartBackup(_host.Target, scheduled: true)); // вторая того же профиля не начинается
        Sta.WaitFor(() => reached.IsSet);
        Sta.Wait(watch.TickAsync());
        Assert.Single(_started); // расписание видит идущую копию и ждёт

        _host.Server.OnBefore = null;
        release.Set();
        Sta.WaitFor(() => jobs.Running(_host.Profile.Id) is null);
        Assert.True(button!.Completion.Result.Ok);

        Tick(watch, jobs);
        Assert.Single(_started); // копия кнопкой свежая — расписанию пока нечего делать
        Assert.Single(Copies());
    });

    [Fact]
    public void WhileACopyHoldsTheConnection_TheWatchDoesNotWaitForIt() => Sta.Run(() =>
    {
        PutLog(HostedLogSamples.Stopped);
        PutWorld(World, "one");
        new ServerNotifySettings
        {
            ServerName = _host.Profile.Name,
            Channels = [new NotifyChannel { Id = "tg", Name = "Telegram", Kind = NotifyKind.Telegram }],
            Routes = new() { [NotifyEvent.ServerStarted] = ["tg"], [NotifyEvent.ModUpdates] = ["tg"] },
        }.Save(_host.Profile.Id, _agents);
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var (watch, jobs) = Watch(); // первый проход: подписка на лог
        _host.Server.OnBefore = (op, path) =>
        {
            if (op != "download" || path != World) return;
            reached.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        Assert.NotNull(jobs.StartBackup(_host.Target, scheduled: false));
        Sta.WaitFor(() => reached.IsSet);

        // минута прошла, и обновления модов пора проверять: опрос и проверка ждали бы конца передачи — проход не ждёт
        _clock.UtcNow += TimeSpan.FromHours(7);
        var tick = watch.TickAsync();
        Sta.WaitFor(() => tick.IsCompleted);
        Assert.False(release.IsSet);

        _host.Server.OnBefore = null;
        release.Set();
        Sta.WaitFor(() => jobs.Running(_host.Profile.Id) is null);
    });
}
