using System.IO;
using System.Net.Http;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Notifications;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using Newtonsoft.Json;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Оповещения серверов у хостера. Агента там нет — оповещения шлёт само окно, пока оно открыто. Сторожит каждый
/// хостовый профиль, у которого во вкладке «Оповещения» включён хоть один маршрут: раз в минуту читает лог по FTP и раз
/// в 6 часов проверяет обновления модов. Лог — тот же <see cref="HostedLogs"/>, что у раздела «Сервер»
/// (<see cref="HostedLogsPool"/>): пока раздел открыт и сам читает лог раз в 3 с, сторож своих запросов не шлёт и берёт
/// события из тех же опросов (<see cref="HostedLogs.WatchAsync"/>) — ни одно не теряется и не приходит дважды. Событий
/// немного (<see cref="Events"/>): упал (с виновником), запущен, остановлен, игрок зашёл и вышел, перегружен, вышли
/// обновления модов, моды обновлены окном. Первый опрос после запуска окна или включения оповещений только запоминает,
/// что есть: вылет вчера, игроки на сервере и текущее состояние — не новость. Окон на машине может быть несколько, а
/// следит за профилем одно (<see cref="HostedWatchLease"/>). Всё, кроме отправки, — в потоке окна.
/// </summary>
/// <param name="profiles">Все профили окна (живой список: хостовые выбираются при каждом проходе).</param>
/// <param name="updates">Проверки обновлений модов — общие с плашкой «Обновить всё».</param>
/// <param name="time">Часы (тесты двигают их сами).</param>
/// <param name="agentsDir">Где лежат настройки оповещений; null — папка агентов этой машины.</param>
public sealed class HostedWatch(IEnumerable<ProfileViewModel> profiles, HostedModUpdates updates, TimeProvider? time = null,
    string? agentsDir = null)
{
    /// <summary>Как часто читать лог, когда раздел «Сервер» его не читает.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Пауза после первой ошибки сети; дальше — вдвое дольше, до <see cref="MaxRetry"/>.</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(15);

    /// <summary>Как часто проверять обновления модов.</summary>
    public static readonly TimeSpan ModsInterval = TimeSpan.FromHours(6);

    /// <summary>Первая проверка обновлений — не сразу после запуска окна, как у агента: окно могли открыть на минуту.</summary>
    public static readonly TimeSpan ModsFirstDelay = TimeSpan.FromMinutes(10);

    /// <summary>Проверка обновлений не удалась (нет связи, модбаза молчит) — следующая попытка через столько.</summary>
    public static readonly TimeSpan ModsRetry = TimeSpan.FromMinutes(30);

    /// <summary>
    /// События сервера у хостера. Нет: копии мира, места на диске, скорого перезапуска, «не запустился» (этого не видно
    /// по FTP) и чата в Discord (мост чата есть только у своего сервера).
    /// </summary>
    public static readonly IReadOnlyList<NotifyEvent> Events =
    [
        NotifyEvent.ServerCrashed, NotifyEvent.ServerStarted, NotifyEvent.ServerStopped, NotifyEvent.PlayerJoined,
        NotifyEvent.PlayerLeft, NotifyEvent.Overloaded, NotifyEvent.ModUpdates, NotifyEvent.ModsUpdated,
    ];

    // ради этих событий лог читается раз в минуту
    private static readonly NotifyEvent[] LogEvents =
    [
        NotifyEvent.ServerCrashed, NotifyEvent.ServerStarted, NotifyEvent.ServerStopped, NotifyEvent.PlayerJoined,
        NotifyEvent.PlayerLeft, NotifyEvent.Overloaded,
    ];

    // таймер окна тикает ровно раз в интервал, а опрос кончается чуть позже тика: без запаса читали бы через раз
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly HostedModUpdates _updates = updates;
    private readonly string? _agentsDir = agentsDir;
    private readonly Dictionary<string, ProfileWatch> _watches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _sending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (GameProfile Profile, List<string> Done, List<string> Failed)> _changed = new(StringComparer.Ordinal);
    private IUiTimer? _timer;
    private bool _ticking;

    /// <summary>Отправка в канал; null — настоящая. Тесты перехватывают сообщения здесь.</summary>
    internal Func<NotifyChannel, NotifyMessage, Task>? Send { get; set; }

    /// <summary>Оповещение в канал не ушло: профиль и текст для вкладки «Оповещения». Не повторяется — следующее событие уйдёт как обычно.</summary>
    public event Action<string, string>? SendFailed;

    /// <summary>За какими профилями следит (тесты).</summary>
    internal IReadOnlyCollection<string> Watched => _watches.Keys;

    /// <summary>Логи, на которые подписан сторож профиля (тесты); не следит — null.</summary>
    internal HostedLogs? LogsOf(string profileId) => _watches.GetValueOrDefault(profileId)?.Logs;

    /// <summary>Дождаться отправки уже поставленных оповещений профиля (тесты).</summary>
    internal Task SentAsync(string profileId) => _sending.GetValueOrDefault(profileId) ?? Task.CompletedTask;

    /// <summary>Все опросы разобраны, проход не идёт (тесты).</summary>
    internal bool IsIdle => !_ticking && _watches.Values.All(w => w.IsIdle);

    /// <summary>Включить проходы по таймеру; первый — сразу, как окно освободится.</summary>
    public void Start()
    {
        if (_timer is null)
        {
            _timer = Ui.Timer(Interval);
            _timer.Tick += (_, _) => _ = TickAsync();
        }
        _timer.Start();
        Ui.PostIdle(() => _ = TickAsync());
    }

    /// <summary>Настройки оповещений профиля поменяли — пересобрать список сразу, не дожидаясь таймера.</summary>
    public void Refresh() => _ = TickAsync();

    /// <summary>
    /// Один проход: кого сторожить (хостовый профиль с включённым маршрутом), а кого отпустить (удалён, не у хостера,
    /// маршруты выключены — без висящих подписок и замков); тем, кому пора, — опрос лога и проверка обновлений модов.
    /// Пока предыдущий проход не кончился, новый не начинается.
    /// </summary>
    public async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            var wanted = new Dictionary<string, (GameProfile Profile, ServerNotifySettings Settings)>(StringComparer.Ordinal);
            foreach (var profile in profiles.Select(p => p.Model).Where(p => p.IsHosted).ToList())
            {
                var settings = ServerNotifySettings.Load(profile.Id, _agentsDir);
                if (settings.Routes.Values.Any(v => v.Count > 0)) wanted[profile.Id] = (profile, settings);
            }
            foreach (var id in _watches.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                _watches[id].Dispose();
                _watches.Remove(id);
            }
            foreach (var (id, (profile, settings)) in wanted)
            {
                if (!_watches.TryGetValue(id, out var watch)) _watches[id] = watch = new ProfileWatch(this, profile);
                await watch.TickAsync(profile, settings);
            }
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <summary>
    /// Перестать следить за всеми: таймер, подписки на логи и замки. Окну это не нужно — замки отпускает система вместе
    /// с процессом; нужно тестам, которые «перезапускают окно».
    /// </summary>
    public void Stop()
    {
        _timer?.Stop();
        foreach (var watch in _watches.Values) watch.Dispose();
        _watches.Clear();
    }

    // ---------- «моды обновлены»: окно само поставило моды на сервер у хостера

    /// <summary>
    /// Окно поставило моды на сервер у хостера (обновление, установка, откат) или не смогло: копится до конца операции,
    /// чтобы «Обновить всё» на пять модов было одним оповещением, а не пятью.
    /// </summary>
    public void ModsChanged(ModTarget target, IEnumerable<string> done, IEnumerable<string> failed)
    {
        if (!target.IsHosted) return;
        if (!_changed.TryGetValue(target.ProfileId, out var change)) _changed[target.ProfileId] = change = (target.Profile, [], []);
        change.Done.AddRange(done);
        change.Failed.AddRange(failed);
    }

    /// <summary>
    /// Операция с модами закончилась: накопленное — оповещением «Обновлены моды» со списком, как у агента (если оно
    /// включено у сервера). Прежняя проверка обновлений профиля — устарела.
    /// </summary>
    public void ModsChangeDone()
    {
        foreach (var (id, change) in _changed)
        {
            _updates.Forget(id);
            if (change.Done.Count + change.Failed.Count == 0) continue;
            var profile = profiles.Select(p => p.Model).FirstOrDefault(p => p.Id == id) ?? change.Profile;
            // значок — у заголовка, сами моды — простым списком (значки на каждой строке рябили бы)
            Notify(profile, NotifyEvent.ModsUpdated, Loc.T("notify.ev.modsUpdated", change.Done.Count),
                [.. change.Done.Select(l => new NotifyLine("• " + l)), .. change.Failed.Select(l => new NotifyLine("✗ " + l))]);
        }
        _changed.Clear();
    }

    // ---------- отправка

    /// <summary>Строка оповещения: значок, подпись и текст — как у агента.</summary>
    private static NotifyLine Line(string icon, string label, string text) => new(icon, label, text);

    /// <summary>
    /// Отправить, если событие у сервера включено. Настройки — с диска на момент события; имя сервера в заголовке —
    /// нынешнее имя профиля. Оповещения одного сервера уходят по очереди, в том порядке, в каком случились; ошибка канала
    /// не повторяется и не роняет окно — она приходит в <see cref="SendFailed"/>.
    /// </summary>
    private void Notify(GameProfile profile, NotifyEvent e, string title, NotifyLine[] details)
    {
        var settings = ServerNotifySettings.Load(profile.Id, _agentsDir) with { ServerName = profile.Name };
        if (!settings.ChannelsFor(e).Any()) return;
        var id = profile.Id;
        var notifier = new ServerNotifier(() => settings, NotifyChannelsViewModel.Http,
            (channel, error) => Ui.Post(() => SendFailed?.Invoke(id, Loc.T("notify.sendFailed", channel, error))), Send);
        var previous = _sending.GetValueOrDefault(id) ?? Task.CompletedTask;
        _sending[id] = previous.ContinueWith(_ => notifier.NotifyAsync(e, title, details), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Ошибка, которую повтор не вылечит: неверный пароль, недоверенный сертификат — правят в «Настройках».</summary>
    private static bool IsHopeless(Exception ex) => HostedServerViewModel.IsHopeless(ex);

    private static bool IsExpected(Exception ex) =>
        ex is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException
            or TimeoutException or HttpRequestException or JsonException;

    /// <summary>Сторож одного профиля: подписка на его логи, что уже известно о сервере и когда опрашивать.</summary>
    private sealed class ProfileWatch(HostedWatch owner, GameProfile profile) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Queue<HostedLogPoll?> _queue = new();
        private readonly HashSet<string> _crashes = new(StringComparer.Ordinal);    // вылеты (Id), о которых уже сказали или которые старые
        private readonly HashSet<string> _crashedRuns = new(StringComparer.Ordinal); // запуски, кончившиеся вылетом
        private GameProfile _profile = profile;
        private HostedWatchLease? _lease;
        private Action<HostedLogPoll>? _polled;
        private Action<HostedCrash>? _crashed;
        private bool _busy;
        private bool _processing;
        private bool _disposed;
        private bool _baseline;            // первый опрос после подписки был: дальше — только новое
        private HostedServerState _state;  // последнее известное состояние (не Unknown — лог мог пропасть на миг перезапуска)
        private string? _run;              // запуск, к которому оно относится
        private OverloadWatch _overloads = new();
        private DateTime _since;           // вылеты раньше этого (время этой машины) — старые
        private DateTime _lastPoll = DateTime.MinValue; // последний опрос лога, кто бы его ни сделал (UTC)
        private DateTime _retryAt = DateTime.MinValue;
        private TimeSpan _retry = TimeSpan.Zero;
        private bool _halted;              // ошибка, которую повтор не вылечит: ждём правки подключения
        private bool _crashAgain;          // поиск вылета сорвался из-за сети
        private string? _endPending;       // запуск кончился между опросами: упал он или остановлен — скажет поиск вылета
        private DateTime _modsNotBefore = owner.UtcNow + ModsFirstDelay;

        private string Id => _profile.Id;

        public HostedLogs? Logs { get; private set; }

        public bool IsIdle => !_busy && !_processing && _queue.Count == 0;

        public async Task TickAsync(GameProfile current, ServerNotifySettings settings)
        {
            if (_busy || _disposed) return;
            _busy = true;
            try
            {
                _profile = current; // имя могли поменять — оно в заголовках
                _lease ??= HostedWatchLease.TryTake(Id, owner._agentsDir);
                if (_lease is null) return; // за этим сервером уже следит другое окно eViSTool на этой машине
                // пароль не прочитать на этой машине — нечем входить; правят в «Настройках», проверим на следующем проходе
                if (ModTarget.For(_profile) is not { IsHosted: true } target)
                {
                    Detach();
                    return;
                }
                if (LogEvents.Any(e => settings.ChannelsFor(e).Any()))
                {
                    var logs = HostedLogsPool.For(target); // подключение поменяли — экземпляр новый: подписаться заново
                    if (!ReferenceEquals(logs, Logs)) await AttachAsync(logs);
                    if (Logs is { } attached && !_halted && owner.UtcNow >= _retryAt && owner.UtcNow - _lastPoll >= Interval - Slack)
                        await PollAsync(attached);
                    if (Logs is { } l && (_crashAgain || l.State == HostedServerState.Crashed && !l.CurrentCrashReported))
                        Enqueue(null); // поиск вылета — в общей очереди с разбором опросов
                }
                else Detach();
                if (settings.ChannelsFor(NotifyEvent.ModUpdates).Any()) await CheckModsAsync(target);
            }
            finally
            {
                _busy = false;
            }
        }

        // ---------- подписка на логи

        private async Task AttachAsync(HostedLogs logs)
        {
            Detach();
            Action<HostedLogPoll> polled = poll => Ui.Post(() => OnPolled(logs, poll));
            Action<HostedCrash> crashed = crash => Ui.Post(() => OnCrashed(logs, crash));
            // экземпляр запоминаем до подписки: опрос, начатый сразу после неё, уже наш
            (Logs, _polled, _crashed) = (logs, polled, crashed);
            _baseline = false;
            _state = HostedServerState.Unknown;
            _run = null;
            _overloads = new OverloadWatch();
            _since = owner._time.GetLocalNow().DateTime;
            _lastPoll = _retryAt = DateTime.MinValue;
            _retry = TimeSpan.Zero;
            _halted = _crashAgain = false;
            _endPending = null;
            try
            {
                await logs.WatchAsync(polled, crashed, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // сторож отпущен, пока ждали конца опроса
            }
            if (_disposed || !ReferenceEquals(Logs, logs)) logs.Unwatch(polled, crashed);
        }

        private void Detach()
        {
            if (Logs is { } logs && _polled is { } polled && _crashed is { } crashed) logs.Unwatch(polled, crashed);
            (Logs, _polled, _crashed) = (null, null, null);
            _queue.Clear();
        }

        /// <summary>Свой опрос — только если лог давно никто не читал. Сбой сети — тихий повтор с паузой побольше.</summary>
        private async Task PollAsync(HostedLogs logs)
        {
            try
            {
                await logs.RefreshAsync(_cts.Token); // строки придут в OnPolled, как от любого опроса
                _lastPoll = owner.UtcNow;
                _retry = TimeSpan.Zero;
                _retryAt = DateTime.MinValue;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                if (_disposed || _cts.IsCancellationRequested) return;
                // неверный пароль раз в минуту — тысячи неудачных входов в сутки, хостер может закрыть адрес
                if (IsHopeless(ex)) _halted = true;
                else
                {
                    _retry = _retry == TimeSpan.Zero ? FirstRetry : TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, MaxRetry.Ticks));
                    _retryAt = owner.UtcNow + _retry;
                }
            }
        }

        // ---------- разбор опросов

        private void OnPolled(HostedLogs logs, HostedLogPoll poll)
        {
            if (_disposed || !ReferenceEquals(logs, Logs)) return;
            _lastPoll = owner.UtcNow;
            Enqueue(poll);
        }

        /// <summary>Опрос (или поиск вылета — null) — в очередь: разбираются по одному, в порядке опросов.</summary>
        private void Enqueue(HostedLogPoll? poll)
        {
            _queue.Enqueue(poll);
            _ = ProcessAsync();
        }

        private async Task ProcessAsync()
        {
            if (_processing) return;
            _processing = true;
            try
            {
                while (!_disposed && _queue.TryDequeue(out var poll) && Logs is { } logs)
                {
                    if (poll is null) await CheckCrashAsync(logs);
                    else await HandleAsync(logs, poll);
                }
            }
            finally
            {
                _processing = false;
            }
        }

        private async Task HandleAsync(HostedLogs logs, HostedLogPoll poll)
        {
            if (!_baseline)
            {
                // первый опрос: что уже есть (состояние, игроки на сервере, давние предупреждения) — не новость
                _baseline = true;
                Remember(poll);
                return;
            }
            var before = _state;
            var beforeRun = _run;
            Remember(poll);

            if (poll.Restarted && before is HostedServerState.Starting or HostedServerState.Running or HostedServerState.Stopping)
            {
                // прошлый запуск кончился между опросами, и конца никто не видел: упал — скажет поиск вылета, иначе остановлен
                _endPending = beforeRun ?? "current";
                await CheckCrashAsync(logs);
            }
            // новый запуск начинается с чистого листа: «запущен» и после запуска, который тоже был запущен
            var from = poll.Restarted ? HostedServerState.Unknown : before;
            if (poll.State == HostedServerState.Running && from != HostedServerState.Running)
                Notify(NotifyEvent.ServerStarted, Loc.T("notify.ev.started"));
            foreach (var move in poll.Moves)
            {
                var online = Line("◦", Loc.T("notify.lbl.online"), move.Online.ToString());
                if (move.Joined) Notify(NotifyEvent.PlayerJoined, Loc.T("notify.ev.joined", move.Name), online);
                else Notify(NotifyEvent.PlayerLeft, Loc.T("notify.ev.left", move.Name), online);
            }
            // «Server overloaded» — по правилам агента (OverloadWatch): порог и «не чаще раза в час» — по времени строк
            foreach (var line in poll.Lines)
                if (_overloads.Add(line.Text, line.Time ?? owner._time.GetLocalNow().DateTime) is { } count)
                    Notify(NotifyEvent.Overloaded, Loc.T("notify.ev.overloaded"),
                        Line("◷", Loc.T("notify.lbl.warnings"), Loc.T("notify.ev.overloadCount", count, (int)OverloadWatch.Window.TotalMinutes)),
                        Line("⚑", Loc.T("notify.lbl.hint"), Loc.T("notify.ev.overloadHostedHint")));
            // упал — это не «остановлен»: о вылете скажет его разбор
            if (poll.State == HostedServerState.Stopped && from != HostedServerState.Stopped)
                Notify(NotifyEvent.ServerStopped, Loc.T("notify.ev.stopped"));
            if (poll.State == HostedServerState.Crashed && from != HostedServerState.Crashed)
                await CheckCrashAsync(logs);
        }

        /// <summary>Состояние и запуск после опроса. Лог пропал (сервер как раз запускается заново) — прежнее остаётся.</summary>
        private void Remember(HostedLogPoll poll)
        {
            if (poll.State == HostedServerState.Unknown) return;
            _state = poll.State;
            _run = poll.Run;
        }

        // ---------- вылеты

        /// <summary>
        /// Поиск вылета: текущего запуска или, если сервер уже запущен заново, прошлого. Найденный приходит в
        /// <see cref="OnCrashed"/> — от этого поиска или от раздела «Сервер», кто нашёл первым. Конец прошлого запуска,
        /// который никто не видел, решается здесь: вылета не было — «сервер остановлен».
        /// </summary>
        private async Task CheckCrashAsync(HostedLogs logs)
        {
            HostedCrash? crash;
            try
            {
                crash = await logs.FindCrashAsync(_cts.Token);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                _crashAgain = !_disposed && !_cts.IsCancellationRequested; // найдётся на следующем проходе
                return;
            }
            _crashAgain = false;
            if (crash is not null) OnCrashed(logs, crash); // обычно он уже пришёл событием — второй раз не скажем
            if (_endPending is { } run && ReferenceEquals(logs, Logs))
            {
                _endPending = null;
                // shortcut: архив прошлого запуска ещё переезжал (папка без лога) — вылет найдётся позже, а «остановлен»
                // уже ушёл. Переделать, если хостер будет переносить логи медленно
                if (!_crashedRuns.Contains(run)) Notify(NotifyEvent.ServerStopped, Loc.T("notify.ev.stopped"));
            }
        }

        private void OnCrashed(HostedLogs logs, HostedCrash crash)
        {
            if (_disposed || !ReferenceEquals(logs, Logs)) return;
            _crashedRuns.Add(crash.Run);
            // один раз; до запуска окна или включения оповещений — старый
            if (!_crashes.Add(crash.Info.Id) || crash.Info.At < _since) return;
            // как у агента, но без строки про сторожа: поднимет ли сервер хостер, отсюда не видно
            var why = crash.Finding.Culprit is { } culprit
                ? Loc.T("notify.ev.culprit", culprit.Mod?.Name ?? culprit.ModId, culprit.Version ?? "")
                : Loc.T("notify.ev.noCulprit");
            Notify(NotifyEvent.ServerCrashed, Loc.T("notify.ev.crashed"), Line("⚑", Loc.T("notify.lbl.mod"), why));
        }

        // ---------- обновления модов

        /// <summary>
        /// Раз в 6 часов (время прошлой проверки — в файле, переживает перезапуск окна): вышли обновления — список, как у
        /// агента; тот же список второй раз не приходит.
        /// </summary>
        private async Task CheckModsAsync(ModTarget target)
        {
            var now = owner.UtcNow;
            if (now < _modsNotBefore) return;
            var state = HostedNotifyState.Load(Id, owner._agentsDir);
            if (state.ModsCheckedAt is { } at && now - at < ModsInterval) return;
            HostedUpdatesFound found;
            try
            {
                found = await owner._updates.CheckAsync(target, _cts.Token);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                _modsNotBefore = owner.UtcNow + ModsRetry; // нет связи или модбаза молчит — не повод тревожить
                return;
            }
            if (_disposed) return;
            var list = found.Due.Select(ModUpdatesDue.Line).ToList();
            if (list.Count > 0 && !list.SequenceEqual(state.ModUpdates))
                Notify(NotifyEvent.ModUpdates, Loc.T("notify.ev.modUpdates", list.Count),
                    [.. list.Take(30).Select(l => new NotifyLine("• " + l)),
                     .. list.Count > 30 ? [new NotifyLine(Loc.T("notify.ev.andMore", list.Count - 30))] : Array.Empty<NotifyLine>(),
                     Line("⚑", Loc.T("notify.lbl.hint"), Loc.T("notify.ev.modUpdatesHostedHint"))]);
            try
            {
                new HostedNotifyState { ModsCheckedAt = now, ModUpdates = list }.Save(Id, owner._agentsDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _modsNotBefore = now + ModsInterval; // не записалось — хотя бы до перезапуска окна не повторим
            }
        }

        private void Notify(NotifyEvent e, string title, params NotifyLine[] details) => owner.Notify(_profile, e, title, details);

        public void Dispose()
        {
            _disposed = true;
            _cts.Cancel();
            Detach();
            _lease?.Dispose();
            _lease = null;
        }
    }

}
