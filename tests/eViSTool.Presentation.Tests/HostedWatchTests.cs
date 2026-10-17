using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Notifications;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Оповещения сервера у хостера, которые шлёт окно (<see cref="HostedWatch"/>): настоящие логи G-Portal на поддельном
/// FTP, отправка перехвачена (сообщения — как ушли бы), часы подменены. Каждое событие — ровно один раз; первый опрос
/// не шлёт прошлого; раздел «Сервер» и сторож не задваивают ни запросы, ни события; без маршрутов профиль не опрашивается;
/// неверный пароль — опрос прекращён; проверка обновлений модов раз в 6 часов не повторяет тот же список и переживает
/// перезапуск окна.
/// </summary>
public sealed class HostedWatchTests : IDisposable
{
    private const string Main = "/Logs/server-main.log";
    private readonly HostedFixture _host = new();
    private readonly string _agents = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-hwatch-" + Guid.NewGuid().ToString("N"))).FullName;
    // часы этой машины: 13:00 по часам сервера (UTC+2) — до строк логов, вылет в 13:40 для сторожа новый
    private readonly TestClock _clock = new(new DateTime(2026, 10, 17, 11, 0, 0, DateTimeKind.Utc));
    private readonly List<(string Channel, NotifyMessage Message)> _sent = [];
    private readonly List<HostedWatch> _watches = [];
    private IReadOnlyList<ModCheckResult> _due = [];
    private int _checks;
    private Exception? _checkError;                              // проверка обновлений падает с этой ошибкой
    private TaskCompletionSource<IReadOnlyList<ModCheckResult>>? _checkGate; // проверка обновлений ждёт, пока тест её отпустит

    public HostedWatchTests() => _host.GPortalLayout(log: false);

    public void Dispose()
    {
        foreach (var watch in _watches) watch.Stop();
        _host.Dispose();
        try { Directory.Delete(_agents, recursive: true); } catch (IOException) { }
    }

    // ---------- стенд

    private static readonly string[] FatalLines =
    [
        "17.10.2026 13:40:00 [Fatal] Game Version: v1.22.7 (Stable)",
        "Critical error occurred in the following mod: badmod@1.0.0",
        "System.InvalidOperationException: boom",
        "   at BadMod.Boom.Now(String where)",
    ];

    private static string[] StoppedTail => HostedLogSamples.Stopped[HostedLogSamples.Running.Length..];

    /// <summary>Отчёт server-crash.log: виновник — badmod.</summary>
    private const string Report = "Game Version: v1.22.7 (Stable)\n17.10.2026 13:40:00: Critical error occurred in the following mod: badmod@1.0.0\n"
                                  + "System.InvalidOperationException: boom\n   at BadMod.Boom.Now(String where)\n";

    private static readonly DateTime CrashUtc = new(2026, 10, 17, 11, 40, 0, DateTimeKind.Utc); // 13:40:00 по часам сервера

    /// <summary>Вылет, видный только по свежему server-crash.log (строк вылета в логе нет); пустой текст — отчёт ещё пишется.</summary>
    private string PutCrashLog(string text)
    {
        var local = _host.Server.Put("/Logs/server-crash.log", text);
        File.SetLastWriteTimeUtc(local, CrashUtc);
        return local;
    }

    private static NotifyLine Culprit => new("⚑", Loc.T("notify.lbl.mod"), Loc.T("notify.ev.culprit", "badmod", "1.0.0"));

    private static string[] NextDay(IEnumerable<string> lines, int day = 18) => [.. lines.Select(l => l.Replace("17.10.2026 ", $"{day}.10.2026 "))];

    /// <summary>Запуск до строки готовности — короче лога с игроком: по размеру видно, что сервер запущен заново.</summary>
    private static string[] Ready => HostedLogSamples.Stopped[..(Array.FindIndex(HostedLogSamples.Stopped, l => l.Contains("now running on Port", StringComparison.Ordinal)) + 1)];

    /// <summary>Самый короткий запуск, который уже «запущен»: первая строка и строка готовности.</summary>
    private static string[] Bare(int day) => NextDay([Ready[0], Ready[^1]], day);

    /// <summary>Лог со временем файла, как у G-Portal: последняя строка только что записана, часы сервера — UTC+2.</summary>
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

