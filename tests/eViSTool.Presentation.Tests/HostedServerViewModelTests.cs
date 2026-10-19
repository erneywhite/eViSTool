using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Presentation.ViewModels;
using eViSTool.Presentation.ViewModels.Mods;
using Newtonsoft.Json.Linq;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Раздел «Сервер» у сервера у хостера: состояние, консоль только для чтения, игроки и прошлые запуски по логу G-Portal
/// (настоящие логи, поддельный FTP «как G-Portal» над временной папкой; время файлов — как у G-Portal: MLSD в UTC, часы
/// сервера UTC+2). Опрос — только пока раздел открыт и активен хостовый профиль; новый вылет — окном один раз. Вопрос
/// «останови сервер в панели хостера» после быстрой проверки по логу: остановлен — без вопроса, иначе — как раньше.
/// </summary>
public sealed class HostedServerViewModelTests : IDisposable
{
    private const string Main = "/Logs/server-main.log";
    private readonly HostedFixture _host = new();
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;

    public HostedServerViewModelTests() => _host.GPortalLayout(log: false);

    public void Dispose()
    {
        AppWindows.Current = _oldWindows!;
        _host.Dispose();
    }

    // ---------- стенд

    /// <summary>
    /// Положить лог на «сервер» (по умолчанию — стенда теста) со временем файла, как у G-Portal: последняя строка только
    /// что записана, часы сервера — UTC+2.
    /// </summary>
    private string PutLog(string remote, string[] lines, HostedFixture? on = null)
    {
        var marks = new HostedRunMarks();
        foreach (var line in lines) marks.Add(line);
        var local = (on ?? _host).Server.Put(remote, HostedLogSamples.Bytes(lines));
        File.SetLastWriteTimeUtc(local, DateTime.SpecifyKind(marks.LastTime!.Value.AddHours(-2), DateTimeKind.Utc));
        return local;
    }

