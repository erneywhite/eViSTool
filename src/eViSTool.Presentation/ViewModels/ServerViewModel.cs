using eViSTool.Presentation;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Виды раздела «Сервер».</summary>
public enum ServerTab { Console, Players, Stats, Journal, Announcements, Config, Schedule, Remote, Notify }

/// <summary>
/// Вкладка «Сервер». Сервером владеет агент (отдельный процесс) — окно только показывает и командует,
/// поэтому его можно закрыть в любой момент. Связь с агентом и опрос его статуса — у наблюдателя
/// (<see cref="ServerMonitor"/>), консоль — своя модель (<see cref="ServerConsoleViewModel"/>, её лицо для вида —
/// в ServerViewModel.Console.cs); здесь — что показывать по статусу, кнопки и раздача статуса вкладкам.
/// </summary>
public sealed partial class ServerViewModel : ObservableObject, IServerMonitorHost
{
    private readonly MainViewModel _main;
    private readonly ServerMonitor _monitor;
    private readonly ServerConsoleViewModel _console;

    /// <summary>Связь с агентом удалённого сервера (null — профиль свой или связи нет): через неё вкладка модов работает с его модами.</summary>
    public AgentClient? RemoteClient => IsRemoteProfile ? Client : null;

    /// <summary>Связь с агентом сервера профиля (своего или удалённого); null — агента нет.</summary>
    internal AgentClient? Client => _monitor.Client;

    /// <summary>Сервер запущен или запускается (по последнему статусу агента).</summary>
    public bool IsServerUp => AgentRunning && State != ServerState.Stopped;

    /// <summary>Редактор serverconfig.json — второй вид раздела, рядом с консолью.</summary>
    public ServerConfigViewModel Config { get; } = new();

    /// <summary>Расписание: резервные копии (и рестарты) — третий вид раздела.</summary>
    public ServerScheduleViewModel Schedule { get; }

    /// <summary>«Предлагать пре-релизы» — для обновления модов сервера при перезапуске.</summary>
    internal bool AllowUnstable => _main.AllowUnstable;
    public ServerRemoteViewModel Remote { get; } = new();

    /// <summary>Игроки: роли, белый список, баны — второй вид раздела.</summary>
    public ServerPlayersViewModel Players { get; }

    /// <summary>Статистика: онлайн, ресурсы, наигранное время (собирает агент, если включено).</summary>
    public ServerStatsViewModel Stats { get; }
    public ServerJournalViewModel Journal { get; }

    /// <summary>Объявления в чат: по расписанию и «сказать сейчас».</summary>
    public ServerAnnouncementsViewModel Announcements { get; }

    /// <summary>Оповещения: какие события в какие каналы — последний вид раздела.</summary>
    public ServerNotifyViewModel Notify { get; }

    /// <summary>Какой вид раздела открыт. Консоль продолжает получать строки при любом.</summary>
    [ObservableProperty] private ServerTab _tab;

    public bool IsConsoleTab => Tab == ServerTab.Console;
    public bool IsPlayersTab => Tab == ServerTab.Players;
    public bool IsAnnouncementsTab => Tab == ServerTab.Announcements;
    public bool IsStatsTab => Tab == ServerTab.Stats;
    public bool IsJournalTab => Tab == ServerTab.Journal;
    public bool IsNotifyTab => Tab == ServerTab.Notify;
    public bool IsConfigTab => Tab == ServerTab.Config;
    public bool IsScheduleTab => Tab == ServerTab.Schedule;
    public bool IsRemoteTab => Tab == ServerTab.Remote;

    // состояние сервера профиля выяснено (первый проход слежения после смены профиля завершён)
    private bool _stateKnown;

    [ObservableProperty] private bool _isServerProfile;

    /// <summary>Сервер на другом компьютере: всё через агента по сети, своих папок у профиля нет.</summary>
    [ObservableProperty] private bool _isRemoteProfile;
    private string? _remoteGameVersion;
    private string? _remoteOs; // система машины с сервером — в подзаголовке
    [ObservableProperty] private ServerState _state = ServerState.Stopped;
    [ObservableProperty] private bool _agentRunning;
    [ObservableProperty] private string _stateText = "";

