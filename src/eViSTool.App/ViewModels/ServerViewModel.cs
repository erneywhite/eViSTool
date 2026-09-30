using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.App.ViewModels;

/// <summary>Виды раздела «Сервер».</summary>
public enum ServerTab { Console, Config, Schedule, Remote }

/// <summary>
/// Вкладка «Сервер». Сервером владеет агент (отдельный процесс) — окно только показывает и командует,
/// поэтому его можно закрыть в любой момент.
/// </summary>
public sealed partial class ServerViewModel : ObservableObject
{
    private const int MaxLines = 5000;

    private readonly MainViewModel _main;
    private AgentClient? _client;
    private CancellationTokenSource? _session;
    private long _lastSeq;
    private int _agentPid;
    private readonly List<string> _history = [];
    private int _historyPos = -1;

    public ObservableCollection<ConsoleLineViewModel> Lines { get; } = [];

    /// <summary>Редактор serverconfig.json — второй вид раздела, рядом с консолью.</summary>
    public ServerConfigViewModel Config { get; } = new();

    /// <summary>Расписание: резервные копии (и рестарты) — третий вид раздела.</summary>
    public ServerScheduleViewModel Schedule { get; }
    public ServerRemoteViewModel Remote { get; } = new();

    /// <summary>Какой вид раздела открыт. Консоль продолжает получать строки при любом.</summary>
    [ObservableProperty] private ServerTab _tab;

    public bool IsConsoleTab => Tab == ServerTab.Console;
    public bool IsConfigTab => Tab == ServerTab.Config;
    public bool IsScheduleTab => Tab == ServerTab.Schedule;
    public bool IsRemoteTab => Tab == ServerTab.Remote;

    // состояние сервера профиля выяснено (первый проход слежения после смены профиля завершён)
    private bool _stateKnown;

    [ObservableProperty] private bool _isServerProfile;

    /// <summary>Сервер на другом компьютере: всё через агента по сети, своих папок у профиля нет.</summary>
    [ObservableProperty] private bool _isRemoteProfile;
    private string? _remoteGameVersion;
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

    /// <summary>Подзаголовок: профиль и версия игры; путь к данным — в подсказке.</summary>
    public string HeaderSubtitle => _main.ActiveProfile is not { } p ? ""
        : p.Model.IsRemote ? Loc.T("server.subtitleRemote", p.Name, RemoteSecret.Unprotect(p.Model.RemoteCode) is { } c ? $"{c.Host}:{c.Port}" : "?",
            _remoteGameVersion ?? "?")
        : Loc.T("server.subtitle", p.Name, p.GameVersionText);
    public string DataDir => _main.ActiveProfile?.Model.DataDir ?? "";

    [RelayCommand]
    private void OpenServerMods() => _main.SelectedTab = 0;

    [RelayCommand]
    private void OpenSettings() => _main.SelectedTab = 3;
    [ObservableProperty] private string _commandText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorText = "";

    /// <summary>Агент старее программы: удалённый — «обнови там», свой при работающем сервере — «обновится после остановки».</summary>
    [ObservableProperty] private string _agentNote = "";

    /// <summary>Сервер из папки игры профиля, запущенный не нашим агентом (например, ViSST).</summary>
    [ObservableProperty] private int? _foreignPid;

    public bool HasForeign => ForeignPid is not null;
    public string ForeignText => ForeignPid is { } pid ? Loc.T("server.foreign", pid) : "";
    public bool CanStart => IsServerProfile && !IsBusy && ForeignPid is null && State == ServerState.Stopped && (!IsRemoteProfile || AgentRunning);
    public bool CanStop => AgentRunning && !IsBusy && State is ServerState.Running or ServerState.Starting;
    public bool CanCommand => AgentRunning && State is ServerState.Running or ServerState.Starting;

    /// <summary>Автопрокрутка: пока пользователь внизу — едем за новыми строками. true — прокрутить в любом случае (первая порция).</summary>
    public event Action<bool>? LinesAppended;

    public ServerViewModel(MainViewModel main)
    {
        _main = main;
        Schedule = new ServerScheduleViewModel(this);
    }

    partial void OnTabChanged(ServerTab value)
    {
        OnPropertyChanged(nameof(IsConsoleTab));
        OnPropertyChanged(nameof(IsConfigTab));
        OnPropertyChanged(nameof(IsScheduleTab));
        OnPropertyChanged(nameof(IsRemoteTab));
        Config.SetActive(value == ServerTab.Config);
        Schedule.SetActive(value == ServerTab.Schedule);
        if (value != ServerTab.Remote) Remote.Hide(); // ушли с вкладки — код подключения снова закрыт
    }