    /// <summary>Канал «Telegram» включён у этих событий сервера.</summary>
    private void Routes(params NotifyEvent[] events) => new ServerNotifySettings
    {
        ServerName = _host.Profile.Name,
        Channels = [new NotifyChannel { Id = "tg", Name = "Telegram", Kind = NotifyKind.Telegram }],
        Routes = events.ToDictionary(e => e, _ => new List<string> { "tg" }),
    }.Save(_host.Profile.Id, _agents);

    private static readonly NotifyEvent[] LogEvents =
    [
        NotifyEvent.ServerCrashed, NotifyEvent.ServerStarted, NotifyEvent.ServerStopped, NotifyEvent.PlayerJoined,
        NotifyEvent.PlayerLeft, NotifyEvent.Overloaded,
    ];

    private HostedWatch Watch(IEnumerable<ProfileViewModel>? profiles = null)
    {
        var updates = new HostedModUpdates((_, _) =>
        {
            _checks++;
            if (_checkError is { } error) return Task.FromException<IReadOnlyList<ModCheckResult>>(error);
            return _checkGate?.Task ?? Task.FromResult(_due);
        }, _clock);
        var watch = new HostedWatch(profiles ?? [new ProfileViewModel(_host.Profile, _ => { })], updates, _clock, _agents)
        {
            Send = (channel, message) =>
            {
                lock (_sent) _sent.Add((channel.Id, message));
                return Task.CompletedTask;
            },
        };
        _watches.Add(watch);
        return watch;
    }

    /// <summary>Проход сторожа и всё, что за ним: разбор опросов, поиск вылета, отправка.</summary>
    private void Tick(HostedWatch watch)
    {
        Sta.Wait(watch.TickAsync());
        Settle(watch);
    }

