using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Запуск в выборе над консолью: текущий (<see cref="Run"/> = null) или прошлый из Logs/Archive.</summary>
public sealed record HostedRunOption(HostedRun? Run, string Text);

/// <summary>Игрок на сервере у хостера: имя и «с 13:31 · 12 мин» по часам этой машины.</summary>
public sealed record HostedPlayerRow(string Name, string SinceText);

/// <summary>
/// Раздел «Сервер» у сервера у хостера: что сервер пишет в server-main.log (по FTP, только чтение), его состояние по
/// логу, кто на нём и прошлые запуски из Logs/Archive. Запускают, останавливают и командуют сервером в панели хостера —
/// здесь только смотрят. Лог опрашивается раз в <see cref="PollInterval"/> и только пока раздел открыт, а профиль
/// активен и у хостера; ушли с раздела — опрос стоит, вернулись — продолжается с того же места лога. Ошибку, которую
/// повтор не вылечит (неверный пароль, недоверенный сертификат), опрос не повторяет, пока подключение не поправят или
/// раздел не откроют заново. Новый вылет показывается тем же окном, что у своего сервера. Сам лог — <see cref="HostedLogs"/>
/// (один на профиль, <see cref="HostedLogsPool"/>), строки консоли и их цвет — те же <see cref="ConsoleLineViewModel"/>,
/// что у своей консоли. Сервер по логу остановлен, а для модов вышли обновления — плашка «Обновить всё»: самое время
/// обновить их перед запуском.
/// </summary>
public sealed partial class HostedServerViewModel : ObservableObject
{
    /// <summary>Как часто читать лог, пока раздел открыт.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Пауза после ошибки сети: связь пропала — не стучимся каждые три секунды.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    /// <summary>Сколько строк текущего запуска держит консоль — как у своей.</summary>
    public const int MaxLines = ServerConsoleViewModel.MaxLines;

    /// <summary>
    /// Сколько строк прошлого запуска показывать — столько же, сколько у текущего и у своей консоли: строки добавляются
    /// в список по одной, и десятки тысяч из лога в 8 МБ подвесили бы окно. Обычный запуск — несколько сотен строк.
    /// </summary>
    public const int PastMaxLines = MaxLines;

    private readonly MainViewModel _main;
    private IUiTimer? _timer;
    private GameProfile? _profile;
    private HostedLogs? _logs;
    private CancellationTokenSource _cts = new();
    private int _generation;
    private bool _sectionOpen;
    private bool _polling;
    private bool _attached;            // первый опрос после подключения к логам профиля был: дальше — только новые строки
    private bool _halted;              // ошибка, которую повтор не вылечит: опрос стоит до правки подключения или нового открытия раздела
    private bool _runsAgain;           // список запусков не прочитался из-за сети: попробовать со следующим опросом
    private bool _crashAgain;          // поиск вылета не удался из-за сети: попробовать со следующим опросом
    private DateTime? _updatedAt;      // последний удачный опрос, время этой машины
    private Exception? _error;         // последний опрос не удался — текст в статусе (пересобирается при смене языка)
    private int _runRequest;           // номер запроса прошлого запуска: ответ на устаревший выбор не показываем
    private bool _quietSelection;      // выбор запуска меняет сам раздел (список пересобран) — не читать лог заново
    private HostedRunLog? _past;       // показанный прошлый запуск
    private readonly List<string> _current = [];                   // строки текущего запуска, пока смотрят прошлый
    private IReadOnlyList<OnlinePlayer> _online = [];
    private readonly HashSet<string> _seenCrashes = new(StringComparer.Ordinal);
    private HostedModUpdates? _updates;        // проверки обновлений, заданные тестом (иначе — общие окна)
    private HostedModUpdates? _updatesWatched; // на чьи перемены подписаны
    private Func<ModTarget, IReadOnlyList<UpdateQueueItem>, Task<IReadOnlyList<UpdateQueueItem>>>? _runUpdates;
    private DateTime? _updatesTried;           // последняя попытка проверить обновления (UTC): не удалась — не раньше чем через UpdatesRetry
    private List<UpdateQueueItem>? _updating;  // пункты очереди, которые сейчас ставят «Обновить всё»

    /// <param name="main">Главная модель: окно вылета выключает мод через неё.</param>
    public HostedServerViewModel(MainViewModel main)
    {
        _main = main;
        ShowState();
    }