    /// <summary>Попросить агента сделать копию мира на работающем сервере.</summary>
    public Task BackupAsync() =>
        _client is { } client ? client.BackupAsync() : throw new InvalidOperationException(Loc.T("sched.notRunning"));

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
    }

    /// <summary>Сменился профиль: отключиться от старого агента, подключиться к агенту нового (если он работает).</summary>
    public void OnProfileSwitched()
    {
        _stateKnown = false;
        _session?.Cancel();
        _client?.Dispose();
        _client = null;
        AgentRunning = false;
        State = ServerState.Stopped;
        ForeignPid = null;
        ErrorText = "";
        AgentNote = "";
        ClearStats();
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(DataDir));
        Lines.Clear();
        _lastSeq = 0;
        _agentPid = 0;

        IsServerProfile = _main.ActiveProfile?.Kind == ProfileKind.Server;
        IsRemoteProfile = _main.ActiveProfile?.Model.IsRemote == true;
        _remoteGameVersion = null;
        // у удалённого профиля пока нет конфигурации и удалённого доступа (их настраивают на компьютере с сервером)
        if (IsRemoteProfile && Tab is ServerTab.Config or ServerTab.Remote) Tab = ServerTab.Console;
        var local = IsServerProfile && !IsRemoteProfile;
        Refresh();
        // сюда попадаем и при правке имени профиля: редактор сам разберётся, сменился ли файл
        Config.OnProfileSwitched(local ? _main.ActiveProfile?.Model.DataDir : null, _main.ActiveProfile?.Name ?? "",
            _main.ActiveProfile?.Model.GameDir);
        Schedule.OnProfileSwitched(IsServerProfile ? _main.ActiveProfile?.Model : null, () => _client);
        Remote.OnProfileSwitched(local ? _main.ActiveProfile?.Model : null);
        if (!IsServerProfile) return;

        _session = new CancellationTokenSource();
        _ = WatchAsync(_session.Token);
    }

    /// <summary>Цикл сеанса: подхватить агента, если он появился; следить за статусом; искать чужой сервер.</summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var profile = _main.ActiveProfile?.Model;
            if (profile is null) return;

            if (profile.IsRemote)
            {
                if (!await WatchRemoteAsync(profile, ct)) return;
                continue;
            }

            if (_client is null && AgentClient.TryConnect(profile.Id) is { } found)
                Attach(found, ct);

            if (_client is not null)
            {
                try
                {
                    var status = await _client.StatusAsync(ct);
                    Apply(status);
                    // eViSTool обновили, а агент прежний: сервер стоит — меняем агента (нужен для удалённого доступа — сразу
                    // запускаем новый; нет — новый появится при следующем запуске сервера)
                    if (AgentProtocol.IsOutdated(status) && status.State == ServerState.Stopped && status.RestartScheduledAt is null && !IsBusy)
                    {
                        var old = _client;
                        Detach();
                        await AgentLauncher.ReplaceAsync(old, ct);
                        if (Core.Server.Remote.RemoteAccess.Load(profile.Id).Enabled)
                            using (await AgentLauncher.EnsureRunningAsync(profile, startServer: false, ct: ct)) { }
                    }
                }
                catch (HttpRequestException)
                {
                    Detach(); // агент ушёл (например, сам завершился после простоя)
                }
                catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException) { }
            }

            // чужой сервер: из папки игры профиля, но не наш
            var ours = _client is not null ? State != ServerState.Stopped : false;
            var pids = await Task.Run(() => GameProcess.FindServerPids(profile), ct);
            if (ct.IsCancellationRequested) return; // профиль уже сменили — найденное относится не к нему
            ForeignPid = pids.Where(pid => !(ours && pid == _serverPid)).Select(pid => (int?)pid).FirstOrDefault();

            _stateKnown = true;
            PushServerState();

            try { await Task.Delay(1500, ct); } catch (TaskCanceledException) { return; }
        }
    }

    private int? _serverPid;

    /// <summary>
    /// Один проход слежения за удалённым сервером: подключиться по коду (если ещё нет) и взять статус.
    /// Связь пропала — сообщение и новая попытка через несколько секунд. false — сеанс закончен.
    /// </summary>
    private async Task<bool> WatchRemoteAsync(GameProfile profile, CancellationToken ct)
    {
        try
        {
            if (_client is null)
            {
                if (RemoteSecret.Unprotect(profile.RemoteCode) is not { } code)
                {
                    ErrorText = Loc.T("remote.errDecrypt");
                    _stateKnown = true;
                    return false; // без кода ждать нечего: его нужно вставить заново в настройках
                }
                var client = AgentClient.ForRemote(code);
                try
                {
                    var status = await client.StatusAsync(ct);
                    Attach(client, ct);
                    Apply(status);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }
            else
            {
                Apply(await _client.StatusAsync(ct));
            }
            if (ErrorText.Length > 0 && !IsBusy) ErrorText = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) return false;
            if (_client is not null) Detach();
            ErrorText = RemoteSecret.Describe(ex);
        }
        _stateKnown = true;
        Refresh();
        try { await Task.Delay(_client is null ? 5000 : 1500, ct); } catch (TaskCanceledException) { return false; }
        return true;
    }

    private void Apply(AgentStatus s)
    {
        AgentRunning = true;
        State = s.State;
        _serverPid = s.ServerPid;
        AgentNote = !AgentProtocol.IsOutdated(s) ? ""
            : IsRemoteProfile ? Loc.T("server.agentOutdatedRemote", s.AgentVersion, AgentProtocol.AppVersion)
            : Loc.T("server.agentOutdatedLocal", s.AgentVersion);
        if (IsRemoteProfile && s.GameVersion != _remoteGameVersion)
        {
            _remoteGameVersion = s.GameVersion;
            OnPropertyChanged(nameof(HeaderSubtitle));
        }

        // агент перезапускался — нумерация строк началась заново
        if (s.AgentPid != _agentPid || s.LastSeq < _lastSeq)
        {
            _agentPid = s.AgentPid;
            if (s.LastSeq < _lastSeq)
            {
                Lines.Clear();
                _lastSeq = 0;
            }
        }

        UptimeText = s.StartedAt is { } started && s.State != ServerState.Stopped ? FormatUptime(DateTime.Now - started) : "—";
        MemoryText = s.MemoryMb is { } mb && s.State != ServerState.Stopped ? Loc.T("server.memoryMb", mb.ToString("N0")) : "—";
        PidText = s.ServerPid is { } pid && s.State != ServerState.Stopped ? pid.ToString() : "—";
        ShowPlayers(s.Players, running: s.State == ServerState.Running);
        Schedule.ShowStatus(s);
        Remote.ShowStatus(s);
        StateNote = s.RestartScheduledAt is { } at ? Loc.T("server.restartIn", Math.Max(0, (int)(at - DateTime.Now).TotalSeconds))
            : s.State == ServerState.Stopped && s.LastExitCode is { } code ? Loc.T("server.lastExit", code)
            : s.NextRestartAt is { } planned ? Loc.T("server.restartPlanned", planned.ToString("HH:mm"))
            : "";
    }

    private void ClearStats()
    {
        UptimeText = MemoryText = PidText = "—";
        StateNote = "";
        ShowPlayers([], running: false);
        Schedule?.ShowStatus(null);
        Remote?.ShowStatus(null);
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
            players.Select(p => Loc.T("server.playerSince", p.Name, p.Since.ToString("HH:mm"), FormatUptime(DateTime.Now - p.Since))));
    }

    private static string FormatUptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes:00}m" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";

    private void Attach(AgentClient client, CancellationToken ct)
    {
        _client = client;
        AgentRunning = true;
        _ = PumpConsoleAsync(client, ct);
    }

    private void Detach()
    {
        _client?.Dispose();
        _client = null;
        AgentRunning = false;
        State = ServerState.Stopped;
        _serverPid = null;
        ClearStats();
    }

    /// <summary>Поток консоли: долгий опрос агента — строка появилась, сразу пришла.</summary>
    private async Task PumpConsoleAsync(AgentClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ReferenceEquals(client, _client))
        {
            try
            {
                var lines = await client.ConsoleAsync(_lastSeq, 25, ct);
                if (lines.Count == 0) continue;
                var first = Lines.Count == 0; // подхватили агента — сразу к последним строкам
                foreach (var l in lines) Lines.Add(new ConsoleLineViewModel(l));
                _lastSeq = lines[^1].Seq;
                while (Lines.Count > MaxLines) Lines.RemoveAt(0);
                LinesAppended?.Invoke(first);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
            {
                if (ct.IsCancellationRequested) return;
                await Task.Delay(1000, CancellationToken.None);
                if (!ReferenceEquals(client, _client)) return;
            }
        }
    }

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
            if (_client is null) throw new InvalidOperationException(Loc.T("server.stateOffline"));
            Apply(await _client.StartAsync());
            return;
        }
        var client = await AgentLauncher.EnsureRunningAsync(profile, startServer: true);
        if (!ReferenceEquals(client, _client))
        {
            if (_client is not null && _client.Endpoint.Pid == client.Endpoint.Pid) client.Dispose();
            else Attach(client, _session!.Token);
        }
        if (_client is not null) Apply(await _client.StatusAsync());
    });

    [RelayCommand]
    private Task Stop() => Do(async () => { if (_client is not null) Apply(await _client.StopAsync()); });

    [RelayCommand]
    private Task Restart() => Do(async () => { if (_client is not null) Apply(await _client.RestartAsync()); });

    [RelayCommand]
    private Task Kill() => Do(async () =>
    {
        if (_client is null) return;
        if (MessageBox.Show(Application.Current.MainWindow, Loc.T("server.killConfirm"), "eViSTool",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Apply(await _client.KillAsync());
    });

    [RelayCommand]
    private Task SendCommand() => Do(async () =>
    {
        var text = CommandText.Trim();
        if (text.Length == 0 || _client is null) return;
        if (!text.StartsWith('/')) text = "/" + text; // «time» → «/time», как в игре
        _history.Remove(text);
        _history.Add(text);
        _historyPos = -1;
        CommandText = "";
        await _client.CommandAsync(text);
    });

    /// <summary>↑/↓ в поле команды — история, как в консоли.</summary>
    public void HistoryStep(int direction)
    {
        if (_history.Count == 0) return;
        _historyPos = _historyPos < 0
            ? (direction < 0 ? _history.Count - 1 : -1)
            : _historyPos + direction;
        if (_historyPos >= _history.Count) _historyPos = -1;
        if (_historyPos < -1) _historyPos = 0;
        CommandText = _historyPos < 0 ? "" : _history[Math.Max(0, _historyPos)];
    }

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
        if (MessageBox.Show(Application.Current.MainWindow, Loc.T("server.killConfirm"), "eViSTool",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return Task.CompletedTask;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }
        return Task.CompletedTask;
    });

    [RelayCommand]
    private void ClearConsole() => Lines.Clear();

    [RelayCommand]
    private void OpenLogs()
    {
        if (_main.ActiveProfile?.Model.DataDir is { } data && Directory.Exists(Path.Combine(data, "Logs")))
            Shell.OpenFolder(Path.Combine(data, "Logs"));
    }
}