    // плитки над консолью
    [ObservableProperty] private RowTone _stateTone = RowTone.Muted;
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private string _pidText = "—";
    /// <summary>Под плиткой состояния: перезапуск через N с / код выхода.</summary>
    [ObservableProperty] private string _stateNote = "";

    /// <summary>На машине с сервером eViSTool новее этого окна: обновлять надо здесь, а не там.</summary>
    private static bool NewerAgent(AgentStatus s) =>
        Core.Versioning.ModVersion.TryParse(s.AgentVersion, out var agent)
        && Core.Versioning.ModVersion.TryParse(AgentProtocol.AppVersion, out var app) && agent.CompareTo(app) > 0;

    /// <summary>Подзаголовок: профиль и версия игры; путь к данным — в подсказке.</summary>
    public string HeaderSubtitle => _main.ActiveProfile is not { } p ? ""
        : p.Model.IsRemote ? Loc.T("server.subtitleRemote", p.Name,
            (RemoteSecret.Unprotect(p.Model.RemoteCode) is { } c ? $"{c.Host}:{c.Port}" : "?") + (_remoteOs is { Length: > 0 } os ? " · " + os : ""),
            _remoteGameVersion ?? "?")
        : Loc.T("server.subtitle", p.Name, p.GameVersionText);
    public string DataDir => _main.ActiveProfile?.Model.DataDir ?? "";

    [RelayCommand]
    private void OpenServerMods() => _main.SelectedTab = AppTab.Mods;

    [RelayCommand]
    private void OpenSettings() => _main.SelectedTab = AppTab.Settings;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorText = "";

    /// <summary>Агент старее программы: удалённый — «обнови там», свой при работающем сервере — «обновится после остановки».</summary>
    [ObservableProperty] private string _agentNote = "";

    /// <summary>Удалённый агент старее окна и не обновляется прямо сейчас — можно обновить его отсюда.</summary>
    [ObservableProperty] private bool _canUpdateRemoteAgent;
    private bool _remoteServerUp;

    /// <summary>Сервер из папки игры профиля, запущенный не нашим агентом (например, ViSST).</summary>
    [ObservableProperty] private int? _foreignPid;

    public bool HasForeign => ForeignPid is not null;
    public string ForeignText => ForeignPid is { } pid ? Loc.T("server.foreign", pid) : "";
    public bool CanStart => IsServerProfile && !IsBusy && ForeignPid is null && State == ServerState.Stopped && (!IsRemoteProfile || AgentRunning)
                            && Schedule?.IsRestoring != true;

    internal void NotifyCanStart() => OnPropertyChanged(nameof(CanStart));
    public bool CanStop => AgentRunning && !IsBusy && State is ServerState.Running or ServerState.Starting;
    public bool CanCommand => AgentRunning && State is ServerState.Running or ServerState.Starting;

    public ServerViewModel(MainViewModel main)
    {
        _main = main;
        _monitor = new ServerMonitor(this);
        _console = new ServerConsoleViewModel(() => Client);
        // свойства консоли вид берёт у раздела под теми же именами — и уведомления о них приходят от раздела. Фильтра по
        // имени нет: у каждого свойства консоли есть двойник здесь, это держит тест ServerConsoleViewModelTests
        _console.PropertyChanging += (_, e) => OnPropertyChanging(e);
        _console.PropertyChanged += (_, e) => OnPropertyChanged(e);
        Schedule = new ServerScheduleViewModel(this);
        Players = new ServerPlayersViewModel(this);
        _console.LinesReceived += Players.OnConsoleLines; // ответ сервера на команду вкладки «Игроки»
        Announcements = new ServerAnnouncementsViewModel(this);
        Stats = new ServerStatsViewModel(this);
        Journal = new ServerJournalViewModel(this);
        Notify = new ServerNotifyViewModel(this);
    }