    /// <summary>Строки консоли: текущего запуска (растут) или выбранного прошлого (как прочитаны).</summary>
    public ObservableCollection<ConsoleLineViewModel> Lines { get; } = [];

    /// <summary>Автопрокрутка, как у своей консоли: true — прокрутить в любом случае (первая порция, другой запуск).</summary>
    public event Action<bool>? LinesAppended;

    /// <summary>«Текущий запуск» и прошлые из Logs/Archive, новые сверху.</summary>
    public ObservableCollection<HostedRunOption> Runs { get; } = [];

    /// <summary>Выбор запуска виден: список запусков уже прочитан.</summary>
    public bool HasRuns => Runs.Count > 0;

    [ObservableProperty] private HostedRunOption? _selectedRun;

    /// <summary>Открыт прошлый запуск: консоль статична, новые строки текущего копятся в стороне.</summary>
    [ObservableProperty] private bool _isPast;

    /// <summary>Чем кончился открытый прошлый запуск (или что он читается, или почему не прочитался).</summary>
    [ObservableProperty] private string _runNote = "";

    /// <summary>Кто на сервере, по времени входа.</summary>
    public ObservableCollection<HostedPlayerRow> Players { get; } = [];

    [ObservableProperty] private string _playersEmptyText = "—";
    public bool HasPlayers => Players.Count > 0;

    /// <summary>Состояние сервера по логу. Надёжны только «остановлен» и «вылетел» (см. <see cref="HostedServerState"/>).</summary>
    [ObservableProperty] private HostedServerState _state = HostedServerState.Unknown;

    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private RowTone _stateTone = RowTone.Muted;

    /// <summary>Под состоянием: «по логу сервера · 13:31:42» — откуда знаем и когда последний раз прочитали.</summary>
    [ObservableProperty] private string _stateNote = "";

    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _playersText = "—";
    [ObservableProperty] private string _playerNames = "";

    /// <summary>Лог не читается (нет связи, пароль не прочитать): что случилось и что дальше. Пусто — всё в порядке.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Консоль текущего запуска пуста: лога нет или сервер ещё не запускали.</summary>
    public bool ShowEmpty => !IsPast && Lines.Count == 0 && _attached;

    /// <summary>Опрос по таймеру идёт (раздел открыт, профиль у хостера).</summary>
    public bool IsPolling => _timer?.IsEnabled == true;

    /// <summary>Вылеты раньше этого (время этой машины) не показываем: окно не тревожит задним числом. По умолчанию — когда раздел создан.</summary>
    public DateTime CrashesSince { get; set; } = DateTime.Now;

    partial void OnIsPastChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    // ---------- профиль и раздел

    /// <summary>
    /// Сменился активный профиль или его настройки. Тот же сервер (поменяли имя, флажок) — всё остаётся как есть; другой
    /// профиль или другое подключение — консоль, игроки и запуски с чистого листа. null — профиль не у хостера.
    /// </summary>
    public void OnProfileSwitched(GameProfile? profile)
    {
        var target = profile is { IsHosted: true } ? ModTarget.For(profile) : null;
        var same = profile is not null && _profile?.Id == profile.Id
                   && (target is null ? _logs is null : _logs is not null && HostedLogsPool.SameServer(_logs.Target, target));
        _profile = profile is { IsHosted: true } ? profile : null;
        WatchUpdates();
        if (same) return;

        Reset();
        _logs = target is { IsHosted: true } ? HostedLogsPool.For(target) : null;
        if (_profile is not null && _logs is null) StatusText = ModTarget.UnreadableText(_profile);
        ShowState(); // «читаю лог сервера…» до первого опроса
        UpdatePolling();
    }

    /// <summary>Раздел «Сервер» открыт или закрыт.</summary>
    public void SetActive(bool open)
    {
        if (open && !_sectionOpen) _halted = false; // открыли заново — ещё одна попытка
        _sectionOpen = open;
        UpdatePolling();
    }