    private static void Append(string local, IEnumerable<string> lines)
    {
        using var file = new FileStream(local, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        file.Write(HostedLogSamples.Bytes(lines));
    }

    /// <summary>Конец запуска после того, что было в логе запущенного сервера: игрок выходит, остановка из панели.</summary>
    private static string[] StoppedTail => HostedLogSamples.Stopped[HostedLogSamples.Running.Length..];

    private static string[] NextDay(IEnumerable<string> lines) => [.. lines.Select(l => l.Replace("17.10.2026 ", "18.10.2026 "))];

    private static readonly string[] FatalLines =
    [
        "17.10.2026 13:40:00 [Fatal] Game Version: v1.22.7 (Stable)",
        "Critical error occurred in the following mod: badmod@1.0.0",
        "System.InvalidOperationException: boom",
        "   at BadMod.Boom.Now(String where)",
    ];

    /// <summary>Раздел на профиле стенда, без главного окна: окно вылета подменяет тест.</summary>
    private HostedServerViewModel Vm(DateTime? crashesSince = null)
    {
        var vm = new HostedServerViewModel(null!) { CrashesSince = crashesSince ?? DateTime.Now };
        vm.OnProfileSwitched(_host.Profile);
        return vm;
    }

    private static void Poll(HostedServerViewModel vm) => Sta.Wait(vm.PollAsync());

    private static string[] Texts(HostedServerViewModel vm) => [.. vm.Lines.Select(l => l.Text)];

    private static IUiTimer Timer(HostedServerViewModel vm) =>
        (IUiTimer)typeof(HostedServerViewModel).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;

    /// <summary>Время сервера (UTC+2) → время этой машины.</summary>
    private static DateTime Local(int hour, int minute, int second) =>
        new DateTime(2026, 10, 17, hour - 2, minute, second, DateTimeKind.Utc).ToLocalTime();

    /// <summary>Главное окно со своим серверным профилем (папка на диске) и профилем у хостера; активен хостовый.</summary>
    private (MainViewModel Main, ProfileViewModel Hosted, ProfileViewModel Own) Open()
    {
        var data = Directory.CreateDirectory(Path.Combine(_host.Dir, "own-data")).FullName;
        Directory.CreateDirectory(Path.Combine(data, "Mods"));
        File.WriteAllText(Path.Combine(data, ProfileResolver.ServerConfigName), """{ "ModPaths": [ "Mods" ] }""");
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel { AutoCheckUpdates = false };
        Loc.Instance.SetLanguage(language);
        main.Profiles.Clear();
        var own = new ProfileViewModel(new GameProfile { Id = "own-" + Guid.NewGuid().ToString("N")[..6], Name = "Own", Kind = ProfileKind.Server, DataDir = data }, _ => { });
        var hosted = new ProfileViewModel(_host.Profile, _ => { });
        main.Profiles.Add(own);
        main.Profiles.Add(hosted);
        main.ActiveProfile = hosted;
        Sta.WaitFor(() => main.Mods.HostedSource.Resolved is not null && !main.Mods.HostedSource.IsLoading);
        return (main, hosted, own);
    }

    // ---------- состояние, консоль, игроки

    [Fact]
    public void Poll_ShowsTheRunningServer_TheLinesWithTheConsoleTints_AndThePlayer() => Sta.Run(() =>
    {
        string[] log = [.. HostedLogSamples.Running, "17.10.2026 13:31:19 [Error] Something broke", "17.10.2026 13:31:19 [Warning] Server overloaded"];
        PutLog(Main, log);
        var vm = Vm();
        var appended = new List<bool>();
        vm.LinesAppended += appended.Add;

        Assert.Equal(Loc.T("server.hostedReadingLog"), vm.StateNote); // ещё не читали
        Poll(vm);

        Assert.Equal(HostedServerState.Running, vm.State);
        Assert.Equal(Loc.T("server.stateRunning"), vm.StateText);
        Assert.Equal(RowTone.Good, vm.StateTone);
        Assert.StartsWith(Loc.T("server.hostedByLog", "").TrimEnd(), vm.StateNote);
        Assert.Equal(log, Texts(vm));
        Assert.Equal([true], appended); // первая порция — прокрутить к концу в любом случае
        // цвет — как у своей консоли: по уровню записи
        Assert.Equal(ConsoleTint.Error, vm.Lines.Single(l => l.Text.EndsWith("Something broke")).Tint);
        Assert.Equal(ConsoleTint.Warning, vm.Lines.Single(l => l.Text.EndsWith("Server overloaded")).Tint);
        Assert.Equal(0.75, vm.Lines[0].Opacity); // [Notification] — приглушённо
        Assert.Null(vm.Lines.First(l => l.Text.Contains("[Event]")).Tint);
        Assert.False(vm.ShowEmpty);

        var player = Assert.Single(vm.Players);
        Assert.Equal("Erney", player.Name);
        Assert.StartsWith(Loc.T("server.hostedPlayerSince", Dates.Clock(Local(13, 31, 18)), ""), player.SinceText);
        Assert.DoesNotContain("203.0.113.7", player.SinceText); // адрес игрока не показываем
        Assert.Equal("1", vm.PlayersText);
        Assert.Equal("Erney", vm.PlayerNames);
        Assert.NotEqual("", vm.UptimeText);
        Assert.Equal(Loc.T("server.hostedRunCurrent"), Assert.Single(vm.Runs).Text); // архива нет — только текущий
    });

    [Fact]
    public void NewLines_AreAppended_StopShowsNobody_AndARestartStartsTheConsoleOver() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var vm = Vm();
        Poll(vm);
        var appended = new List<bool>();
        vm.LinesAppended += appended.Add;

        Append(local, StoppedTail);
        Poll(vm);

        Assert.Equal(HostedLogSamples.Stopped, Texts(vm)); // без повторов
        Assert.Equal([false], appended);                   // дописали — прокрутка, только если пользователь внизу
        Assert.Equal(HostedServerState.Stopped, vm.State);
        Assert.Equal(Loc.T("server.stateStopped"), vm.StateText);
        Assert.Empty(vm.Players);
        Assert.False(vm.HasPlayers);
        Assert.Equal(Loc.T("server.hostedPlayersDown"), vm.PlayersEmptyText);
        Assert.Equal("", vm.PlayersText); // не запущен — плитки пустые, без прочерков
        Assert.Equal("", vm.UptimeText);

        // новый запуск: прошлый лог уехал в архив, новый короче
        _host.Server.Put("/Logs/Archive/2026-10-17_13_27_08/server-main.log", HostedLogSamples.Bytes(HostedLogSamples.Stopped));
        var fresh = NextDay(HostedLogSamples.Starting);
        PutLog(Main, fresh);
        Poll(vm);

        Assert.Equal(fresh, Texts(vm));
        Assert.Equal(HostedServerState.Starting, vm.State);
        Assert.Equal(Loc.T("server.stateStarting"), vm.StateText);
        Assert.Equal(RowTone.Update, vm.StateTone);
        Assert.True(appended[^1]);                          // с чистого листа — к концу в любом случае
        Assert.Equal([null, "2026-10-17_13_27_08"], vm.Runs.Select(r => r.Run?.Name)); // список запусков освежён
    });

    [Fact]
    public void EmptyLogFolder_SaysSo_InsteadOfAnEmptyBlackBox() => Sta.Run(() =>
    {
        _host.Server.MakeDir("/Logs");
        var vm = Vm();

        Poll(vm);

        Assert.Equal(HostedServerState.Unknown, vm.State);
        Assert.Equal(Loc.T("server.hostedStateUnknown"), vm.StateText);
        Assert.True(vm.ShowEmpty);
        Assert.Equal("—", vm.PlayersEmptyText);
    });

    // ---------- когда идёт опрос

    [Fact]
    public void Polling_OnlyWhileTheSectionIsOpenAndTheHostedProfileIsActive_ThenGoesOnFromTheSamePlace() => Sta.Run(() =>
    {
        var local = PutLog(Main, HostedLogSamples.Running);
        var (main, hosted, own) = Open();
        var vm = main.Server.Hosted;
        Assert.False(vm.IsPolling); // раздел не открыт — за логом не ходим
        Assert.Empty(vm.Lines);

        main.SelectedTab = AppTab.Server;
        Assert.True(vm.IsPolling);
        Sta.WaitFor(() => vm.State == HostedServerState.Running);
        Assert.Equal(HostedLogSamples.Running, Texts(vm));
        Assert.Equal(HostedServerViewModel.PollInterval, Timer(vm).Interval);
        Assert.Equal(TimeSpan.FromSeconds(3), HostedServerViewModel.PollInterval);

        main.SelectedTab = AppTab.Mods;  // ушли с раздела — таймер стоит
        Assert.False(vm.IsPolling);
        Assert.False(Timer(vm).IsEnabled);
        Append(local, StoppedTail);

        main.SelectedTab = AppTab.Server; // вернулись — с того же места лога, без повторов
        Assert.True(vm.IsPolling);
        Sta.WaitFor(() => vm.State == HostedServerState.Stopped);
        Assert.Equal(HostedLogSamples.Stopped, Texts(vm));

        main.ActiveProfile = own;         // свой профиль — опроса нет, консоль хостера пуста
        Assert.False(vm.IsPolling);
        Assert.Empty(vm.Lines);
        Assert.Equal(HostedServerState.Unknown, vm.State);

        main.ActiveProfile = hosted;      // снова хостер — весь текущий запуск, без повторов
        Assert.True(vm.IsPolling);
        Sta.WaitFor(() => vm.Lines.Count > 0);
        Assert.Equal(HostedLogSamples.Stopped, Texts(vm));

        main.SelectedTab = AppTab.Settings;
        Assert.False(vm.IsPolling);
    });

    [Fact]
    public void SameProfileTouched_KeepsTheConsole_OtherConnection_StartsOver() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        var vm = Vm();
        Poll(vm);

        _host.Profile.Name = "Renamed";   // имя, флажок — тот же сервер
        _host.Settings.SkipStopReminder = true;
        vm.OnProfileSwitched(_host.Profile);
        Assert.Equal(HostedLogSamples.Running, Texts(vm));

        _host.Settings.Port = 2121;       // другое подключение — прежние строки не про него
        vm.OnProfileSwitched(_host.Profile);
        Assert.Empty(vm.Lines);
        Assert.Equal(HostedServerState.Unknown, vm.State);
    });

    [Fact]
    public void NetworkError_IsAStatusLine_PollingSlowsDownAndRecovers() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        var vm = Vm();
        _host.Server.FailOn = (op, _) => op == "list";
        _host.Server.FailKind = RemoteFailure.Timeout;

        vm.SetActive(true);
        Sta.WaitFor(() => vm.StatusText.Length > 0);

        Assert.Equal(Loc.T("server.hostedRetry", Loc.T("hosted.errTimeout"), 10), vm.StatusText);
        Assert.Equal(HostedServerViewModel.RetryInterval, Timer(vm).Interval);
        Assert.True(vm.IsPolling);         // опрос не бросаем
        Assert.Equal(HostedServerState.Unknown, vm.State);

        _host.Server.FailOn = null;        // связь вернулась
        Poll(vm);

        Assert.Equal("", vm.StatusText);
        Assert.Equal(HostedServerViewModel.PollInterval, Timer(vm).Interval);
        Assert.Equal(HostedServerState.Running, vm.State);
        vm.SetActive(false);
    });

    [Fact]
    public void UnreadablePassword_SaysWhy_AndDoesNotPoll() => Sta.Run(() =>
    {
        _host.Settings.Password = "not-a-protected-secret";
        var vm = Vm();

        vm.SetActive(true);

        Assert.Equal(Loc.T("hosted.errDecrypt"), vm.StatusText);
        Assert.False(vm.IsPolling);
        Assert.False(_host.Server.Calls.ContainsKey("connect"));
    });

    [Fact]
    public void SwitchingToAnotherHostedProfile_WithTheSectionOpen_ReadsItAtOnce_WithTheUsualInterval() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        using var other = new HostedFixture();
        other.GPortalLayout(log: false);
        var otherLog = NextDay(HostedLogSamples.Stopped);
        PutLog(Main, otherLog, other);
        var vm = Vm();
        _host.Server.FailOn = (op, _) => op == "list";  // у первого профиля нет связи: следующая попытка — через 10 с
        _host.Server.FailKind = RemoteFailure.Timeout;
        vm.SetActive(true);
        Sta.WaitFor(() => vm.StatusText.Length > 0);
        Assert.Equal(HostedServerViewModel.RetryInterval, Timer(vm).Interval);

        var clock = Stopwatch.StartNew();
        vm.OnProfileSwitched(other.Profile);
        Assert.Equal(HostedServerViewModel.PollInterval, Timer(vm).Interval); // интервал — обычный, не от чужой ошибки
        Sta.WaitFor(() => vm.Lines.Count > 0);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), clock.Elapsed.ToString()); // сразу, не по тику прежнего таймера
        Assert.Equal(otherLog, Texts(vm));
        Assert.Equal("", vm.StatusText);
        Assert.True(vm.IsPolling);
        vm.SetActive(false);
    });

    [Fact]
    public void PollStartedBeforeAProfileSwitch_IsThrownAway() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        using var other = new HostedFixture();
        other.GPortalLayout(log: false);
        var otherLog = NextDay(HostedLogSamples.Stopped);
        PutLog(Main, otherLog, other);
        // первый сервер отвечает, только когда тест отпустит; отмену не слушает — ответ придёт уже после переключения
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = false;
        using var hook = RemoteFilesFactory.RegisterHost(_host.Host, _ => new HookFiles(_host.Server, async () =>
        {
            entered = true;
            await release.Task;
        }));
        var vm = Vm();

        var poll = vm.PollAsync();
        Sta.WaitFor(() => entered);
        vm.OnProfileSwitched(other.Profile); // пока первый читается — другой профиль
        release.SetResult();
        Sta.Wait(poll);

        Assert.Empty(vm.Lines);              // строки первого в консоль второго не попали
        Assert.Equal(HostedServerState.Unknown, vm.State);
        Assert.Empty(vm.Runs);
        Poll(vm);
        Assert.Equal(otherLog, Texts(vm));
        Assert.Equal(HostedServerState.Stopped, vm.State);
    });

    [Fact]
    public void LeavingTheSectionDuringAPoll_TheTimerStaysStopped_EvenAfterAnError() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = false;
        using var hook = RemoteFilesFactory.RegisterHost(_host.Host, _ => new HookFiles(_host.Server, async () =>
        {
            entered = true;
            await release.Task;
        }));
        var vm = Vm();

        vm.SetActive(true);                  // открыли — опрос сразу
        Sta.WaitFor(() => entered);
        vm.SetActive(false);                 // ушли, пока сервер отвечает
        Assert.False(vm.IsPolling);
        _host.Server.FailOn = (op, _) => op == "list"; // и ответ — сбой сети
        _host.Server.FailKind = RemoteFailure.Timeout;
        release.SetResult();
        Sta.WaitFor(() => vm.StatusText.Length > 0);

        Assert.False(vm.IsPolling);
        Assert.False(Timer(vm).IsEnabled);   // новый интервал таймер не запустил
    });

    [Theory]
    [InlineData(RemoteFailure.Auth)]
    [InlineData(RemoteFailure.CertificateUntrusted)]
    [InlineData(RemoteFailure.Unsupported)]
    public void ErrorsARetryWillNotFix_AreNotRetried_UntilTheSectionIsOpenedAgain(RemoteFailure kind) => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        var vm = Vm();
        _host.Server.FailOn = (op, _) => op == "connect";
        _host.Server.FailKind = kind;

        vm.SetActive(true);
        Sta.WaitFor(() => vm.StatusText.Length > 0);

        var reason = kind switch
        {
            RemoteFailure.Auth => Loc.T("hosted.errAuth"),
            RemoteFailure.CertificateUntrusted => Loc.T("server.hostedCertUntrusted"), // доверить здесь нельзя — без «доверяй, только если…»
            _ => Loc.T("hosted.errNoTransport"),
        };
        Assert.Equal(Loc.T("server.hostedNoRetry", reason), vm.StatusText);
        Assert.False(vm.IsPolling);          // неверный пароль каждые 10 с — сотни неудачных входов в час
        Assert.False(Timer(vm).IsEnabled);

        _host.Server.FailOn = null;          // поправили подключение
        vm.SetActive(false);
        vm.SetActive(true);                  // открыли раздел снова — пробуем ещё раз
        Assert.True(vm.IsPolling);
        Sta.WaitFor(() => vm.State == HostedServerState.Running);
        Assert.Equal("", vm.StatusText);
        vm.SetActive(false);
    });

    /// <summary>SFTP: незнакомый или сменившийся ключ сервера и беда с файлом ключа повтором не лечатся — своими словами,
    /// без «доверяй, только если…» (довериться можно только в окне подключения).</summary>
    [Theory]
    [InlineData("hostkey")]
    [InlineData("hostkeychanged")]
    [InlineData("keyfile")]
    [InlineData("passphrase")]
    public void SftpErrorsARetryWillNotFix_AreNotRetried_AndSaidInTheirOwnWords(string what) => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        var vm = Vm();
        _host.Server.FailOn = (op, _) => op == "connect";
        _host.Server.FailWith = () => what switch
        {
            "hostkey" or "hostkeychanged" => new RemoteFilesException(RemoteFailure.CertificateUntrusted, "connect: untrusted host key ssh-ed25519 256")
            {
                CertificateSha256 = "SHA256:" + new string('Q', 43), IsHostKey = true, HostKeyChanged = what == "hostkeychanged",
            },
            "keyfile" => new RemoteFilesException(RemoteFailure.KeyFile, "C:\\keys\\id_ed25519: Could not find file"),
            _ => new RemoteFilesException(RemoteFailure.KeyPassphrase, "C:\\keys\\id_ed25519: wrong passphrase"),
        };

        vm.SetActive(true);
        Sta.WaitFor(() => vm.StatusText.Length > 0);

        var reason = what switch
        {
            "hostkey" => Loc.T("server.hostedHostKeyUntrusted"),
            "hostkeychanged" => Loc.T("server.hostedHostKeyChanged"),
            "keyfile" => Loc.T("hosted.errKeyFile", "C:\\keys\\id_ed25519: Could not find file") + ".",
            _ => Loc.T("hosted.errKeyPassphrase"),
        };
        Assert.Equal(Loc.T("server.hostedNoRetry", reason), vm.StatusText);
        Assert.DoesNotContain(new string('Q', 43), vm.StatusText);
        Assert.False(vm.IsPolling);
        Assert.False(Timer(vm).IsEnabled);
        vm.SetActive(false);
    });

    [Fact]
    public void ErrorText_EndsItsSentence_BeforeTheRetryNote() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        var vm = Vm();
        _host.Server.FailOn = (op, _) => op == "list";
        _host.Server.FailKind = RemoteFailure.Connect; // «Не удалось подключиться к серверу: {0}» — сообщение сервера без точки

        Poll(vm);

        var tail = Loc.T("server.hostedRetry", "", 10);
        Assert.EndsWith("refused." + tail, vm.StatusText);
        Assert.StartsWith(Loc.T("hosted.errConnect", "").TrimEnd(), vm.StatusText);
    });

    [Fact]
    public void CopyLines_CopiesTheSelectedLinesInTheirOrder() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        var vm = Vm();
        Poll(vm);
        var old = Ui.Platform;
        var clipboard = new ClipboardPlatform();
        Ui.Platform = clipboard;
        try
        {
            vm.CopyLinesCommand.Execute(new List<ConsoleLineViewModel> { vm.Lines[3], vm.Lines[1] });
            Assert.Equal(HostedLogSamples.Running[1] + Environment.NewLine + HostedLogSamples.Running[3], Assert.Single(clipboard.Copied));

            vm.CopyLinesCommand.Execute(new List<ConsoleLineViewModel>()); // ничего не выбрано — буфер не трогаем
            Assert.Single(clipboard.Copied);
        }
        finally
        {
            Ui.Platform = old;
        }
    });

    [Fact]
    public void Subtitle_LeavesTheGameVersionOut_UntilItIsKnown() => Sta.Run(() =>
    {
        var (main, hosted, _) = Open();
        // в шапке только хост и порт: логин FTP попадал бы на каждый скриншот
        var where = $"{_host.Settings.Host}:{_host.Settings.Port}";
        Assert.Equal(Loc.T("server.subtitleHosted", hosted.Name, where, "1.22.7"), main.Server.HeaderSubtitle);
        Assert.DoesNotContain(_host.Settings.User + "@", main.Server.HeaderSubtitle);

        hosted.HostedGameVersion = null;     // чтение модов версию ещё не нашло
        _host.Settings.FoundGameVersion = null; // и с прошлых раз она не запомнена
        Assert.Equal(Loc.T("server.subtitleHostedNoVersion", hosted.Name, where), main.Server.HeaderSubtitle);

        _host.Settings.GameVersion = "1.22.6"; // указана вручную
        Assert.Equal(Loc.T("server.subtitleHosted", hosted.Name, where, "1.22.6"), main.Server.HeaderSubtitle);
    });

    // ---------- прошлые запуски

    [Fact]
    public void PastRun_IsReadOnDemand_ShownAsIs_AndTheCurrentOneComesBackWithWhatGrewMeanwhile() => Sta.Run(() =>
    {
        const string Archived = "/Logs/Archive/2026-10-17_13_25_13/server-main.log";
        _host.Server.Put(Archived, HostedLogSamples.Bytes(HostedLogSamples.Archived));
        var local = PutLog(Main, HostedLogSamples.Running);
        var read = new List<string>();
        _host.Server.OnBefore = (op, path) => { if (op is "read" or "download") read.Add(path); };
        var vm = Vm();
        Poll(vm);

        Assert.Equal([null, "2026-10-17_13_25_13"], vm.Runs.Select(r => r.Run?.Name));
        Assert.Equal(Loc.T("server.hostedRunCurrent"), vm.Runs[0].Text);
        Assert.Equal(Dates.Relative(Local(13, 25, 13)), vm.Runs[1].Text); // время сервера → время этой машины
        Assert.Same(vm.Runs[0], vm.SelectedRun);
        Assert.DoesNotContain(Archived, read); // прошлый запуск не читается, пока его не выбрали

        vm.SelectedRun = vm.Runs[1];
        Sta.WaitFor(() => vm.RunNote == Loc.T("server.hostedRunStopped"));

        Assert.True(vm.IsPast);
        Assert.Equal(HostedLogSamples.Archived, Texts(vm));
        Assert.Contains(Archived, read);

        Append(local, StoppedTail);     // текущий тем временем дописался — прошлый на экране не меняется
        Poll(vm);
        Assert.Equal(HostedLogSamples.Archived, Texts(vm));
        Assert.Equal(HostedServerState.Stopped, vm.State); // а плитки — про текущий

        vm.SelectedRun = vm.Runs[0];
        Assert.False(vm.IsPast);
        Assert.Equal("", vm.RunNote);
        Assert.Equal(HostedLogSamples.Stopped, Texts(vm));
    });

    [Fact]
    public void PastRun_KilledWithoutAStop_IsSaidSo() => Sta.Run(() =>
    {
        _host.Server.Put("/Logs/Archive/2026-10-17_13_25_13/server-main.log", HostedLogSamples.Bytes(HostedLogSamples.Running));
        PutLog(Main, NextDay(HostedLogSamples.Starting));
        var vm = Vm();
        Poll(vm);

        vm.SelectedRun = vm.Runs[1];
        Sta.WaitFor(() => vm.RunNote != Loc.T("server.hostedRunReading"));

        Assert.Equal(Loc.T("server.hostedRunCut"), vm.RunNote);
    });

    [Fact]
    public void PastRuns_MissedBecauseOfTheNetwork_AreReadWithTheNextPoll() => Sta.Run(() =>
    {
        _host.Server.Put("/Logs/Archive/2026-10-17_13_25_13/server-main.log", HostedLogSamples.Bytes(HostedLogSamples.Archived));
        PutLog(Main, HostedLogSamples.Running);
        _host.Server.FailKind = RemoteFailure.Timeout;
        _host.Server.FailOn = (op, path) => op == "list" && path == "/Logs/Archive";
        var vm = Vm();

        Poll(vm);
        Assert.Equal(HostedServerState.Running, vm.State); // консоль и плитки — как обычно
        Assert.False(vm.HasRuns);

        _host.Server.FailOn = null;
        Poll(vm);
        Assert.Equal([null, "2026-10-17_13_25_13"], vm.Runs.Select(r => r.Run?.Name));
    });

    [Fact]
    public void PastRun_HugeLog_ShowsOnlyItsEnd_AsManyLinesAsTheConsoleKeeps() => Sta.Run(() =>
    {
        var lines = Enumerable.Range(0, HostedServerViewModel.MaxLines + 500)
            .Select(i => $"17.10.2026 13:25:{i % 60:00} [Notification] line {i}").ToArray();
        _host.Server.Put("/Logs/Archive/2026-10-17_13_25_13/server-main.log", HostedLogSamples.Bytes(lines));
        PutLog(Main, HostedLogSamples.Running);
        var vm = Vm();
        Poll(vm);

        vm.SelectedRun = vm.Runs[1];
        Sta.WaitFor(() => vm.RunNote != Loc.T("server.hostedRunReading"));

        Assert.Equal(HostedServerViewModel.MaxLines, vm.Lines.Count);
        Assert.Equal(lines[^1], vm.Lines[^1].Text);
        Assert.EndsWith(Loc.T("server.hostedRunTruncated"), vm.RunNote);
    });

    // ---------- вылеты

    [Fact]
    public void Crash_ShowsTheServerCrashWindowOnce() => Sta.Run(() =>
    {
        PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines]);
        var shown = new List<(GameProfile Profile, ServerCrashInfo Crash)>();
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.ShowServerCrash), a =>
        {
            shown.Add(((GameProfile)a[1]!, (ServerCrashInfo)a[2]!));
            return null;
        }));
        var vm = Vm(crashesSince: DateTime.MinValue);

        Poll(vm);

        Assert.Equal(HostedServerState.Crashed, vm.State);
        Assert.Equal(Loc.T("server.hostedStateCrashed"), vm.StateText);
        Assert.Equal(RowTone.Danger, vm.StateTone);
        var (profile, crash) = Assert.Single(shown);
        Assert.Same(_host.Profile, profile);
        Assert.Equal(32, crash.Id.Length);

        Poll(vm);
        Poll(vm);
        Assert.Single(shown); // тот же вылет второй раз не показываем
    });

    [Fact]
    public void Crash_BeforeTheWindowOpened_IsNotShown() => Sta.Run(() =>
    {
        PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines]);
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of(); // окно вылета уронило бы тест
        var vm = Vm(crashesSince: new DateTime(2100, 1, 1));

        Poll(vm);
        Poll(vm);

        Assert.Equal(HostedServerState.Crashed, vm.State);
    });

    [Fact]
    public void Crash_OnceFound_IsNotSearchedForAgainOnEveryPoll() => Sta.Run(() =>
    {
        PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines]);
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.ShowServerCrash), _ => null));
        var vm = Vm(crashesSince: DateTime.MinValue);
        Poll(vm);
        var lists = _host.Server.Calls["list"];

        for (var i = 0; i < 5; i++) Poll(vm);

        Assert.Equal(HostedServerState.Crashed, vm.State);
        Assert.Equal(lists + 5, _host.Server.Calls["list"]); // один листинг на опрос, как у работающего сервера
    });

    /// <summary>Вылет прошлого запуска в 13:40 (по часам сервера) и текущий запуск на следующий день.</summary>
    private void CrashedThenStartedAgain()
    {
        PutLog("/Logs/Archive/2026-10-17_13_27_08/server-main.log", [.. HostedLogSamples.Running, .. FatalLines]);
        PutLog(Main, NextDay(HostedLogSamples.Running));
    }

    [Fact]
    public void Crash_WhileTheSectionWasClosed_AndTheServerStartedAgain_IsShownOnTheFirstPoll() => Sta.Run(() =>
    {
        // окно открыли в 13:00, сервер упал в 13:40, панель хостера запустила его снова, раздел «Сервер» открыли позже
        CrashedThenStartedAgain();
        var shown = new List<ServerCrashInfo>();
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of((nameof(IAppWindows.ShowServerCrash), a =>
        {
            shown.Add((ServerCrashInfo)a[2]!);
            return null;
        }));
        var vm = Vm(crashesSince: Local(13, 0, 0));

        Poll(vm);

        Assert.Equal(HostedServerState.Running, vm.State);
        Assert.Single(shown);
        Poll(vm);
        Assert.Single(shown);
    });

    [Fact]
    public void Crash_OfAPastRun_BeforeTheWindowOpened_IsNotShownOnTheFirstPoll() => Sta.Run(() =>
    {
        CrashedThenStartedAgain();
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of(); // окно вылета уронило бы тест

        Poll(Vm(crashesSince: Local(13, 45, 0)));  // вылет раньше окна — старый
    });

    [Fact]
    public void FirstPoll_DoesNotReadTheArchive_WhenTheCurrentRunStartedBeforeTheWindowOpened() => Sta.Run(() =>
    {
        CrashedThenStartedAgain();
        var read = new List<string>();
        _host.Server.OnBefore = (op, path) => { if (op is "read" or "download") read.Add(path); };
        AppWindows.Current = NewProfileViewModelTests.Fake<IAppWindows>.Of();

        Poll(Vm(crashesSince: new DateTime(2100, 1, 1)));

        // прошлый запуск кончился до окна — его вылет всё равно старый
        Assert.DoesNotContain(read, path => path.StartsWith("/Logs/Archive/", StringComparison.Ordinal));
    });

    // ---------- вопрос «останови сервер в панели хостера»

    private JArray? Disabled() => JObject.Parse(_host.Server.Text("/serverconfig.json"))["WorldConfig"]?["DisabledMods"] as JArray;

    /// <summary>«Мои моды» хостового профиля с модом alpha; ждёт, пока моды прочитаны.</summary>
    private ModsViewModel Mods()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        return Open().Main.Mods;
    }

    /// <summary>Главное окно с хостовым профилем и модом alpha на сервере; моды прочитаны.</summary>
    private MainViewModel MainWithAlpha()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        return Open().Main;
    }

    private static ModRowViewModel Alpha(ModsViewModel mods) => mods.Rows.Single(r => r.ModId == "alpha");

    [Fact]
    public void Toggle_ServerStoppedByTheLog_DoesNotAsk() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Stopped);
        var mods = Mods();
        using var dialogs = new ScriptedDialogs(); // любой вопрос уронил бы тест

        Sta.Wait(mods.ToggleEnabledCommand.ExecuteAsync(Alpha(mods)));

        Assert.Empty(dialogs.Asked);
        Assert.Contains("alpha", Disabled()!.Select(t => (string?)t));
    });

    [Theory]
    [InlineData("running")]
    [InlineData("starting")]
    [InlineData("stopping")]
    [InlineData("crashed")]
    [InlineData("nolog")]
    [InlineData("nolink")]
    public void Toggle_ServerNotSurelyStopped_AsksAsBefore(string state) => Sta.Run(() =>
    {
        switch (state)
        {
            case "running": PutLog(Main, HostedLogSamples.Running); break;
            case "starting": PutLog(Main, HostedLogSamples.Starting); break;
            case "stopping":
                // остановку начали только что: лог ещё растёт
                File.SetLastWriteTimeUtc(_host.Server.Put(Main, HostedLogSamples.Bytes(HostedLogSamples.Stopping)), DateTime.UtcNow);
                break;
            case "crashed": PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines]); break;
            case "nolog": break;
            case "nolink": PutLog(Main, HostedLogSamples.Stopped); break;
        }
        var mods = Mods();
        if (state == "nolink") _host.Server.FailOn = (op, path) => path.StartsWith("/Logs", StringComparison.Ordinal); // лог не прочитать
        using var dialogs = new ScriptedDialogs(Answer.No);

        Sta.Wait(mods.ToggleEnabledCommand.ExecuteAsync(Alpha(mods)));

        Assert.Equal([Loc.T("hosted.stopAsk", _host.Profile.Name)], dialogs.Asked);
        Assert.True(Alpha(mods).IsEnabled); // ответили «нет» — конфиг не тронут
    });

    [Fact]
    public void Delete_ServerStoppedByTheLog_AsksOnlyAboutTheDeletion() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Stopped);
        var mods = Mods();
        using var dialogs = new ScriptedDialogs(Answer.Yes);

        Sta.Wait(mods.DeleteCommand.ExecuteAsync(Alpha(mods)));

        Assert.EndsWith(Loc.T("hosted.deleteNote"), Assert.Single(dialogs.Asked));
        Assert.False(_host.Server.Has("/Mods/alpha_1.0.0.zip"));
    });

    [Fact]
    public void Queue_ServerStoppedByTheLog_InstallsWithoutTheQuestion() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Stopped);
        var mods = Mods();
        var zip = _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0");
        using var dialogs = new ScriptedDialogs();

        mods.Enqueue([new UpdateQueueItem(_host.Target, "alpha", "alpha", "1.0.0", "1.1.0", null, zip)], ModHistorySource.Update);
        Sta.WaitFor(() => _host.Server.Has("/Mods/alpha_1.1.0.zip") && !mods.IsQueueRunning);

        Assert.Empty(dialogs.Asked);
    });

    [Fact]
    public void Queue_ServerRunning_AsksOnce() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Running);
        var mods = Mods();
        var zip = _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0");
        using var dialogs = new ScriptedDialogs(Answer.Yes);

        mods.Enqueue([new UpdateQueueItem(_host.Target, "alpha", "alpha", "1.0.0", "1.1.0", null, zip)], ModHistorySource.Update);
        Sta.WaitFor(() => _host.Server.Has("/Mods/alpha_1.1.0.zip") && !mods.IsQueueRunning);

        Assert.Equal([Loc.T("hosted.stopAsk", _host.Profile.Name)], dialogs.Asked);
    });

    [Fact]
    public void StopCheck_DoesNotHoldTheWindowThread_WhileTheServerAnswers() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Stopped);
        // настоящий FTP отвечает не сразу: подделка тоже — каждый листинг через 300 мс
        using var slow = RemoteFilesFactory.RegisterHost(_host.Host, _ => new SlowFiles(_host.Server, TimeSpan.FromMilliseconds(300)));
        var service = new ModInstallService((MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel)),
            new ModUpdater(new Core.ModDb.ModDbClient()), _ => { }, () => null);
        using var dialogs = new ScriptedDialogs();
        var posted = false;

        var check = service.ConfirmIfRunningAsync(_host.Target);
        Ui.Post(() => posted = true);

        Assert.False(check.IsCompleted);     // ждёт сервер…
        Sta.WaitFor(() => posted);           // …а очередь окна тем временем крутится
        Sta.Wait(check);
        Assert.True(check.Result);           // по логу остановлен — без вопроса
        Assert.Empty(dialogs.Asked);
    });

    [Fact]
    public void StopReminder_SkipsTheQuestionOnlyForStopped_ForAShortWhile_AndDoesNotCheckWhenItWouldNotAsk() => Sta.Run(() =>
    {
        var clock = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        var state = HostedServerState.Stopped;
        var checks = 0;
        var reminder = new HostedStopReminder(() => clock, (_, _) =>
        {
            checks++;
            return state == (HostedServerState)(-1) ? throw new RemoteFilesException(RemoteFailure.Timeout, "slow") : Task.FromResult(state);
        });
        var target = _host.Target;
        void Check() => Sta.Wait(reminder.CheckAsync(target));

        Check();
        Assert.True(reminder.IsKnownStopped(target));
        clock += HostedStopReminder.StoppedMemory + TimeSpan.FromSeconds(1);
        Assert.False(reminder.IsKnownStopped(target)); // проверка — только для ближайших вопросов

        foreach (var s in new[] { HostedServerState.Running, HostedServerState.Starting, HostedServerState.Stopping,
                     HostedServerState.Crashed, HostedServerState.Unknown, (HostedServerState)(-1) })
        {
            state = HostedServerState.Stopped;
            Check();
            state = s;
            Check();
            Assert.False(reminder.IsKnownStopped(target), s.ToString()); // не «остановлен» — и прошлое «остановлен» забыто
        }

        using (new ScriptedDialogs(Answer.Yes)) Assert.True(reminder.Confirm(target)); // «Да» помнится десять минут
        checks = 0;
        Check();
        Assert.Equal(0, checks); // спрашивать не будем — и в сеть незачем

        clock += HostedStopReminder.Memory;
        _host.Settings.SkipStopReminder = true;
        Sta.Wait(reminder.CheckAsync(_host.Target));
        Assert.Equal(0, checks); // вопрос выключен в профиле
    });

    [Fact]
    public void CrashWindow_DisablingTheCulprit_AsksToStopTheServer_AndShares_TheAnswerWithMyMods() => Sta.Run(() =>
    {
        PutLog(Main, [.. HostedLogSamples.Running, .. FatalLines]); // вылетел — панель хостера могла уже запустить его снова
        _host.PutMod("/Mods/beta_1.0.0.zip", "beta", "1.0.0");
        var main = MainWithAlpha();
        var mods = main.Mods;
        var disable = new ModDisabler(main).For(_host.Profile, "/Mods/alpha_1.0.0.zip", "alpha")!;
        var before = _host.Server.Text("/serverconfig.json");

        using (var dialogs = new ScriptedDialogs(Answer.No))
        {
            var refused = disable();
            Sta.Wait(refused);
            Assert.Equal([Loc.T("hosted.stopAsk", _host.Profile.Name)], dialogs.Asked);
            Assert.True(refused.Result.Failed); // кнопку можно нажать снова
            Assert.Equal(Loc.T("crash.disableNotStopped"), refused.Result.Message);
            Assert.Equal(before, _host.Server.Text("/serverconfig.json"));
        }

        using (var dialogs = new ScriptedDialogs(Answer.Yes))
        {
            var done = disable();
            Sta.Wait(done);
            Assert.False(done.Result.Failed);
            Assert.Contains("alpha", Disabled()!.Select(t => (string?)t));
        }

        // «Да» из окна вылета помнится и для «Моих модов»: выключить ещё один мод — без вопроса
        Sta.WaitFor(() => !mods.HostedSource.IsLoading && mods.Rows.Any(r => r.ModId == "beta"));
        using (var dialogs = new ScriptedDialogs())
        {
            Sta.Wait(mods.ToggleEnabledCommand.ExecuteAsync(mods.Rows.Single(r => r.ModId == "beta")));
            Assert.Empty(dialogs.Asked);
            Assert.Contains("beta", Disabled()!.Select(t => (string?)t));
        }
    });

    [Fact]
    public void CrashWindow_ServerStoppedByTheLog_DisablesWithoutTheQuestion() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Stopped);
        var main = MainWithAlpha();
        using var dialogs = new ScriptedDialogs(); // любой вопрос уронил бы тест

        var done = new ModDisabler(main).For(_host.Profile, "/Mods/alpha_1.0.0.zip", "alpha")!();
        Sta.Wait(done);

        Assert.False(done.Result.Failed);
        Assert.Contains("alpha", Disabled()!.Select(t => (string?)t));
    });

    [Fact]
    public void Queue_WhileTheLogIsChecked_TheTabIsBusy_AndSaysWhatItWaitsFor() => Sta.Run(() =>
    {
        PutLog(Main, HostedLogSamples.Stopped);
        var mods = Mods();
        // ответ сервера — через 300 мс, как по сети; соединение — новое, уже через подделку с задержкой
        using var slow = RemoteFilesFactory.RegisterHost(_host.Host, _ => new SlowFiles(_host.Server, TimeSpan.FromMilliseconds(300)));
        HostedSessions.Forget(_host.Profile.Id);
        var zip = _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0");
        var dropped = _host.Zip("gamma_1.0.0.zip", "gamma", "1.0.0");
        using var dialogs = new ScriptedDialogs();

        mods.Enqueue([new UpdateQueueItem(_host.Target, "alpha", "alpha", "1.0.0", "1.1.0", null, zip)], ModHistorySource.Update);

        Assert.True(mods.IsBusy);            // щелчок не потерялся: вкладка занята и говорит, чего ждёт
        Assert.False(mods.CanQueue);
        Assert.Equal(Loc.T("hosted.stopChecking"), mods.StatusText);
        Sta.Wait(mods.AddFilesAsync([dropped])); // zip, перетащенный тем временем, своей установки не начнёт
        Sta.WaitFor(() => _host.Server.Has("/Mods/alpha_1.1.0.zip") && !mods.IsQueueRunning);

        Assert.Empty(dialogs.Asked);         // по логу остановлен
        Assert.False(_host.Server.Has("/Mods/gamma_1.0.0.zip"));
        Assert.False(mods.IsBusy);
    });

    [Fact]
    public void StopReminder_AnUnexpectedFailureOfTheCheck_MeansAsking_AndDoesNotEscape() => Sta.Run(() =>
    {
        var reminder = new HostedStopReminder(check: (_, _) => throw new ArgumentException("bug"));

        Sta.Wait(reminder.CheckAsync(_host.Target));

        Assert.False(reminder.IsKnownStopped(_host.Target));
    });

    /// <summary>Платформа окна, которая запоминает, что положили в буфер обмена.</summary>
    private sealed class ClipboardPlatform : TestUiPlatform
    {
        public List<string> Copied { get; } = [];
        public override void SetClipboard(string text) => Copied.Add(text);
    }

    /// <summary>Подделка FTP, перед каждым листингом которой тест решает, когда ей ответить.</summary>
    private sealed class HookFiles(IRemoteFiles inner, Func<Task> beforeList) : IRemoteFiles
    {
        public RemoteFeatures Features => inner.Features;
        public bool IsConnected => inner.IsConnected;
        public Task ConnectAsync(CancellationToken ct) => inner.ConnectAsync(ct);

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string dir, CancellationToken ct)
        {
            await beforeList();
            return await inner.ListAsync(dir, ct);
        }

        public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct) => inner.ReadRangeAsync(path, offset, count, ct);
        public Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct) => inner.DownloadAsync(path, destination, progress, ct);
        public Task UploadAsync(Stream source, string path, IProgress<long>? progress, CancellationToken ct) => inner.UploadAsync(source, path, progress, ct);
        public Task RenameAsync(string from, string to, CancellationToken ct) => inner.RenameAsync(from, to, ct);
        public Task DeleteFileAsync(string path, CancellationToken ct) => inner.DeleteFileAsync(path, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Подделка FTP, которая отвечает на листинг с задержкой, как настоящий сервер по сети.</summary>
    private sealed class SlowFiles(IRemoteFiles inner, TimeSpan delay) : IRemoteFiles
    {
        public RemoteFeatures Features => inner.Features;
        public bool IsConnected => inner.IsConnected;
        public Task ConnectAsync(CancellationToken ct) => inner.ConnectAsync(ct);

        public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string dir, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return await inner.ListAsync(dir, ct);
        }

        public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);
        public Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct) => inner.ReadRangeAsync(path, offset, count, ct);
        public Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct) => inner.DownloadAsync(path, destination, progress, ct);
        public Task UploadAsync(Stream source, string path, IProgress<long>? progress, CancellationToken ct) => inner.UploadAsync(source, path, progress, ct);
        public Task RenameAsync(string from, string to, CancellationToken ct) => inner.RenameAsync(from, to, ct);
        public Task DeleteFileAsync(string path, CancellationToken ct) => inner.DeleteFileAsync(path, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
