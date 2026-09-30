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

namespace eViSTool.App.ViewModels;

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

    /// <summary>Открыта «Конфигурация» (иначе — «Консоль»). Консоль при этом продолжает получать строки.</summary>
    [ObservableProperty] private bool _isConfigTab;

    // состояние сервера профиля выяснено (первый проход слежения после смены профиля завершён)
    private bool _stateKnown;

    [ObservableProperty] private bool _isServerProfile;
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
    public string HeaderSubtitle => _main.ActiveProfile is { } p ? Loc.T("server.subtitle", p.Name, p.GameVersionText) : "";
    public string DataDir => _main.ActiveProfile?.Model.DataDir ?? "";

    [RelayCommand]
    private void OpenServerMods() => _main.SelectedTab = 0;

    [RelayCommand]
    private void OpenSettings() => _main.SelectedTab = 3;
    [ObservableProperty] private string _commandText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorText = "";

    /// <summary>Сервер из папки игры профиля, запущенный не нашим агентом (например, ViSST).</summary>
    [ObservableProperty] private int? _foreignPid;

    public bool HasForeign => ForeignPid is not null;
    public string ForeignText => ForeignPid is { } pid ? Loc.T("server.foreign", pid) : "";
    public bool CanStart => IsServerProfile && !IsBusy && ForeignPid is null && State == ServerState.Stopped;
    public bool CanStop => AgentRunning && !IsBusy && State is ServerState.Running or ServerState.Starting;
    public bool CanCommand => AgentRunning && State is ServerState.Running or ServerState.Starting;

    /// <summary>Автопрокрутка: пока пользователь внизу — едем за новыми строками. true — прокрутить в любом случае (первая порция).</summary>
    public event Action<bool>? LinesAppended;

    public ServerViewModel(MainViewModel main) => _main = main;

    partial void OnIsConfigTabChanged(bool value) => Config.SetActive(value);

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
        (StateText, StateTone) = !IsServerProfile ? ("", RowTone.Muted) : (AgentRunning ? State : ServerState.Stopped) switch
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
        ClearStats();
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(DataDir));
        Lines.Clear();
        _lastSeq = 0;
        _agentPid = 0;

        IsServerProfile = _main.ActiveProfile?.Kind == ProfileKind.Server;
        Refresh();
        // сюда попадаем и при правке имени профиля: редактор сам разберётся, сменился ли файл
        Config.OnProfileSwitched(IsServerProfile ? _main.ActiveProfile?.Model.DataDir : null, _main.ActiveProfile?.Name ?? "",
            _main.ActiveProfile?.Model.GameDir);
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

            if (_client is null && AgentClient.TryConnect(profile.Id) is { } found)
                Attach(found, ct);

            if (_client is not null)
            {
                try
                {
                    Apply(await _client.StatusAsync(ct));
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

    private void Apply(AgentStatus s)
    {
        AgentRunning = true;
        State = s.State;
        _serverPid = s.ServerPid;

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
        StateNote = s.RestartScheduledAt is { } at ? Loc.T("server.restartIn", Math.Max(0, (int)(at - DateTime.Now).TotalSeconds))
            : s.State == ServerState.Stopped && s.LastExitCode is { } code ? Loc.T("server.lastExit", code)
            : "";
    }

    private void ClearStats()
    {
        UptimeText = MemoryText = PidText = "—";
        StateNote = "";
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