    partial void OnTabChanged(ServerTab value)
    {
        OnPropertyChanged(nameof(IsConsoleTab));
        OnPropertyChanged(nameof(IsPlayersTab));
        OnPropertyChanged(nameof(IsAnnouncementsTab));
        OnPropertyChanged(nameof(IsStatsTab));
        OnPropertyChanged(nameof(IsJournalTab));
        OnPropertyChanged(nameof(IsNotifyTab));
        OnPropertyChanged(nameof(IsConfigTab));
        OnPropertyChanged(nameof(IsScheduleTab));
        OnPropertyChanged(nameof(IsRemoteTab));
        Config.SetActive(value == ServerTab.Config);
        Schedule.SetActive(value == ServerTab.Schedule);
        Players.SetActive(value == ServerTab.Players);
        Announcements.SetActive(value == ServerTab.Announcements);
        Stats.SetActive(value == ServerTab.Stats);
        Journal.SetActive(value == ServerTab.Journal);
        Notify.SetActive(value == ServerTab.Notify);
        if (value != ServerTab.Remote) Remote.Hide(); // ушли с вкладки — код подключения снова закрыт
    }

    /// <summary>Попросить агента сделать копию мира на работающем сервере.</summary>
    public Task BackupAsync() =>
        Client is { } client ? client.BackupAsync() : throw new InvalidOperationException(Loc.T("sched.notRunning"));

