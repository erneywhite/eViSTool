using eViSTool.Presentation;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Виды раздела «Сервер».</summary>
public enum ServerTab { Console, Players, Stats, Journal, Announcements, Config, Schedule, Remote, Notify, Backups }

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

    /// <summary>Сервер у хостера: лог, состояние и игроки по FTP — вместо всех видов своего сервера.</summary>
    public HostedServerViewModel Hosted { get; }

    /// <summary>
    /// Оповещения сервера у хостера — второй (и последний) вид его раздела рядом с «Обзором»: та же вкладка, что у своего
    /// сервера, но события — только его, а шлёт их окно. Свой экземпляр: вкладке своего сервера хостовый профиль не
    /// передаётся.
    /// </summary>
    public ServerNotifyViewModel HostedNotify { get; }

    /// <summary>Копии мира сервера у хостера на этом компьютере — третий вид его раздела.</summary>
    public HostedBackupsViewModel HostedBackups { get; }

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
    public bool IsBackupsTab => Tab == ServerTab.Backups;

    /// <summary>У хостера открыт «Обзор»: любой вид, кроме «Оповещений» и «Копий».</summary>
    public bool IsHostedOverviewTab => Tab is not (ServerTab.Notify or ServerTab.Backups);

    // состояние сервера профиля выяснено (первый проход слежения после смены профиля завершён)
    private bool _stateKnown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotServer), nameof(ShowConsoleEmpty))]
    private bool _isServerProfile;

    /// <summary>Сервер на другом компьютере: всё через агента по сети, своих папок у профиля нет.</summary>
    [ObservableProperty] private bool _isRemoteProfile;

    /// <summary>Сервер у хостера: управляет им панель хостера, здесь — лог, состояние и игроки (<see cref="Hosted"/>) и переход к модам.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotServer), nameof(IsRunning))]
    private bool _isHostedProfile;

    /// <summary>Плашка «это не серверный профиль»: у сервера у хостера своя плашка.</summary>
    public bool ShowNotServer => !IsServerProfile && !IsHostedProfile;
    private string? _remoteGameVersion;
    private string? _remoteOs; // система машины с сервером — в подзаголовке
    [ObservableProperty] private ServerState _state = ServerState.Stopped;
    [ObservableProperty] private bool _agentRunning;
    [ObservableProperty] private string _stateText = "";

    // плитки над консолью: пока сервер не запущен, значения пустые (не прочерки) — вид приглушает такую плитку
    [ObservableProperty] private RowTone _stateTone = RowTone.Muted;
    [ObservableProperty] private string _uptimeText = "";
    [ObservableProperty] private string _memoryText = "";
    [ObservableProperty] private string _pidText = "";
    /// <summary>Под плиткой состояния: перезапуск через N с / код выхода.</summary>
    [ObservableProperty] private string _stateNote = "";

    /// <summary>На машине с сервером eViSTool новее этого окна: обновлять надо здесь, а не там.</summary>
    private static bool NewerAgent(AgentStatus s) =>
        Core.Versioning.ModVersion.TryParse(s.AgentVersion, out var agent)
        && Core.Versioning.ModVersion.TryParse(AgentProtocol.AppVersion, out var app) && agent.CompareTo(app) > 0;

    /// <summary>
    /// Адрес сервера у хостера для подзаголовка: только хост и порт, как у удалённого. Логин FTP в шапке ни к чему — он
    /// попадал бы на каждый скриншот раздела; целиком подключение видно в «Подключение…».
    /// </summary>
    private static string HostedAddress(HostedConnection? hosted) => hosted is null ? "?" : $"{hosted.Host}:{hosted.Port}";

    /// <summary>Подзаголовок: профиль и версия игры; путь к данным — в подсказке.</summary>
    public string HeaderSubtitle => _main.ActiveProfile is not { } p ? ""
        // версию игры у хостера узнаёт чтение модов: пока не узнали — без неё, а не «Vintage Story узнаю при чтении модов»
        : p.Model.IsHosted ? (p.Model.Hosted?.GameVersion ?? p.HostedGameVersion) is { Length: > 0 } hostedVersion
            ? Loc.T("server.subtitleHosted", p.Name, HostedAddress(p.Model.Hosted), hostedVersion)
            : Loc.T("server.subtitleHostedNoVersion", p.Name, HostedAddress(p.Model.Hosted))
        : p.Model.IsRemote ? Loc.T("server.subtitleRemote", p.Name,
            (RemoteSecret.Unprotect(p.Model.RemoteCode) is { } c ? $"{c.Host}:{c.Port}" : "?") + (_remoteOs is { Length: > 0 } os ? " · " + os : ""),
            _remoteGameVersion ?? "?")
        : Loc.T("server.subtitle", p.Name, p.GameVersionText);
    public string DataDir => _main.ActiveProfile?.Model.DataDir ?? "";

    [RelayCommand]
    private void OpenServerMods() => _main.SelectedTab = AppTab.Mods;

    /// <summary>«Открыть профили» у клиентского профиля и «Подключение…» у сервера у хостера: профиль правят в «Профилях».</summary>
    [RelayCommand]
    private void OpenProfiles() => _main.OpenProfileEditor(_main.ActiveProfile);

    /// <summary>Общие «Настройки»: там каналы оповещений (переход из вкладки «Оповещения»).</summary>
    internal void OpenAppSettings() => _main.SelectedTab = AppTab.Settings;

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

    internal void NotifyCanStart()
    {
        OnPropertyChanged(nameof(CanStart));
        NotifyConsoleEmpty(); // восстановление мира — причина не запускать
    }

    public bool CanStop => AgentRunning && !IsBusy && State is ServerState.Running or ServerState.Starting;
    public bool CanCommand => AgentRunning && State is ServerState.Running or ServerState.Starting;

    /// <summary>
    /// Сервер профиля работает — не запускается и не останавливается: «дышащая» точка в карточке профиля внизу боковой
    /// панели. Свой и удалённый — по статусу агента, у хостера — по свежему опросу лога, кто бы его ни читал
    /// (<see cref="HostedServerViewModel.IsRunning"/>): раздел «Сервер» при этом может быть закрыт.
    /// </summary>
    public bool IsRunning => IsHostedProfile ? Hosted.IsRunning : AgentRunning && State == ServerState.Running;

    // ---------- пустая консоль ----------

    /// <summary>
    /// Вместо пустой консоли — по центру «Сервер остановлен» и «Запустить»: строк нет, сервер не запущен и не чужой (о
    /// чужом — плашка выше). Есть строки или сервер работает — обычная консоль. Пока первый проход слежения после смены
    /// профиля не кончился, заглушки нет: сервер мог и работать, а крупное «остановлен» с кнопкой мелькало бы зря.
    /// </summary>
    public bool ShowConsoleEmpty => IsServerProfile && _stateKnown && Lines.Count == 0 && !IsServerUp && !HasForeign;

    /// <summary>Заголовок пустой консоли: «Сервер остановлен», у удалённого без связи — «Нет связи с сервером».</summary>
    public string ConsoleEmptyTitle => RemoteOffline ? Loc.T("server.emptyOffline") : Loc.T("server.emptyStopped");

    /// <summary>Почему запустить нельзя — тогда вместо кнопки эта строка. Пусто — можно (или мешает только идущее действие с кнопки).</summary>
    public string ConsoleEmptyNote => RemoteOffline ? Loc.T(_remoteNeedsFix ? "server.emptyOfflineFix" : "server.emptyOfflineHint")
        : Schedule?.IsRestoring == true ? Loc.T("server.emptyRestoring")
        : "";

    /// <summary>
    /// «Запустить» в пустой консоли: видна, когда причины не запускать нет. Пока идёт действие с кнопки, она видна, но
    /// неактивна (<see cref="CanStart"/>): иначе мигала бы на каждое нажатие.
    /// </summary>
    public bool ShowConsoleEmptyStart => ConsoleEmptyNote.Length == 0;

    // удалённый сервер, а связи с его агентом нет: остановлен ли он, неизвестно
    private bool RemoteOffline => IsRemoteProfile && !AgentRunning;

    // связь сама не вернётся (код не читается, ключ или сертификат не подходят): пустая консоль не обещает «появится,
    // когда связь вернётся», а говорит, что поправить
    private bool _remoteNeedsFix;

    private void SetRemoteNeedsFix(bool value)
    {
        if (_remoteNeedsFix == value) return;
        _remoteNeedsFix = value;
        NotifyConsoleEmpty();
    }

    private bool _hadLines; // в консоли были строки — при прошлом изменении списка

    private void NotifyConsoleEmpty()
    {
        OnPropertyChanged(nameof(ShowConsoleEmpty));
        OnPropertyChanged(nameof(ConsoleEmptyTitle));
        OnPropertyChanged(nameof(ConsoleEmptyNote));
        OnPropertyChanged(nameof(ShowConsoleEmptyStart));
    }

    public ServerViewModel(MainViewModel main)
    {
        _main = main;
        _monitor = new ServerMonitor(this);
        _console = new ServerConsoleViewModel(() => Client);
        // свойства консоли вид берёт у раздела под теми же именами — и уведомления о них приходят от раздела. Фильтра по
        // имени нет: у каждого свойства консоли есть двойник здесь, это держит тест ServerConsoleViewModelTests
        _console.PropertyChanging += (_, e) => OnPropertyChanging(e);
        _console.PropertyChanged += (_, e) => OnPropertyChanged(e);
        // пустая консоль: заглушка видна, пока строк нет, — сообщаем, только когда список пустеет или наполняется
        _console.Lines.CollectionChanged += (_, _) =>
        {
            if (_hadLines == Lines.Count > 0) return;
            _hadLines = Lines.Count > 0;
            OnPropertyChanged(nameof(ShowConsoleEmpty));
        };
        Schedule = new ServerScheduleViewModel(this);
        Players = new ServerPlayersViewModel(this);
        _console.LinesReceived += Players.OnConsoleLines; // ответ сервера на команду вкладки «Игроки»
        Announcements = new ServerAnnouncementsViewModel(this);
        Stats = new ServerStatsViewModel(this);
        Journal = new ServerJournalViewModel(this);
        Notify = new ServerNotifyViewModel(this);
        Hosted = new HostedServerViewModel(main);
        // «дышащая» точка у хостера — по свежему опросу лога
        Hosted.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HostedServerViewModel.IsRunning)) OnPropertyChanged(nameof(IsRunning));
        };
        HostedNotify = new ServerNotifyViewModel(this, hosted: true);
        // операции с копиями мира — общие на окно (их делает и расписание); без главной модели (тесты раздела) — свои
        HostedBackups = new HostedBackupsViewModel(main, main?.HostedWorldJobs ?? new HostedWorldJobs());
        // включили или выключили маршрут — сторож оповещений берёт профиль сразу, а не через минуту; не ушло — скажем на вкладке
        // и будем говорить, пока в канал не уйдёт удачное (главной модели нет только в тестах раздела)
        HostedNotify.Saved += () => main?.HostedWatch?.Refresh();
        HostedNotify.Watch = main?.HostedWatch;
    }

    /// <summary>
    /// Раздел «Сервер» открыт или закрыт. Лог сервера у хостера читается по FTP только пока раздел открыт; своему
    /// серверу это не нужно — его наблюдатель работает всегда.
    /// </summary>
    public void SetSectionActive(bool open)
    {
        if (open && IsHostedProfile) OnPropertyChanged(nameof(HeaderSubtitle)); // версию игры могло найти чтение модов
        Hosted.SetActive(open);
        if (open && IsBackupsTab) HostedBackups.SetActive(true); // список копий — свежий при каждом заходе
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
        OnPropertyChanged(nameof(IsBackupsTab));
        OnPropertyChanged(nameof(IsHostedOverviewTab));
        Config.SetActive(value == ServerTab.Config);
        Schedule.SetActive(value == ServerTab.Schedule);
        Players.SetActive(value == ServerTab.Players);
        Announcements.SetActive(value == ServerTab.Announcements);
        Stats.SetActive(value == ServerTab.Stats);
        Journal.SetActive(value == ServerTab.Journal);
        Notify.SetActive(value == ServerTab.Notify);
        HostedNotify.SetActive(value == ServerTab.Notify);
        HostedBackups.SetActive(value == ServerTab.Backups);
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
        OnPropertyChanged(nameof(IsRunning));
        NotifyConsoleEmpty();
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
        Hosted.OnLanguageChanged();
        HostedNotify.OnLanguageChanged();
        HostedBackups.OnLanguageChanged();
    }

    /// <summary>Сменился профиль: отключиться от старого агента, подключиться к агенту нового (если он работает).</summary>
    public void OnProfileSwitched()
    {
        // правки конфига старого профиля — сохранить, пока связь с его агентом ещё открыта
        var next = _main.ActiveProfile?.Model;
        // у профиля у хостера нет ни папок на этой машине, ни агента: серверных вкладок для него нет
        var nextServer = next?.Kind == ProfileKind.Server && next.IsHosted == false;
        Config.BeforeSwitch(nextServer && next is { IsLocal: true } ? next.DataDir : null, nextServer && next!.IsRemote ? next : null);

        _stateKnown = false;
        _remoteNeedsFix = false;
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

        IsHostedProfile = _main.ActiveProfile?.Model.IsHosted == true;
        // хостовый профиль — Kind = Server, но серверным разделом это не считается: монитора и вкладок нет, свой вид — Hosted
        IsServerProfile = _main.ActiveProfile?.Kind == ProfileKind.Server && !IsHostedProfile;
        Hosted.OnProfileSwitched(IsHostedProfile ? _main.ActiveProfile?.Model : null);
        HostedNotify.OnProfileSwitched(IsHostedProfile ? _main.ActiveProfile?.Model : null);
        HostedBackups.OnProfileSwitched(IsHostedProfile ? _main.ActiveProfile : null);
        IsRemoteProfile = _main.ActiveProfile?.Model.IsRemote == true;
        _remoteGameVersion = null;
        _remoteOs = null;
        // удалённый доступ настраивают на компьютере с сервером — у удалённого профиля этой вкладки нет
        if (IsRemoteProfile && Tab == ServerTab.Remote) Tab = ServerTab.Console;
        // у хостера видов три: «Обзор» (тот же Console), «Оповещения» и «Копии» — остальных у него нет; «Копий» нет у своего
        if (IsHostedProfile && Tab is not (ServerTab.Console or ServerTab.Notify or ServerTab.Backups)) Tab = ServerTab.Console;
        if (!IsHostedProfile && Tab == ServerTab.Backups) Tab = ServerTab.Console;
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

        if (!_stateKnown)
        {
            _stateKnown = true;
            NotifyConsoleEmpty(); // первый проход: теперь известно, остановлен ли сервер
        }
        PushServerState();
        Players.PollLocal(); // файлы игроков поменялись (сервер записал списки, правка в другом окне)
        Announcements.PollLocal();
        Notify.PollLocal();
    }

    void IServerMonitorHost.OnRemoteAnswered()
    {
        if (ErrorText.Length > 0 && !IsBusy) ErrorText = "";
        SetRemoteNeedsFix(false);
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
        SetRemoteNeedsFix(error is not null && DateTime.UtcNow >= _agentRestartingUntil && RemoteSecret.NeedsNewCode(error));
        _stateKnown = true;
        Refresh();
    }

    void IServerMonitorHost.OnRemoteCodeUnreadable()
    {
        ErrorText = Loc.T("remote.errDecrypt");
        _stateKnown = true;
        _remoteNeedsFix = true;
        NotifyConsoleEmpty();
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

        UptimeText = s.StartedAt is { } started && s.State != ServerState.Stopped ? Dates.Uptime(DateTime.Now - started) : "";
        MemoryText = s.MemoryMb is { } mb && s.State != ServerState.Stopped ? Loc.T("server.memoryMb", mb.ToString("N0")) : "";
        PidText = s.ServerPid is { } pid && s.State != ServerState.Stopped ? pid.ToString() : "";
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
        UptimeText = MemoryText = PidText = "";
        StateNote = "";
        ShowPlayers([], running: false);
        Schedule?.ShowStatus(null);
        Remote?.ShowStatus(null);
        Players?.ShowStatus(null);
        Announcements?.ShowStatus(null);
        Notify?.ShowStatus(null);
    }

    // ---------- игроки на сервере ----------

    /// <summary>Плитка «Игроки»: число, имена одной строкой и подсказка «кто с какого времени». Сервер не работает — пусто.</summary>
    [ObservableProperty] private string _playersText = "";
    [ObservableProperty] private string _playerNames = "";
    [ObservableProperty] private string? _playersTip;

    private void ShowPlayers(IReadOnlyList<OnlinePlayer> players, bool running)
    {
        PlayersText = running ? players.Count.ToString() : "";
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
            _main.MarkPlayed(profile);
            return;
        }
        var client = await AgentLauncher.EnsureRunningAsync(profile, startServer: true);
        _main.MarkPlayed(profile);
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