/// <summary>Строка консоли с цветом по уровню.</summary>
public sealed class ConsoleLineViewModel(ConsoleLine line)
{
    // лесная палитра: ошибки — розоватые, предупреждения — янтарь, ввод — песок, сообщения eViSTool — дымчато-голубые, чат — зелёный
    private static readonly Brush ErrorBrush = Frozen(0xE8, 0x9B, 0x96);
    private static readonly Brush WarningBrush = Frozen(0xED, 0xBF, 0x82);
    private static readonly Brush InputBrush = Frozen(0xD4, 0xBD, 0x83);
    private static readonly Brush SystemBrush = Frozen(0x9F, 0xB7, 0xC9);
    private static readonly Brush ChatBrush = Frozen(0xB0, 0xC8, 0x98);

    public string Text { get; } = line.Kind == ConsoleLineKind.Input ? "> " + line.Text : line.Text;

    /// <summary>null — обычный цвет текста темы.</summary>
    public Brush? Foreground { get; } = line.Kind switch
    {
        ConsoleLineKind.Error => ErrorBrush,
        ConsoleLineKind.Input => InputBrush,
        ConsoleLineKind.System => SystemBrush,
        _ => (line.Level?.Replace("Server ", "", StringComparison.Ordinal)) switch // в консоли уровни вида «Server Event»
        {
            "Error" or "Fatal" => ErrorBrush,
            "Warning" => WarningBrush,
            "Chat" => ChatBrush,
            _ => null,
        },
    };

    public double Opacity { get; } = line.Kind == ConsoleLineKind.Output
                                     && line.Level?.EndsWith("Notification", StringComparison.Ordinal) == true ? 0.75 : 1;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