    private void UpdatePolling()
    {
        if (!_sectionOpen || _logs is null || _halted)
        {
            _timer?.Stop();
            OnPropertyChanged(nameof(IsPolling));
            return;
        }
        if (_timer is null)
        {
            _timer = Ui.Timer(PollInterval);
            _timer.Tick += (_, _) => _ = PollAsync();
        }
        if (_timer.IsEnabled) return;
        _timer.Interval = _error is null ? PollInterval : RetryInterval;
        _timer.Start();
        OnPropertyChanged(nameof(IsPolling));
        _ = PollAsync(); // раздел открыли — сразу, не через три секунды
    }

    private void Reset()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _generation++;
        // таймер — заново: другой профиль читается сразу и с обычным интервалом, а не по тику прежнего (после его ошибки —
        // через десять секунд)
        _timer?.Stop();
        _polling = false;
        _attached = false;
        _halted = false;
        _runsAgain = false;
        _crashAgain = false;
        _updatedAt = null;
        _error = null;
        _past = null;
        _runRequest++;
        _current.Clear();
        _online = [];
        Lines.Clear();
        _quietSelection = true;
        Runs.Clear();
        SelectedRun = null;
        _quietSelection = false;
        OnPropertyChanged(nameof(HasRuns));
        IsPast = false;
        RunNote = "";
        StatusText = "";
        State = HostedServerState.Unknown;
        ShowState();
        OnPropertyChanged(nameof(ShowEmpty));
        // плашка обновлений — про прежний профиль; его очередь, если идёт, доставит «Мои моды»
        if (_updating is { } items) foreach (var item in items) item.PropertyChanged -= OnUpdateItemChanged;
        _updating = null;
        IsUpdating = false;
        UpdatesCount = 0;
        UpdatesNote = "";
        UpdatesProgressText = "";
        _updatesTried = null;
    }

    /// <summary>Сменился язык: тексты, собранные в коде, — заново.</summary>
    public void OnLanguageChanged()
    {
        ShowState();
        OnPropertyChanged(nameof(UpdatesText));
        ShowUpdateProgress();
        if (_error is not null) StatusText = ErrorText(_error);
        if (_past is not null) RunNote = Outcome(_past);
        if (_logs is { } logs && Runs.Count > 0)
        {
            var chosen = SelectedRun?.Run?.Name;
            FillRuns(logs, [.. Runs.Select(r => r.Run).OfType<HostedRun>()], chosen);
        }
    }

    // ---------- опрос

    /// <summary>
    /// Один опрос лога: новые строки — в консоль, состояние и игроки — в плитки. Сервер запущен заново — консоль с чистого
    /// листа и свежий список запусков. Вылетел (или перезапущен после вылета) — найти вылет и показать окно. Сбой сети —
    /// строкой в статусе, следующий опрос — через <see cref="RetryInterval"/>.
    /// </summary>
    public async Task PollAsync()
    {
        if (_polling || _logs is not { } logs || _profile is not { } profile) return;
        _polling = true;
        var generation = _generation;
        var ct = _cts.Token;
        try
        {
            HostedLogUpdate update;
            try
            {
                update = await logs.PollAsync(ct);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                if (generation != _generation || ct.IsCancellationRequested) return;
                _error = ex;
                StatusText = ErrorText(ex);
                if (IsHopeless(ex))
                {
                    // неверный пароль каждые десять секунд — сотни неудачных входов в час, хостер может закрыть адрес
                    _halted = true;
                    UpdatePolling();
                }
                else SetInterval(RetryInterval);
                return;
            }
            if (generation != _generation) return; // пока читали, профиль сменили
            _error = null;
            StatusText = "";
            SetInterval(PollInterval);

            var first = !_attached;
            Apply(logs, update);
            if (first || update.Restarted || _runsAgain) await LoadRunsAsync(logs, generation, ct);
            // вылет ищем после перезапуска, пока сервер лежит (если вылет этого запуска ещё не отдан: поиск — лишний листинг
            // на каждом опросе) и при первом опросе профиля, если прошлый запуск мог кончиться уже при открытом окне: сервер
            // мог упасть и снова запуститься, пока раздел не открывали. Текущий запуск начался раньше CrashesSince — вылет
            // прошлого старый, и архив не читаем. Поиск сорвался из-за сети — со следующим опросом
            var archiveDue = first && !(logs.RunStarted is { } started && logs.ToLocal(started) < CrashesSince);
            var crashCheck = update.Restarted || _crashAgain
                             || (update.State == HostedServerState.Crashed ? !logs.CurrentCrashReported : archiveDue);
            if (crashCheck) await CheckCrashAsync(logs, profile, generation, ct);
            // вылет мог найти и сторож оповещений, пока раздел был закрыт: логи отдают вылет один раз — он у них в списке
            foreach (var crash in logs.FoundCrashes) ShowCrash(profile, crash);
        }
        finally
        {
            if (generation == _generation) _polling = false;
        }
    }

    // таймер окна при смене интервала отсчитывает заново — меняем, только когда интервал правда другой
    private void SetInterval(TimeSpan interval)
    {
        if (_timer is not null && _timer.Interval != interval) _timer.Interval = interval;
    }

    private void Apply(HostedLogs logs, HostedLogUpdate update)
    {
        if (!_attached)
        {
            // логи профиля могли читать и раньше (раздел уже открывали, проверка перед изменением модов) — тогда в опросе
            // только новое, а консоли нужен весь текущий запуск: он у логов есть целиком.
            // shortcut: проверка из другой вкладки, успевшая прочитать лог между этим опросом и снимком строк, отдаст свои
            // строки ещё раз со следующим опросом — в консоли они повторятся. Переделать, если станет заметно
            _attached = true;
            _current.Clear();
            _current.AddRange(Tail(logs.Lines, MaxLines));
            if (!IsPast) ShowCurrent();
        }
        else
        {
            if (update.Restarted)
            {
                _current.Clear();
                if (!IsPast) Lines.Clear();
            }
            _current.AddRange(update.NewLines);
            if (_current.Count > MaxLines) _current.RemoveRange(0, _current.Count - MaxLines);
            if (!IsPast && (update.NewLines.Count > 0 || update.Restarted))
            {
                var fresh = Lines.Count == 0;
                foreach (var line in update.NewLines) Lines.Add(Line(line));
                while (Lines.Count > MaxLines) Lines.RemoveAt(0);
                LinesAppended?.Invoke(fresh);
            }
        }
        _updatedAt = DateTime.Now;
        _online = update.Players;
        State = update.State;
        ShowState();
        OnPropertyChanged(nameof(ShowEmpty));
        _ = RefreshUpdatesAsync();
    }

    /// <summary>Консоль — строки текущего запуска (раздел открыт заново, вернулись из прошлого запуска).</summary>
    private void ShowCurrent()
    {
        Lines.Clear();
        foreach (var line in _current) Lines.Add(Line(line));
        LinesAppended?.Invoke(true);
        OnPropertyChanged(nameof(ShowEmpty));
    }

    /// <summary>Строка лога — как строка своей консоли: тот же цвет по уровню, та же отсечка огромных строк.</summary>
    private static ConsoleLineViewModel Line(string text)
    {
        if (text.Length > ServerConsole.MaxLineLength) text = string.Concat(text.AsSpan(0, ServerConsole.MaxLineLength), "…");
        return new ConsoleLineViewModel(new ConsoleLine(0, default, ConsoleLineKind.Output, text));
    }

    private static IEnumerable<string> Tail(IReadOnlyList<string> lines, int count) => lines.Skip(Math.Max(0, lines.Count - count));

    /// <summary>Плитки: состояние с пометкой «по логу», время работы, игроки.</summary>
    private void ShowState()
    {
        (StateText, StateTone) = State switch
        {
            HostedServerState.Starting => (Loc.T("server.stateStarting"), RowTone.Update),
            HostedServerState.Running => (Loc.T("server.stateRunning"), RowTone.Good),
            HostedServerState.Stopping => (Loc.T("server.stateStopping"), RowTone.Warn),
            HostedServerState.Stopped => (Loc.T("server.stateStopped"), RowTone.Muted),
            HostedServerState.Crashed => (Loc.T("server.hostedStateCrashed"), RowTone.Danger),
            _ => (Loc.T("server.hostedStateUnknown"), RowTone.Muted),
        };
        StateNote = _updatedAt is { } at ? Loc.T("server.hostedByLog", Dates.ClockSeconds(at))
            : _logs is not null ? Loc.T("server.hostedReadingLog")
            : "";

        var up = State is HostedServerState.Starting or HostedServerState.Running or HostedServerState.Stopping;
        UptimeText = up && _logs?.RunStarted is { } started ? Dates.Uptime(DateTime.Now - _logs.ToLocal(started)) : "—";
        PlayersText = up ? _online.Count.ToString() : "—";
        PlayerNames = !up ? "" : _online.Count == 0 ? Loc.T("server.playersNone") : string.Join(" · ", _online.Select(p => p.Name));
        PlayersEmptyText = up ? Loc.T("server.playersNone")
            : State is HostedServerState.Stopped or HostedServerState.Crashed ? Loc.T("server.hostedPlayersDown")
            : "—";
        Players.Clear();
        if (_logs is { } logs)
            foreach (var p in _online)
            {
                var since = logs.ToLocal(p.Since);
                Players.Add(new HostedPlayerRow(p.Name, Loc.T("server.hostedPlayerSince", Dates.Clock(since), Dates.Uptime(DateTime.Now - since))));
            }
        OnPropertyChanged(nameof(HasPlayers));
    }

    private static string ErrorText(Exception ex) => IsHopeless(ex)
        ? Loc.T("server.hostedNoRetry", Describe(ex))
        : Loc.T("server.hostedRetry", Describe(ex), (int)RetryInterval.TotalSeconds);

    /// <summary>
    /// Ошибка, которую повтор не вылечит: неверный логин или пароль, недоверенный сертификат или ключ сервера, протокол,
    /// который программа не умеет, беда с файлом ключа SFTP. Это правят в «Настройках». Тем же правилом сторож оповещений
    /// бросает опрос (<see cref="HostedWatch"/>).
    /// </summary>
    internal static bool IsHopeless(Exception ex) =>
        ex is RemoteFilesException
        {
            Kind: RemoteFailure.Auth or RemoteFailure.CertificateUntrusted or RemoteFailure.Unsupported or RemoteFailure.KeyFile
                or RemoteFailure.KeyPassphrase,
        };

    /// <summary>
    /// Что случилось — законченным предложением: за ним в строке идёт «попробую снова…». Сертификату и ключу сервера
    /// здесь доверить нельзя (это делают в «Настройках»), поэтому совет «доверяй, только если…» не к месту — свой текст.
    /// Так же говорят об ошибках копии мира (<see cref="HostedWorldText"/>).
    /// </summary>
    internal static string Describe(Exception ex)
    {
        if (ex is RemoteFilesException { Kind: RemoteFailure.CertificateUntrusted, IsHostKey: true } hostKey)
            return hostKey.HostKeyChanged ? Loc.T("server.hostedHostKeyChanged") : Loc.T("server.hostedHostKeyUntrusted");
        if (ex is RemoteFilesException { Kind: RemoteFailure.CertificateUntrusted }) return Loc.T("server.hostedCertUntrusted");
        var text = HostedSecret.Describe(ex).TrimEnd();
        return text.Length == 0 || text[^1] is '.' or '!' or '?' or '…' ? text : text + ".";
    }

    // ---------- прошлые запуски

    private async Task LoadRunsAsync(HostedLogs logs, int generation, CancellationToken ct)
    {
        IReadOnlyList<HostedRun> runs;
        try
        {
            runs = await logs.RunsAsync(ct);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // сбой сети — попробуем со следующим опросом; другое (нет прав на папку и т. п.) повтор не вылечит, и список
            // подождёт перезапуска сервера или смены профиля. Консоль это не задевает
            if (generation == _generation) _runsAgain = !ct.IsCancellationRequested && IsNetwork(ex);
            return;
        }
        if (generation != _generation) return;
        _runsAgain = false;
        FillRuns(logs, runs, SelectedRun?.Run?.Name);
    }

    private void FillRuns(HostedLogs logs, IReadOnlyList<HostedRun> runs, string? chosen)
    {
        _quietSelection = true;
        Runs.Clear();
        Runs.Add(new HostedRunOption(null, Loc.T("server.hostedRunCurrent")));
        foreach (var run in runs) Runs.Add(new HostedRunOption(run, Dates.Relative(logs.ToLocal(run.Started))));
        SelectedRun = Runs.FirstOrDefault(r => chosen is not null && r.Run?.Name == chosen) ?? Runs[0];
        _quietSelection = false;
        OnPropertyChanged(nameof(HasRuns));
        if (SelectedRun.Run is null && IsPast) BackToCurrent(); // открытый прошлый запуск пропал из архива
    }

    partial void OnSelectedRunChanged(HostedRunOption? value)
    {
        if (_quietSelection || value is null) return;
        if (value.Run is null) BackToCurrent();
        else _ = ShowPastAsync(value.Run);
    }

    private void BackToCurrent()
    {
        _runRequest++;
        _past = null;
        RunNote = "";
        IsPast = false;
        ShowCurrent();
    }

    /// <summary>Прошлый запуск — по требованию: его server-main.log целиком (большой — конец) и чем он кончился.</summary>
    private async Task ShowPastAsync(HostedRun run)
    {
        if (_logs is not { } logs) return;
        var request = ++_runRequest;
        var ct = _cts.Token;
        IsPast = true;
        _past = null;
        Lines.Clear();
        RunNote = Loc.T("server.hostedRunReading");
        try
        {
            var log = await logs.ReadRunAsync(run, ct);
            if (request != _runRequest) return; // пока читали, выбрали другой запуск
            _past = log;
            foreach (var line in Tail(log.Lines, PastMaxLines)) Lines.Add(Line(line));
            RunNote = Outcome(log);
            LinesAppended?.Invoke(true);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (request == _runRequest && !ct.IsCancellationRequested) RunNote = Loc.T("server.hostedRunFailed", Describe(ex));
        }
    }

    private static string Outcome(HostedRunLog log)
    {
        var text = log.Outcome switch
        {
            HostedServerState.Crashed => Loc.T("server.hostedRunCrashed"),
            HostedServerState.Stopped => Loc.T("server.hostedRunStopped"),
            _ => Loc.T("server.hostedRunCut"),
        };
        return log.Truncated || log.Lines.Count > PastMaxLines ? text + " " + Loc.T("server.hostedRunTruncated") : text;
    }

    // ---------- вылеты

    /// <summary>
    /// Вылет нашёлся — то же окно, что у своего сервера (с виновником, если нашёлся; выключить его можно прямо из окна).
    /// Каждый вылет — один раз за окно; случившийся до открытия окна не показываем. Ищем только пока идёт опрос, то есть
    /// пока раздел открыт.
    /// </summary>
    private async Task CheckCrashAsync(HostedLogs logs, GameProfile profile, int generation, CancellationToken ct)
    {
        HostedCrash? crash;
        try
        {
            crash = await logs.FindCrashAsync(ct);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (generation == _generation) _crashAgain = !ct.IsCancellationRequested && IsNetwork(ex); // найдётся при следующем опросе
            return;
        }
        if (generation == _generation) _crashAgain = false;
        // профиль могли сменить, пока искали, — вылет всё равно настоящий: логи профиля второй раз его не отдадут
        if (crash is not null) ShowCrash(profile, crash);
    }

    /// <summary>Окно вылета — один раз на вылет; случившийся до открытия окна не показываем.</summary>
    private void ShowCrash(GameProfile profile, HostedCrash crash)
    {
        if (!_seenCrashes.Add(crash.Info.Id) || crash.Info.At < CrashesSince) return;
        AppWindows.Current.ShowServerCrash(_main, profile, crash.Info);
    }

    // ---------- обновления модов перед запуском

    /// <summary>Какой давности проверке обновлений плашка ещё верит; старше — проверить заново.</summary>
    public static readonly TimeSpan UpdatesFreshFor = TimeSpan.FromMinutes(30);

    /// <summary>Проверка не удалась — следующая не раньше чем через столько: опрос идёт раз в 3 с.</summary>
    public static readonly TimeSpan UpdatesRetry = TimeSpan.FromMinutes(5);

    /// <summary>Проверки обновлений — общие со сторожем оповещений (<see cref="MainViewModel.HostedUpdates"/>); тесты ставят свои.</summary>
    public HostedModUpdates? Updates
    {
        get => _updates ?? _main?.HostedUpdates;
        set
        {
            _updates = value;
            WatchUpdates();
        }
    }

    /// <summary>Пункт очереди для обновления — релиз модбазы. Тесты ставят сохранённую копию, чтобы не скачивать.</summary>
    internal Func<ModTarget, ModCheckResult, UpdateQueueItem> QueueItem { get; set; } = (target, r) =>
        new UpdateQueueItem(target, r.Local.Info!.ModId, ModUpdatesDue.Name(r), r.Local.Info.Version ?? "", r.LatestCompatible!.ModVersion ?? "?",
            r.LatestCompatible, null);

    /// <summary>
    /// «Обновить всё» очередью «Моих модов»: пункты очереди, которые ставят эти обновления (мод, уже стоявший в очереди,
    /// — тем пунктом); пусто — не начали (отказались, вкладка занята другим).
    /// </summary>
    internal Func<ModTarget, IReadOnlyList<UpdateQueueItem>, Task<IReadOnlyList<UpdateQueueItem>>>? RunUpdates
    {
        get => _runUpdates ?? (_main is { } main ? (target, items) => main.Mods.UpdateAllAsync(target, items) : null);
        set => _runUpdates = value;
    }

    /// <summary>Сколько обновлений модов известно (последняя проверка).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdates), nameof(UpdatesText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    private int _updatesCount;

    /// <summary>Обновления ставятся: на плашке — ход, кнопка недоступна.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdates))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    private bool _isUpdating;

    [ObservableProperty] private double _updatesProgress;
    [ObservableProperty] private string _updatesProgressText = "";

    /// <summary>Итог «Обновить всё»: «Установлено: 3, ошибок: 0 · Готово. Запусти сервер в панели хостера…».</summary>
    [ObservableProperty] private string _updatesNote = "";

    /// <summary>Плашка «Обновления модов: N»: сервер по логу остановлен или вылетел, а обновления есть — или как раз ставятся.</summary>
    public bool ShowUpdates => IsUpdating || UpdatesCount > 0 && HostedLogs.IsSurelyDown(State);

    public string UpdatesText => Loc.T("server.hostedUpdates", UpdatesCount);

    partial void OnStateChanged(HostedServerState value)
    {
        OnPropertyChanged(nameof(ShowUpdates));
        UpdateAllCommand.NotifyCanExecuteChanged();
        if (!HostedLogs.IsSurelyDown(value) && !IsUpdating) UpdatesNote = ""; // сервер запустили — «запусти сервер» уже не к месту
    }

    /// <summary>Подписаться на перемены проверок обновлений (моды поменяли — прежняя проверка устарела).</summary>
    private void WatchUpdates()
    {
        if (Updates is not { } updates || ReferenceEquals(updates, _updatesWatched)) return;
        if (_updatesWatched is { } old) old.Changed -= OnUpdatesChanged;
        updates.Changed += OnUpdatesChanged;
        _updatesWatched = updates;
    }

    private void OnUpdatesChanged(string profileId)
    {
        if (_profile?.Id != profileId) return;
        _updatesTried = null;
        if (Updates?.Known(profileId) is { } found) UpdatesCount = found.Due.Count;
        else _ = RefreshUpdatesAsync(); // моды поменяли — что осталось, узнаем заново
    }

    /// <summary>
    /// После опроса: сервер по логу остановлен или вылетел — обновления модов должны быть известны. Есть свежая проверка
    /// (общая со сторожем оповещений) — по ней; нет — проверить: скан по FTP и модбаза, без скачивания.
    /// </summary>
    private async Task RefreshUpdatesAsync()
    {
        if (!HostedLogs.IsSurelyDown(State) || IsUpdating || _profile is not { } profile || Updates is not { } updates) return;
        if (updates.Fresh(profile.Id, UpdatesFreshFor) is { } fresh)
        {
            UpdatesCount = fresh.Due.Count;
            return;
        }
        var now = DateTime.UtcNow;
        if (_updatesTried is { } tried && now - tried < UpdatesRetry) return;
        if (ModTarget.For(profile) is not { IsHosted: true } target) return;
        _updatesTried = now;
        var generation = _generation;
        try
        {
            var found = await updates.CheckAsync(target, _cts.Token);
            if (generation != _generation) return;
            UpdatesCount = found.Due.Count;
            _updatesTried = null; // пауза — только после неудачи: устаревшую проверку повторяем сразу
        }
        catch (Exception ex) when (IsExpected(ex) || ex is HttpRequestException or Newtonsoft.Json.JsonException)
        {
            // модбаза или FTP не ответили — плашки нет, попробуем позже
        }
    }

    private bool CanUpdateAll() => !IsUpdating && UpdatesCount > 0 && HostedLogs.IsSurelyDown(State);

    /// <summary>
    /// «Обновить всё» — та же операция, что в «Моих модах»: вопрос со списком, очередь (копии прежних версий, журнал,
    /// история), итог «Готово. Запусти сервер в панели хостера». Вопрос «останови сервер» не задаётся: проверка по логу
    /// перед очередью видит, что он остановлен. Пока ставится — ход на плашке и кнопка недоступна; поставилось —
    /// проверка устаревает, и плашка показывает, что осталось (обычно — ничего, и она исчезает).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllAsync()
    {
        if (_profile is not { } profile || Updates?.Known(profile.Id) is not { Due.Count: > 0 } found || RunUpdates is not { } run
            || ModTarget.For(profile) is not { IsHosted: true } target) return;
        var items = found.Due.Select(r => QueueItem(target, r)).ToList();
        UpdatesNote = "";
        IsUpdating = true;
        Track(items);
        var queued = await run(target, items);
        if (!ReferenceEquals(_updating, items)) return; // пока спрашивали, профиль сменили — плашку уже сбросили
        if (queued.Count == 0)
        {
            FinishUpdates(items, report: false); // отказались или вкладка занята — ничего не начато
            return;
        }
        // мод, который уже ждал или ставился в очереди «Моих модов», второй раз не встаёт: следим за тем пунктом,
        // иначе свой ждал бы вечно и плашка не кончилась бы никогда
        var tracked = queued.ToList();
        Track(tracked);
        if (tracked.All(IsFinal)) FinishUpdates(tracked, report: true);
    }

    /// <summary>Следить за ходом этих пунктов (прежние — отпустить).</summary>
    private void Track(List<UpdateQueueItem> items)
    {
        if (_updating is { } old) foreach (var item in old) item.PropertyChanged -= OnUpdateItemChanged;
        _updating = items;
        foreach (var item in items) item.PropertyChanged += OnUpdateItemChanged;
        ShowUpdateProgress();
    }

    private static bool IsFinal(UpdateQueueItem item) => item.State is QueueState.Done or QueueState.Failed or QueueState.Cancelled;

    private void OnUpdateItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updating is not { } items || e.PropertyName is not (nameof(UpdateQueueItem.State) or nameof(UpdateQueueItem.Progress))) return;
        ShowUpdateProgress();
        if (items.All(IsFinal)) FinishUpdates(items, report: true);
    }

    /// <summary>Ход на плашке: «Установка модов · 1 из 3» и полоса — как у очереди «Моих модов».</summary>
    private void ShowUpdateProgress()
    {
        if (_updating is not { } items) return;
        var finished = items.Count(IsFinal);
        UpdatesProgressText = Loc.T("queue.titleRunning", finished, items.Count);
        UpdatesProgress = items.Count == 0 ? 0 : (finished + items.Where(i => i.IsWorking).Sum(i => i.Progress)) / items.Count;
    }

    private void FinishUpdates(List<UpdateQueueItem> items, bool report)
    {
        if (!ReferenceEquals(_updating, items)) return;
        foreach (var item in items) item.PropertyChanged -= OnUpdateItemChanged;
        _updating = null;
        var done = items.Count(i => i.State == QueueState.Done);
        if (report)
        {
            UpdatesNote = ModsViewModel.WithStartHint(Loc.T("queue.finished", done, items.Count(i => i.State == QueueState.Failed)), done > 0);
            // до свежей проверки (очередь устарит прежнюю, когда закончит) — без поставленных
            UpdatesCount = Math.Max(0, UpdatesCount - done);
        }
        IsUpdating = false;
        UpdatesProgressText = "";
        UpdatesProgress = 0;
    }

    /// <summary>«Копировать выделенные строки» в меню консоли — как у своей консоли.</summary>
    [RelayCommand]
    private void CopyLines(IList? selected)
    {
        if (ConsoleLineViewModel.SelectedText(Lines, selected) is { } text) Ui.Platform.SetClipboard(text);
    }

    /// <summary>Сбой сети: пройдёт сам, повторить стоит.</summary>
    private static bool IsNetwork(Exception ex) =>
        ex is TimeoutException or RemoteFilesException { Kind: RemoteFailure.Timeout or RemoteFailure.Connect };

    private static bool IsExpected(Exception ex) =>
        ex is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException
            or TimeoutException;
}
