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

    private HostedWatch Watch()
    {
        var updates = new HostedModUpdates((_, _) =>
        {
            _checks++;
            return Task.FromResult(_due);
        }, _clock);
        var watch = new HostedWatch([new ProfileViewModel(_host.Profile, _ => { })], updates, _clock, _agents)
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