    partial void OnStateChanged(ServerState value) => Refresh();
    partial void OnAgentRunningChanged(bool value) => Refresh();
    partial void OnIsBusyChanged(bool value) => Refresh();
    partial void OnForeignPidChanged(int? value)
    {
        OnPropertyChanged(nameof(HasForeign));
        OnPropertyChanged(nameof(ForeignText));
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanCommand));
        (StateText, StateTone) = !IsServerProfile ? ("", RowTone.Muted)
            : IsRemoteProfile && !AgentRunning ? (Loc.T("server.stateOffline"), RowTone.Warn)
            : (AgentRunning ? State : ServerState.Stopped) switch
        {
            ServerState.Starting => (Loc.T("server.stateStarting"), RowTone.Update),
            ServerState.Running => (Loc.T("server.stateRunning"), RowTone.Good),
            ServerState.Stopping => (Loc.T("server.stateStopping"), RowTone.Warn),
            _ => (Loc.T("server.stateStopped"), RowTone.Muted),
        };
        PushServerState();
        Players?.OnServerStateChanged();
        Announcements?.OnServerStateChanged();
    }

    /// <summary>
    /// Редактору конфига: работает ли сервер. «Работает» — всё, кроме полностью остановленного, и чужой сервер тоже.
    /// Пока состояние не выяснено, молчим: сброс полей при смене профиля — ещё не «сервер остановлен».
    /// </summary>
    private void PushServerState()
    {
        if (_stateKnown) Config.SetServerRunning((AgentRunning && State != ServerState.Stopped) || HasForeign);
    }

    /// <summary>Сменился язык: тексты, собранные в коде, — заново.</summary>
    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(ForeignText));
        Refresh();
        Config.OnLanguageChanged();
        Schedule.OnLanguageChanged();
        Remote.OnLanguageChanged();
        Players.OnLanguageChanged();
        Notify.OnLanguageChanged();
    }

    /// <summary>Сменился профиль: отключиться от старого агента, подключиться к агенту нового (если он работает).</summary>
    public void OnProfileSwitched()
    {
        // правки конфига старого профиля — сохранить, пока связь с его агентом ещё открыта
        var next = _main.ActiveProfile?.Model;
        var nextServer = next?.Kind == ProfileKind.Server;
        Config.BeforeSwitch(nextServer && next is { IsRemote: false } ? next.DataDir : null, nextServer && next!.IsRemote ? next : null);

        _stateKnown = false;
        _monitor.Stop();
        AgentRunning = false;
        State = ServerState.Stopped;
        ForeignPid = null;
        ErrorText = "";
        AgentNote = "";
        _agentRestartingUntil = DateTime.MinValue;
        ClearStats();
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(DataDir));
        _console.Reset();

        IsServerProfile = _main.ActiveProfile?.Kind == ProfileKind.Server;
        IsRemoteProfile = _main.ActiveProfile?.Model.IsRemote == true;
        _remoteGameVersion = null;
        _remoteOs = null;
        // удалённый доступ настраивают на компьютере с сервером — у удалённого профиля этой вкладки нет
        if (IsRemoteProfile && Tab == ServerTab.Remote) Tab = ServerTab.Console;
        var local = IsServerProfile && !IsRemoteProfile;
        Refresh();
        // сюда попадаем и при правке имени профиля: редактор сам разберётся, сменился ли файл
        Config.OnProfileSwitched(local ? _main.ActiveProfile?.Model.DataDir : null, _main.ActiveProfile?.Name ?? "",
            _main.ActiveProfile?.Model.GameDir, IsRemoteProfile ? _main.ActiveProfile?.Model : null, () => Client);
        Schedule.OnProfileSwitched(IsServerProfile ? _main.ActiveProfile?.Model : null, () => Client);
        Remote.OnProfileSwitched(local ? _main.ActiveProfile?.Model : null);
        Players.OnProfileSwitched(IsServerProfile ? _main.ActiveProfile?.Model : null);
        Announcements.OnProfileSwitched(IsServerProfile ? _main.ActiveProfile?.Model : null);
        Stats.OnProfileSwitched(IsServerProfile ? _main.ActiveProfile?.Model : null);
        Journal.OnProfileSwitched(IsServerProfile ? _main.ActiveProfile?.Model : null);
        Notify.OnProfileSwitched(IsServerProfile ? _main.ActiveProfile?.Model : null);
        if (!IsServerProfile) return;

        _monitor.Start();
    }

    // ---------- что сообщает наблюдатель (ServerMonitor) ----------

    GameProfile? IServerMonitorHost.Profile => _main.ActiveProfile?.Model;

    void IServerMonitorHost.OnAttached(AgentClient client, CancellationToken session)
    {
        AgentRunning = true;
        _ = _console.PumpAsync(client, session);
    }

    void IServerMonitorHost.OnDetached()
    {
        AgentRunning = false;
        State = ServerState.Stopped;
        _serverPid = null;
        ClearStats();
    }

    void IServerMonitorHost.OnStatus(AgentStatus status) => Apply(status);

    void IServerMonitorHost.OnLocalChecked(IReadOnlyList<int> serverPids, bool ourServerUp)
    {
        // чужой сервер: из папки игры профиля, но не наш
        ForeignPid = serverPids.Where(pid => !(ourServerUp && pid == _serverPid)).Select(pid => (int?)pid).FirstOrDefault();

        _stateKnown = true;
        PushServerState();
        Players.PollLocal(); // файлы игроков поменялись (сервер записал списки, правка в другом окне)
        Announcements.PollLocal();
        Notify.PollLocal();
    }

    void IServerMonitorHost.OnRemoteAnswered()
    {
        if (ErrorText.Length > 0 && !IsBusy) ErrorText = "";
    }

    void IServerMonitorHost.OnRemoteChecked(Exception? error)
    {
        if (error is not null)
        {
            if (DateTime.UtcNow < _agentRestartingUntil)
            {
                // агент обновляется и перезапускается — связь и должна пропасть: не пугаем ошибкой, ждём его
                AgentNote = Loc.T("selfupd.stateRestart");
                ErrorText = "";
            }
            else ErrorText = RemoteSecret.Describe(error);
        }
        _stateKnown = true;
        Refresh();
    }

    void IServerMonitorHost.OnRemoteCodeUnreadable()
    {
        ErrorText = Loc.T("remote.errDecrypt");
        _stateKnown = true;
    }

    private int? _serverPid;

    private void Apply(AgentStatus s)
    {
        _console.CheckCommands(s);
        AgentRunning = true;
        State = s.State;
        _serverPid = s.ServerPid;
        if (IsRemoteProfile)
        {
            Config.ShowRemoteStatus(s);
            _main.Mods.OnRemoteStatus(s);
        }
        // удалённый агент обновляется по нашей просьбе — ход обновления вместо надписи о старой версии
        var olderAgent = IsRemoteProfile && AgentProtocol.IsOutdated(s)
                         && Core.Versioning.ModVersion.TryParse(s.AgentVersion, out var agentVersion)
                         && Core.Versioning.ModVersion.TryParse(AgentProtocol.AppVersion, out var appVersion)
                         && agentVersion.CompareTo(appVersion) < 0;
        _remoteServerUp = s.State != ServerState.Stopped;
        // обновляется — скоро перезапустится; пока не вернётся (до трёх минут), пропажа связи — не ошибка. Любой этап:
        // скачивание с GitHub бывает таким быстрым, что опрос раз в полторы секунды «установку» и «перезапуск» не застаёт
        if (s.SelfUpdate is "download" or "stop" or "install" or "restart") _agentRestartingUntil = DateTime.UtcNow.AddMinutes(3);
        else if (s.SelfUpdate is null or "failed") _agentRestartingUntil = DateTime.MinValue;
        CanUpdateRemoteAgent = olderAgent && s.SelfUpdate is null or "failed";
        AgentNote = s.SelfUpdate switch
        {
            "download" => Loc.T("selfupd.stateDownload", AgentProtocol.AppVersion),
            "stop" => Loc.T("selfupd.stateStop"),
            "install" => Loc.T("selfupd.stateInstall"),
            "restart" => Loc.T("selfupd.stateRestart"),
            "failed" => Loc.T("selfupd.stateFailed", s.SelfUpdateError ?? "?"),
            _ => !AgentProtocol.IsOutdated(s) ? ""
                : IsRemoteProfile ? Loc.T(olderAgent ? "selfupd.outdated" : NewerAgent(s) ? "server.agentNewerRemote" : "server.agentOutdatedRemote",
                    s.AgentVersion, AgentProtocol.AppVersion)
                : Loc.T("server.agentOutdatedLocal", s.AgentVersion),
        };
        if (IsRemoteProfile && (s.GameVersion != _remoteGameVersion || s.Os != _remoteOs))
        {
            _remoteGameVersion = s.GameVersion;
            _remoteOs = s.Os;
            if (_main.ActiveProfile is { } active) active.RemoteGameVersion = s.GameVersion;
            OnPropertyChanged(nameof(HeaderSubtitle));
        }

        // агент перезапускался — нумерация строк началась заново
        _console.CheckSequence(s);

        UptimeText = s.StartedAt is { } started && s.State != ServerState.Stopped ? Dates.Uptime(DateTime.Now - started) : "—";
        MemoryText = s.MemoryMb is { } mb && s.State != ServerState.Stopped ? Loc.T("server.memoryMb", mb.ToString("N0")) : "—";
        PidText = s.ServerPid is { } pid && s.State != ServerState.Stopped ? pid.ToString() : "—";
        ShowPlayers(s.Players, running: s.State == ServerState.Running);
        Schedule.ShowStatus(s);
        Remote.ShowStatus(s);
        Players.ShowStatus(s);
        Announcements.ShowStatus(s);
        Stats.ShowStatus(s);
        Journal.ShowStatus(s);
        Notify.ShowStatus(s);
        StateNote = s.RestartScheduledAt is { } at ? Loc.T("server.restartIn", Math.Max(0, (int)(at - DateTime.Now).TotalSeconds))
            : s.State == ServerState.Stopped && s.LastExitCode is { } code ? Loc.T("server.lastExit", code)
            : s.NextRestartAt is { } planned ? Loc.T("server.restartPlanned", Dates.Clock(planned))
            : "";
    }

    private void ClearStats()
    {
        UptimeText = MemoryText = PidText = "—";
        StateNote = "";
        ShowPlayers([], running: false);
        Schedule?.ShowStatus(null);
        Remote?.ShowStatus(null);
        Players?.ShowStatus(null);
        Announcements?.ShowStatus(null);
        Notify?.ShowStatus(null);
    }

    // ---------- игроки на сервере ----------

    /// <summary>Плитка «Игроки»: число, имена одной строкой и подсказка «кто с какого времени».</summary>
    [ObservableProperty] private string _playersText = "—";
    [ObservableProperty] private string _playerNames = "";
    [ObservableProperty] private string? _playersTip;

    private void ShowPlayers(IReadOnlyList<OnlinePlayer> players, bool running)
    {
        PlayersText = running ? players.Count.ToString() : "—";
        PlayerNames = !running ? "" : players.Count == 0 ? Loc.T("server.playersNone") : string.Join(" · ", players.Select(p => p.Name));
        PlayersTip = players.Count == 0 ? null : string.Join(Environment.NewLine,
            players.Select(p => Loc.T("server.playerSince", p.Name, Dates.Clock(p.Since), Dates.Uptime(DateTime.Now - p.Since))));
    }

    /// <summary>До какого времени ждём удалённого агента, перезапускающегося после «Обновить на сервере».</summary>
    private DateTime _agentRestartingUntil = DateTime.MinValue;

    // ---------- кнопки ----------

    private async Task Do(Func<Task> action)
    {
        IsBusy = true;
        ErrorText = "";
        try { await action(); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or FileNotFoundException or TimeoutException or IOException)
        {
            ErrorText = ex.Message;
        }
        finally { IsBusy = false; }
    }

    // сервер читает конфиг с диска: несохранённые правки редактора сначала предлагаем сохранить
    [RelayCommand]
    private Task Start() => !Config.ConfirmBeforeServerStart() ? Task.CompletedTask : Do(async () =>
    {
        if (_main.ActiveProfile?.Model is not { } profile) return;
        if (profile.IsRemote)
        {
            // удалённый агент уже работает там — просто просим запустить сервер
            if (Client is null) throw new InvalidOperationException(Loc.T("server.stateOffline"));
            Apply(await Client.StartAsync());
            return;
        }
        var client = await AgentLauncher.EnsureRunningAsync(profile, startServer: true);
        if (!ReferenceEquals(client, Client))
        {
            if (Client is not null && Client.Endpoint.Pid == client.Endpoint.Pid) client.Dispose();
            else _monitor.Attach(client);
        }
        if (Client is not null) Apply(await Client.StatusAsync());
    });

    /// <summary>
    /// «Обновить там»: агент на другом компьютере скачает версию окна с GitHub, поставит её и перезапустится сам
    /// (работающий сервер — остановит и запустит снова; спрашиваем, игроков отключит на минуту).
    /// </summary>
    [RelayCommand]
    private Task UpdateRemoteAgent() => Do(async () =>
    {
        if (Client is not { } client) return;
        var question = _remoteServerUp ? Loc.T("selfupd.confirmRunning", AgentProtocol.AppVersion) : Loc.T("selfupd.confirm", AgentProtocol.AppVersion);
        if (!Ui.Confirm(question))
            return;
        try
        {
            _agentRestartingUntil = DateTime.UtcNow.AddMinutes(3); // с этой минуты агент может пропасть — это он обновляется
            Apply(await client.SelfUpdateAsync(AgentProtocol.AppVersion));
        }
        catch (AgentHttpException ex) when (AgentHttpException.IsOldAgent(ex))
        {
            // агент из версии без самообновления — его один раз обновляют руками
            _agentRestartingUntil = DateTime.MinValue;
            throw new InvalidOperationException(Loc.T("selfupd.tooOld"));
        }
        catch
        {
            _agentRestartingUntil = DateTime.MinValue; // не началось — пропажа связи снова ошибка
            throw;
        }
    });

    [RelayCommand]
    private Task Stop() => Do(async () => { if (Client is not null) Apply(await Client.StopAsync()); });

    [RelayCommand]
    private Task Restart() => Do(async () => { if (Client is not null) Apply(await Client.RestartAsync()); });

    [RelayCommand]
    private Task Kill() => Do(async () =>
    {
        if (Client is null) return;
        if (!Ui.Confirm(Loc.T("server.killConfirm"), warning: true)) return;
        Apply(await Client.KillAsync());
    });

    // ---------- чужой сервер ----------

    [RelayCommand]
    private Task StopForeign() => Do(async () =>
    {
        if (ForeignPid is not { } pid) return;
        ErrorText = "";
        if (!ConsoleInterop.SendCtrlCToForeignProcess(pid))
        {
            ErrorText = Loc.T("server.foreignCtrlCFailed");
            return;
        }
        // ждём, пока сохранит мир и выйдет
        for (var i = 0; i < 240 && GameProcess.FindServerPids(_main.ActiveProfile!.Model).Contains(pid); i++)
            await Task.Delay(500);
        if (GameProcess.FindServerPids(_main.ActiveProfile!.Model).Contains(pid))
            ErrorText = Loc.T("server.foreignStillRunning");
    });

    [RelayCommand]
    private Task KillForeign() => Do(() =>
    {
        if (ForeignPid is not { } pid) return Task.CompletedTask;
        if (!Ui.Confirm(Loc.T("server.killConfirm"), warning: true)) return Task.CompletedTask;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }
        return Task.CompletedTask;
    });

    [RelayCommand]
    private void OpenLogs()
    {
        if (_main.ActiveProfile?.Model.DataDir is { } data && Directory.Exists(Path.Combine(data, "Logs")))
            Ui.OpenFolder(Path.Combine(data, "Logs"));
    }
}