    /// <summary>Дождаться, пока события из очереди окна разобраны и оповещения ушли.</summary>
    private void Settle(HostedWatch watch)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var ran = UiThread.Current!.RunPending();
            if (!ran && watch.IsIdle && watch.SentAsync(_host.Profile.Id).IsCompleted) return;
            if (DateTime.UtcNow > until) throw new TimeoutException("сторож не успокоился");
            if (!ran) Thread.Sleep(10);
        }
    }

    /// <summary>Следующий проход — через минуту.</summary>
    private void Later(HostedWatch watch)
    {
        _clock.UtcNow += HostedWatch.Interval;
        Tick(watch);
    }

    private string[] Titles()
    {
        lock (_sent) return [.. _sent.Select(s => s.Message.Title)];
    }

    private int Calls(string op)
    {
        lock (_host.Server.Calls) return _host.Server.Calls.GetValueOrDefault(op);
    }

    private static ModCheckResult Update(string modId, string installed, string latest) => new()
    {
        Local = new LocalMod($"/Mods/{modId}_{installed}.zip", new ModInfo { ModId = modId, OriginalModId = modId, Name = modId, Version = installed }, null),
        Status = ModStatus.UpdateAvailable,
        LatestCompatible = new ModDbRelease { ModVersion = latest, MainFile = $"https://example.test/{modId}_{latest}.zip" },
    };

    // ---------- события

    [Fact]
    public void FirstPoll_SendsNothingFromThePast() => Sta.Run(() =>
    {
        // вчерашний вылет, игрок на сервере, предупреждения о перегрузке — уже были, когда окно открыли
        _clock.UtcNow = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        PutLog(Main, [.. HostedLogSamples.Running, .. Enumerable.Repeat("17.10.2026 13:39:00 [Warning] Server overloaded. A tick took 600ms to complete.", 12), .. FatalLines]);
        Routes(LogEvents);
        var watch = Watch();

        Tick(watch);
        Later(watch);
        Later(watch);

        Assert.Equal([_host.Profile.Id], watch.Watched);
        Assert.Empty(Titles());
    });

    [Fact]
    public void EachEvent_IsSentOnce_InTheOrderItHappened() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        var watch = Watch();
        Tick(watch); // первый опрос: игрок уже на сервере

        Append(local, StoppedTail); // вышел, зашёл и вышел, сервер остановили из панели
        Later(watch);
        Later(watch);

        Assert.Equal([Loc.T("notify.ev.left", "Erney"), Loc.T("notify.ev.joined", "Erney"), Loc.T("notify.ev.left", "Erney"),
            Loc.T("notify.ev.stopped")], Titles());
        lock (_sent)
        {
            Assert.All(_sent, s => Assert.Equal("tg", s.Channel));
            Assert.All(_sent, s => Assert.Equal("Hosted", s.Message.Server)); // имя профиля
            Assert.Equal(new NotifyLine("◦", Loc.T("notify.lbl.online"), "1"), _sent[1].Message.Details.Single());
        }

        // запуск заново: прошлый лог уехал в архив, новый — с начала
        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", HostedLogSamples.Bytes(HostedLogSamples.Stopped));
        var next = PutLog(Main, NextDay(HostedLogSamples.Starting));
        Later(watch);
        Assert.Equal(4, Titles().Length); // запускается — ещё не «запущен»
        Append(next, NextDay(HostedLogSamples.Running[HostedLogSamples.Starting.Length..]));
        Later(watch);
        Later(watch);

        Assert.Equal(Loc.T("notify.ev.started"), Titles()[4]);
        Assert.Equal(Loc.T("notify.ev.joined", "Erney"), Titles()[^1]); // и зашедший игрок нового запуска
        Assert.Equal(1, Titles().Count(t => t == Loc.T("notify.ev.started")));
    });

    [Fact]
    public void Crash_IsSentOnce_WithTheModToBlame() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        Routes(NotifyEvent.ServerCrashed, NotifyEvent.ServerStopped);
        var watch = Watch();
        Tick(watch);

        Append(local, FatalLines);
        Later(watch);
        Later(watch);
        Later(watch);

        var crash = Assert.Single(_sent).Message; // «упал» — это не «остановлен»
        Assert.Equal(Loc.T("notify.ev.crashed"), crash.Title);
        Assert.Equal(NotifySeverity.Problem, crash.Severity);
        Assert.Equal(new NotifyLine("⚑", Loc.T("notify.lbl.mod"), Loc.T("notify.ev.culprit", "badmod", "1.0.0")), crash.Details.Single());
    });

    [Fact]
    public void Crash_ReportStillBeingWritten_IsWaitedFor_UntilTheNextPoll() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        Routes(NotifyEvent.ServerCrashed);
        var watch = Watch();
        Tick(watch);

        var report = PutCrashLog(""); // упал, а отчёт сервер ещё пишет
        Later(watch);
        Assert.Empty(Titles());       // без отчёта не разбираем: виновника было бы не узнать

        File.WriteAllText(report, Report);
        File.SetLastWriteTimeUtc(report, CrashUtc);
        Later(watch);

        var crash = Assert.Single(_sent).Message;
        Assert.Equal(Loc.T("notify.ev.crashed"), crash.Title);
        Assert.Equal(Culprit, crash.Details.Single());
    });

    [Fact]
    public void LeavingWhileTheServerStops_IsNotAPlayerLeft() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running); // игрок на сервере
        Routes(LogEvents);
        var watch = Watch();
        Tick(watch);

        Append(local,
        [
            "17.10.2026 13:31:53 [Notification] Server termination event SIGTERM received. Shutting down server",
            "17.10.2026 13:31:54 [Event] Player Erney left.",
            "17.10.2026 13:31:54 [Event] Client 3 disconnected.",
            "17.10.2026 13:31:58 [Event] Stopped the server!",
        ]);
        Later(watch);

        Assert.Equal([Loc.T("notify.ev.stopped")], Titles()); // как у агента: остановка — не «все вышли»
    });

    [Fact]
    public void RestartBetweenTwoPolls_IsAStopAndAStart_OrACrashAndAStart() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        var watch = Watch();
        Tick(watch);

        // минуту спустя сервер уже работает снова: остановку никто не видел, вылета в архиве нет
        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", HostedLogSamples.Bytes(HostedLogSamples.Running));
        PutLog(Main, NextDay(Ready));
        Later(watch);

        Assert.Equal([Loc.T("notify.ev.stopped"), Loc.T("notify.ev.started")], Titles());

        // и снова: на этот раз прошлый запуск упал
        lock (_sent) _sent.Clear();
        PutLog("/Logs/Archive/2026-10-18_13_27_08/server-main.log", [.. NextDay(Ready), .. NextDay(FatalLines)]);
        PutLog(Main, Bare(19));
        Later(watch);
        Later(watch);

        Assert.Equal([Loc.T("notify.ev.crashed"), Loc.T("notify.ev.started")], Titles());
    });

    [Fact]
    public void Overload_FollowsTheAgentsRules_ByTheTimeOfTheLines() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        Routes(NotifyEvent.Overloaded);
        var watch = Watch();
        Tick(watch);

        // девять за десять минут — ещё не повод; десятая — повод; дальше в течение часа — тишина
        Append(local, Enumerable.Range(0, 9).Select(i => $"17.10.2026 13:32:{i:00} [Warning] Server overloaded. A tick took 600ms to complete."));
        Later(watch);
        Assert.Empty(Titles());
        Append(local, ["17.10.2026 13:35:00 [Warning] Server overloaded. A tick took 700ms to complete."]);
        Later(watch);
        Append(local, Enumerable.Range(0, 20).Select(i => $"17.10.2026 13:50:{i:00} [Warning] Server overloaded. A tick took 600ms to complete."));
        Later(watch);

        var message = Assert.Single(_sent).Message;
        Assert.Equal(Loc.T("notify.ev.overloaded"), message.Title);
        Assert.Equal(Loc.T("notify.ev.overloadCount", 10, 10), message.Details[0].Text);
        Assert.Equal(Loc.T("notify.ev.overloadHostedHint"), message.Details[1].Text);
    });

    // ---------- раздел «Сервер» и сторож

    [Fact]
    public void OpenSection_AndTheWatch_DoNotDoubleRequestsOrEvents() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        var watch = Watch();
        Tick(watch);
        var section = new HostedServerViewModel(null!) { CrashesSince = DateTime.MinValue };
        section.OnProfileSwitched(_host.Profile);
        Sta.Wait(section.PollAsync()); // раздел открыли: опрашивает сам, раз в 3 с
        Settle(watch);

        Append(local, StoppedTail);
        for (var i = 0; i < 4; i++)
        {
            _clock.UtcNow += TimeSpan.FromSeconds(20); // раздел читает лог часто, сторож проходит своим чередом
            var lists = Calls("list");
            Sta.Wait(section.PollAsync());
            Settle(watch);
            Tick(watch);                               // лог только что читал раздел — сторож в сеть не ходит
            Assert.Equal(lists + 1, Calls("list"));    // один листинг — раздела
        }

        Assert.Equal(HostedLogSamples.Stopped, section.Lines.Select(l => l.Text)); // строки раздела — целы
        Assert.Equal([Loc.T("notify.ev.left", "Erney"), Loc.T("notify.ev.joined", "Erney"), Loc.T("notify.ev.left", "Erney"),
            Loc.T("notify.ev.stopped")], Titles());
    });

    [Fact]
    public void WatchOwnPoll_DoesNotEatTheLinesOfTheSection() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        var watch = Watch();
        var section = new HostedServerViewModel(null!);
        section.OnProfileSwitched(_host.Profile);
        Sta.Wait(section.PollAsync());

        Tick(watch);
        Append(local, StoppedTail);
        Later(watch);                  // сторож прочитал хвост раньше раздела
        Sta.Wait(section.PollAsync());

        Assert.Equal(HostedLogSamples.Stopped, section.Lines.Select(l => l.Text));
        Assert.Equal(HostedServerState.Stopped, section.State);
    });

    [Fact]
    public void Crash_FoundByTheWatch_IsStillShownWhenTheSectionOpens() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        Routes(NotifyEvent.ServerCrashed);
        var watch = Watch();
        Tick(watch);
        Append(local, FatalLines);
        Later(watch);
        Assert.Single(Titles());
        var shown = new List<ServerCrashInfo>();
        var old = AppWindows.Current;
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.ShowServerCrash), a =>
        {
            shown.Add((ServerCrashInfo)a[2]!);
            return null;
        }));
        try
        {
            var section = new HostedServerViewModel(null!) { CrashesSince = DateTime.MinValue };
            section.OnProfileSwitched(_host.Profile);
            Sta.Wait(section.PollAsync());
            Sta.Wait(section.PollAsync());

            Assert.Single(shown); // логи отдали вылет сторожу, но окно вылета раздел всё равно показал — один раз
        }
        finally
        {
            AppWindows.Current = old;
        }
    });

    [Fact]
    public void Crash_SeenByTheOpenSection_TheWatchDoesNotSearchOverIt_AndTheReportIsWaitedFor() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        Routes(NotifyEvent.ServerCrashed);
        var watch = Watch();
        Tick(watch);
        var shown = new List<ServerCrashInfo>();
        var old = AppWindows.Current;
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.ShowServerCrash), a =>
        {
            shown.Add((ServerCrashInfo)a[2]!);
            return null;
        }));
        try
        {
            var section = new HostedServerViewModel(null!) { CrashesSince = DateTime.MinValue };
            section.OnProfileSwitched(_host.Profile);
            Sta.Wait(section.PollAsync());
            Settle(watch);

            var report = PutCrashLog("");  // упал, отчёт ещё пишется
            Sta.Wait(section.PollAsync()); // раздел увидел вылет и ждёт отчёт до следующего опроса
            Settle(watch);
            _clock.UtcNow += TimeSpan.FromSeconds(20);
            Tick(watch);                   // лог только что читал раздел — и вылет ищет тоже он
            Assert.Empty(Titles());
            Assert.Empty(shown);

            File.WriteAllText(report, Report);
            File.SetLastWriteTimeUtc(report, CrashUtc);
            Sta.Wait(section.PollAsync()); // через 3 с отчёт дописан
            Settle(watch);

            Assert.Equal("badmod", Assert.Single(shown).ModId);
            Assert.Equal(Culprit, Assert.Single(_sent).Message.Details.Single());
        }
        finally
        {
            AppWindows.Current = old;
        }
    });

    // ---------- кого сторожить

    [Fact]
    public void ProfileWithoutRoutes_IsNotPolled() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        new ServerNotifySettings { Channels = [new NotifyChannel { Id = "tg", Name = "Telegram" }] }.Save(_host.Profile.Id, _agents);
        var watch = Watch();

        Tick(watch);
        Later(watch);

        Assert.Empty(watch.Watched);
        Assert.Equal(0, Calls("connect"));
        Assert.Equal(0, Calls("list"));
    });

    [Fact]
    public void OnlyModRoutes_DoNotReadTheLog() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        Routes(NotifyEvent.ModsUpdated, NotifyEvent.ModUpdates);
        var watch = Watch();

        Tick(watch);
        Later(watch);

        Assert.Equal([_host.Profile.Id], watch.Watched);
        Assert.Null(watch.LogsOf(_host.Profile.Id));
        Assert.Equal(0, Calls("list"));
    });

    [Fact]
    public void RoutesTurnedOff_TheWatchLetsGo() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        var watch = Watch();
        Tick(watch);
        var logs = watch.LogsOf(_host.Profile.Id)!;
        Assert.Null(HostedWatchLease.TryTake(_host.Profile.Id, _agents)); // пока следит — замок занят

        Routes();
        Later(watch);
        Append(local, StoppedTail);
        Sta.Wait(logs.PollAsync(default)); // раздел читает дальше — сторожу уже всё равно
        Settle(watch);

        Assert.Empty(watch.Watched);
        using (var lease = HostedWatchLease.TryTake(_host.Profile.Id, _agents)) Assert.NotNull(lease); // замок отпущен
        Assert.Empty(Titles());
    });

    [Fact]
    public void ProfileRemovedMidPass_IsLetGoAtOnce_AndItsCheckSendsAndWritesNothing() => Sta.Run(() =>
    {
        Routes(NotifyEvent.ModUpdates);
        _due = [Update("alpha", "1.0.0", "1.1.0")];
        _checkGate = new TaskCompletionSource<IReadOnlyList<ModCheckResult>>();
        var profiles = new List<ProfileViewModel> { new(_host.Profile, _ => { }) };
        var watch = Watch(profiles);
        Tick(watch);
        _clock.UtcNow += HostedWatch.ModsFirstDelay;
        var pass = watch.TickAsync();              // проверка обновлений идёт (долгий скан по FTP)
        Assert.Equal(1, _checks);

        profiles.Clear();                          // профиль удалили, пока она шла
        watch.Refresh();
        Assert.Empty(watch.Watched);               // отпущен сразу, вместе с замком
        using (var lease = HostedWatchLease.TryTake(_host.Profile.Id, _agents)) Assert.NotNull(lease);

        _checkGate.SetResult(_due);
        Sta.Wait(pass);
        Settle(watch);
        Assert.Empty(Titles());
        Assert.False(File.Exists(HostedNotifyState.FileFor(_host.Profile.Id, _agents)));
        Assert.Empty(watch.Watched);
    });

    [Fact]
    public void RemovingTheProfile_LetsItGo_AndForgetsItsNotificationFiles() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        var id = _host.Profile.Id;
        // главная модель сторожит в папке агентов этой машины (данные рядом с тестами)
        new ServerNotifySettings
        {
            Channels = [new NotifyChannel { Id = "tg", Name = "Telegram", Kind = NotifyKind.Telegram }],
            Routes = new() { [NotifyEvent.ServerStopped] = ["tg"] },
        }.Save(id);
        new HostedNotifyState { ModsCheckedAt = _clock.UtcNow, ModUpdates = ["alpha: 1.0.0 → 1.1.0"] }.Save(id);
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel { AutoCheckUpdates = false };
        Loc.Instance.SetLanguage(language);
        try
        {
            main.Profiles.Clear();
            var own = new ProfileViewModel(new GameProfile { Id = "own-" + Guid.NewGuid().ToString("N"), Name = "Свой", Kind = ProfileKind.Server }, _ => { });
            var hosted = new ProfileViewModel(_host.Profile, _ => { });
            main.Profiles.Add(own);
            main.Profiles.Add(hosted);
            main.ActiveProfile = own;
            main.HostedWatch.Refresh();
            Sta.WaitFor(() => main.HostedWatch.Watched.Contains(id) && main.HostedWatch.IsIdle);
            Assert.Null(HostedWatchLease.TryTake(id)); // следит — замок занят

            using var dialogs = new ScriptedDialogs(Answer.Yes);
            Sta.Wait(main.RemoveProfileCommand.ExecuteAsync(hosted));

            Assert.Empty(main.HostedWatch.Watched);
            Assert.False(File.Exists(HostedWatchLease.FileFor(id)));     // замок отпущен и убран
            Assert.False(File.Exists(ServerNotifySettings.FileFor(id))); // настройки с каналами — тоже
            Assert.False(File.Exists(HostedNotifyState.FileFor(id)));
        }
        finally
        {
            main.HostedWatch.Stop();
            foreach (var file in new[] { ServerNotifySettings.FileFor(id), HostedNotifyState.FileFor(id), HostedWatchLease.FileFor(id) })
                try { File.Delete(file); } catch (IOException) { }
        }
    });

    [Fact]
    public void RoutesTurnedOnMidPass_AreTakenRightAfterIt() => Sta.Run(() =>
    {
        using var other = new HostedFixture();
        Routes(NotifyEvent.ModUpdates);
        _checkGate = new TaskCompletionSource<IReadOnlyList<ModCheckResult>>();
        var watch = Watch([new ProfileViewModel(_host.Profile, _ => { }), new ProfileViewModel(other.Profile, _ => { })]);
        Tick(watch);
        _clock.UtcNow += HostedWatch.ModsFirstDelay;
        var pass = watch.TickAsync();              // первый сервер проверяет обновления

        new ServerNotifySettings
        {
            Channels = [new NotifyChannel { Id = "tg", Name = "Telegram", Kind = NotifyKind.Telegram }],
            Routes = new() { [NotifyEvent.ModsUpdated] = ["tg"] },
        }.Save(other.Profile.Id, _agents);         // второму включили оповещения
        watch.Refresh();
        Assert.DoesNotContain(other.Profile.Id, watch.Watched);

        _checkGate.SetResult([]);
        Sta.Wait(pass);
        Settle(watch);
        Assert.Contains(other.Profile.Id, watch.Watched); // сразу после прохода, а не по таймеру через минуту
    });

    [Fact]
    public void AnotherWindowWatching_ThisOneStaysQuiet() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        using var other = HostedWatchLease.TryTake(_host.Profile.Id, _agents); // за сервером следит другое окно
        var watch = Watch();

        Tick(watch);

        Assert.Null(watch.LogsOf(_host.Profile.Id));
        Assert.Equal(0, Calls("list"));
    });

    [Fact]
    public void RenamedProfile_ComesWithTheNewName() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        Routes(NotifyEvent.ServerStopped);
        var watch = Watch();
        Tick(watch);

        _host.Profile.Name = "Survival";
        Append(local, StoppedTail);
        Later(watch);

        Assert.Equal("Survival", Assert.Single(_sent).Message.Server);
    });

    // ---------- сеть

    [Fact]
    public void WrongPassword_StopsPolling_UntilTheConnectionIsEdited() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        _host.Server.FailOn = (op, _) => op == "connect";
        _host.Server.FailKind = RemoteFailure.Auth;
        var watch = Watch();

        Tick(watch);
        var connects = Calls("connect");
        Assert.True(connects > 0);
        for (var i = 0; i < 5; i++)
        {
            _clock.UtcNow += HostedWatch.MaxRetry;
            Tick(watch);
        }
        Assert.Equal(connects, Calls("connect")); // неверный пароль раз в минуту — тысячи неудачных входов в сутки

        _host.Server.FailOn = null;
        _host.Settings.Password = HostedSecret.Protect("fixed"); // поправили подключение
        Later(watch);

        Assert.True(Calls("connect") > connects);
        Assert.Equal(HostedServerState.Running, watch.LogsOf(_host.Profile.Id)!.State);
    });

    [Fact]
    public void NetworkError_IsRetriedQuietly_WithALongerPause() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        _host.Server.FailOn = (op, _) => op == "list";
        _host.Server.FailKind = RemoteFailure.Timeout;
        var watch = Watch();

        Tick(watch);
        var lists = Calls("list");
        Later(watch);                                // через минуту — ещё рано
        Assert.Equal(lists, Calls("list"));
        _clock.UtcNow += HostedWatch.FirstRetry;     // через паузу — снова
        Tick(watch);
        Assert.True(Calls("list") > lists);

        _host.Server.FailOn = null;
        _clock.UtcNow += HostedWatch.FirstRetry * 2;
        Tick(watch);
        Assert.Equal(HostedServerState.Running, watch.LogsOf(_host.Profile.Id)!.State);
        Assert.Empty(Titles()); // ни ошибок в каналы, ни «прошлого»
    });

    [Fact]
    public void WrongPassword_WhileLookingForTheCrash_StopsTheSearchToo() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        var watch = Watch();
        Tick(watch);

        PutCrashLog(Report); // упал — а прочитать отчёт уже не пускают
        _host.Server.FailKind = RemoteFailure.Auth;
        _host.Server.FailOn = (op, path) => op == "read" && path == "/Logs/server-crash.log";
        Later(watch);
        var calls = Calls("list") + Calls("read") + Calls("connect");
        for (var i = 0; i < 3; i++)
        {
            _clock.UtcNow += HostedWatch.MaxRetry;
            Tick(watch);
        }

        Assert.Equal(calls, Calls("list") + Calls("read") + Calls("connect")); // ни поиска раз в минуту, ни опроса
        Assert.Empty(Titles());
    });

    [Fact]
    public void NetworkError_WhileLookingForTheCrash_WaitsLikeThePoll() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        var watch = Watch();
        Tick(watch);

        PutCrashLog(Report);
        _host.Server.FailKind = RemoteFailure.Timeout;
        _host.Server.FailOn = (op, path) => op == "read" && path == "/Logs/server-crash.log";
        Later(watch);
        var reads = Calls("read");
        Later(watch); // через минуту — ещё рано
        Assert.Equal(reads, Calls("read"));

        _host.Server.FailOn = null;
        _clock.UtcNow += HostedWatch.FirstRetry;
        Tick(watch);
        Assert.Equal(Culprit, Assert.Single(_sent).Message.Details.Single()); // после паузы вылет нашёлся
    });

    [Fact]
    public void WrongPasswordDuringMaintenance_ASuccessfulReadByTheSection_BringsTheWatchBack() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        Routes(LogEvents);
        _host.Server.FailOn = (op, _) => op == "connect";
        _host.Server.FailKind = RemoteFailure.Auth; // хостер на обслуживании отвечает «530» на вход
        var watch = Watch();
        Tick(watch);

        _host.Server.FailOn = null;                 // обслуживание кончилось, пароль прежний
        var section = new HostedServerViewModel(null!);
        section.OnProfileSwitched(_host.Profile);
        Sta.Wait(section.PollAsync());              // раздел открыли — лог читается
        Settle(watch);

        // раздел закрыли: дальше лог читает сторож — и сообщает
        Append(local, StoppedTail);
        Later(watch);
        Assert.Contains(Loc.T("notify.ev.stopped"), Titles());
    });

    // ---------- обновления модов

    [Fact]
    public void ModUpdates_AreCheckedEvery6Hours_TheSameListIsNotRepeated_AndItSurvivesARestart() => Sta.Run(() =>
    {
        Routes(NotifyEvent.ModUpdates);
        _due = [Update("alpha", "1.0.0", "1.1.0")];
        var watch = Watch();

        Tick(watch);
        Assert.Equal(0, _checks);                    // не сразу после запуска окна
        _clock.UtcNow += HostedWatch.ModsFirstDelay;
        Tick(watch);
        Assert.Equal(1, _checks);
        var message = Assert.Single(_sent).Message;
        Assert.Equal(Loc.T("notify.ev.modUpdates", 1), message.Title);
        Assert.Equal("• alpha: 1.0.0 → 1.1.0", message.Details[0].Text);
        Assert.Equal(Loc.T("notify.ev.modUpdatesHostedHint"), message.Details[^1].Text);

        _clock.UtcNow += TimeSpan.FromHours(1);
        Tick(watch);
        Assert.Equal(1, _checks);                    // раз в 6 часов
        _clock.UtcNow += HostedWatch.ModsInterval;
        Tick(watch);
        Assert.Equal(2, _checks);
        Assert.Single(_sent);                        // тот же список — не повторяем

        // окно перезапустили: время проверки и список — из файла
        watch.Stop();
        var again = Watch();
        Tick(again);
        _clock.UtcNow += HostedWatch.ModsFirstDelay;
        Tick(again);
        Assert.Equal(2, _checks);
        _clock.UtcNow += HostedWatch.ModsInterval;
        Tick(again);
        Assert.Equal(3, _checks);
        Assert.Single(_sent);

        _due = [Update("alpha", "1.0.0", "1.1.0"), Update("beta", "2.0.0", "2.0.1")];
        _clock.UtcNow += HostedWatch.ModsInterval;
        Tick(again);
        Assert.Equal(2, _sent.Count);
        Assert.Equal(Loc.T("notify.ev.modUpdates", 2), _sent[1].Message.Title);
    });

    [Fact]
    public void ModUpdates_WrongPassword_IsNotRetriedEvery30Minutes() => Sta.Run(() =>
    {
        Routes(NotifyEvent.ModUpdates);
        _checkError = new RemoteFilesException(RemoteFailure.Auth, "530 Login incorrect");
        var watch = Watch();
        Tick(watch);
        _clock.UtcNow += HostedWatch.ModsFirstDelay;
        Tick(watch);
        Assert.Equal(1, _checks);

        for (var i = 0; i < 4; i++)
        {
            _clock.UtcNow += HostedWatch.ModsRetry;
            Tick(watch);
        }
        Assert.Equal(1, _checks);            // неверный пароль — не 48 входов в сутки
        _clock.UtcNow += HostedWatch.ModsInterval;
        Tick(watch);
        Assert.Equal(2, _checks);            // в срок следующей проверки — снова
        Assert.Empty(Titles());
    });

    [Fact]
    public void ModsChangedByTheWindow_AreOneMessage_WhenTheOperationEnds() => Sta.Run(() =>
    {
        Routes(NotifyEvent.ModsUpdated);
        var watch = Watch();
        var target = _host.Target;

        watch.ModsChanged(target, ["alpha: 1.0.0 → 1.1.0"], []);
        watch.ModsChanged(target, ["beta: 2.0.0 → 2.0.1"], ["gamma_1.0.zip: нет места"]);
        watch.ModsChanged(ModTarget.For(new GameProfile { Id = "own", Name = "Own", Kind = ProfileKind.Server })!, ["own: 1 → 2"], []); // свой — не сюда
        Assert.Empty(Titles());                      // пока операция идёт — копим
        watch.ModsChangeDone();
        Settle(watch);
        watch.ModsChangeDone();                      // второй итог без перемен — ничего
        Settle(watch);

        var message = Assert.Single(_sent).Message;
        Assert.Equal(Loc.T("notify.ev.modsUpdated", 2), message.Title);
        Assert.Equal(["• alpha: 1.0.0 → 1.1.0", "• beta: 2.0.0 → 2.0.1", "✗ gamma_1.0.zip: нет места"], message.Details.Select(d => d.Text));
    });

    /// <summary>Часы для тестов: время двигает сам тест.</summary>
    private sealed class TestClock(DateTime utc) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utc;

        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }
}
