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
/// обновления модов, моды обновлены окном, копия мира по расписанию не получилась. Первый опрос после запуска окна или
/// включения оповещений только запоминает, что есть: вылет вчера, игроки на сервере и текущее состояние — не новость.
/// Окон на машине может быть несколько, а следит за профилем одно (<see cref="HostedWatchLease"/>). Тот же таймер
/// скачивает копии мира по расписанию (HostedWatch.Backups.cs) — у всех хостовых профилей, где оно включено, с
/// маршрутами оповещений или без; пока копия профиля идёт, его лог и обновления модов сторож не читает. Всё, кроме
/// отправки, — в потоке окна.
/// </summary>
/// <param name="profiles">Все профили окна (живой список: хостовые выбираются при каждом проходе).</param>
/// <param name="updates">Проверки обновлений модов — общие с плашкой «Обновить всё».</param>
/// <param name="time">Часы (тесты двигают их сами).</param>
/// <param name="agentsDir">Где лежат настройки оповещений; null — папка агентов этой машины.</param>
/// <param name="jobs">Операции с копиями мира — общие с вкладкой «Копии»; null — копий по расписанию нет.</param>
public sealed partial class HostedWatch(IEnumerable<ProfileViewModel> profiles, HostedModUpdates updates, TimeProvider? time = null,
    string? agentsDir = null, HostedWorldJobs? jobs = null)
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
    /// События сервера у хостера. Копия мира — та, что окно скачивает по расписанию на этот компьютер. Нет: места на
    /// диске, скорого перезапуска, «не запустился» (этого не видно по FTP) и чата в Discord (мост чата есть только у
    /// своего сервера).
    /// </summary>
    public static readonly IReadOnlyList<NotifyEvent> Events =
    [
        NotifyEvent.ServerCrashed, NotifyEvent.ServerStarted, NotifyEvent.ServerStopped, NotifyEvent.PlayerJoined,
        NotifyEvent.PlayerLeft, NotifyEvent.Overloaded, NotifyEvent.ModUpdates, NotifyEvent.ModsUpdated, NotifyEvent.BackupFailed,
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
    private readonly HostedWorldJobs? _jobs = jobs;

    /// <summary>Профили окна — для копий по расписанию (HostedWatch.Backups.cs).</summary>
    private IEnumerable<ProfileViewModel> Profiles => profiles;
    private readonly Dictionary<string, ProfileWatch> _watches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _sending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (GameProfile Profile, List<string> Done, List<string> Failed)> _changed = new(StringComparer.Ordinal);
    private IUiTimer? _timer;
    private bool _ticking;
    private bool _again; // Refresh пришёл посреди прохода — пройти ещё раз, как только этот кончится

    /// <summary>Отправка в канал; null — настоящая. Тесты перехватывают сообщения здесь.</summary>
    internal Func<NotifyChannel, NotifyMessage, Task>? Send { get; set; }

    // профиль → канал → последняя неудачная отправка в него (только в потоке окна)
    private readonly Dictionary<string, Dictionary<string, HostedSendFailure>> _failures = new(StringComparer.Ordinal);

    /// <summary>
    /// Оповещение профиля в канал не ушло (id профиля). Не повторяется — следующее событие уйдёт как обычно, — но ошибка
    /// помнится (<see cref="SendFailures"/>), пока в этот канал не уйдёт удачное оповещение или проверочное.
    /// </summary>
    public event Action<string>? SendFailed;

    /// <summary>В канал профиля ушло оповещение после неудачи (id профиля): его ошибка забыта.</summary>
    public event Action<string>? SendRecovered;

    /// <summary>Неудачные отправки профиля, которые ещё в силе: по каналу — последняя.</summary>
    public IReadOnlyList<HostedSendFailure> SendFailures(string profileId) =>
        _failures.TryGetValue(profileId, out var byChannel) ? [.. byChannel.Values] : [];

    /// <summary>
    /// Забыть ошибки профиля, которые уже не про его каналы: канал поправили в «Настройках», убрали или выключили у
    /// сервера (решает вкладка «Оповещения» — она знает, что сейчас настроено).
    /// </summary>
    public void ForgetSendFailures(string profileId, Func<HostedSendFailure, bool> stale)
    {
        if (!_failures.TryGetValue(profileId, out var byChannel)) return;
        foreach (var id in byChannel.Where(kv => stale(kv.Value)).Select(kv => kv.Key).ToList()) byChannel.Remove(id);
        if (byChannel.Count == 0) _failures.Remove(profileId);
    }

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

    /// <summary>
    /// Профили или их оповещения поменяли: кого больше не сторожить (удалён, не у хостера, маршруты выключены) —
    /// отпустить сразу, даже посреди прохода (его опрос или проверка модов прерываются и ничего не шлют); новых —
    /// взять проходом сейчас или сразу после идущего, не дожидаясь таймера.
    /// </summary>
    public void Refresh()
    {
        Release(Wanted());
        if (_ticking) _again = true;
        else _ = TickAsync();
    }

    /// <summary>
    /// Один проход: кого сторожить (хостовый профиль с включённым маршрутом), а кого отпустить (удалён, не у хостера,
    /// маршруты выключены — без висящих подписок и замков); тем, кому пора, — опрос лога и проверка обновлений модов;
    /// затем — копия мира по расписанию, если её пора скачать. Пока предыдущий проход не кончился, новый по таймеру не
    /// начинается.
    /// </summary>
    public async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            do
            {
                _again = false;
                var wanted = Wanted();
                Release(wanted);
                foreach (var id in wanted.Keys)
                {
                    // пока сторожили прежний профиль, этот могли удалить или выключить ему оповещения — берём, что сейчас
                    if (Want(id) is not { } want) continue;
                    var (profile, settings) = want;
                    if (!_watches.TryGetValue(id, out var watch)) _watches[id] = watch = new ProfileWatch(this, profile);
                    await watch.TickAsync(profile, settings);
                }
                await CheckBackupsAsync();
            }
            while (_again);
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <summary>Кого сторожить: хостовые профили, у которых включён хоть один маршрут.</summary>
    private Dictionary<string, (GameProfile Profile, ServerNotifySettings Settings)> Wanted()
    {
        var wanted = new Dictionary<string, (GameProfile Profile, ServerNotifySettings Settings)>(StringComparer.Ordinal);
        foreach (var profile in profiles.Select(p => p.Model).Where(p => p.IsHosted).ToList())
            if (Want(profile) is { } want) wanted[profile.Id] = want;
        return wanted;
    }

    private (GameProfile Profile, ServerNotifySettings Settings)? Want(string profileId) =>
        profiles.Select(p => p.Model).FirstOrDefault(p => p.Id == profileId && p.IsHosted) is { } profile ? Want(profile) : null;

    private (GameProfile Profile, ServerNotifySettings Settings)? Want(GameProfile profile)
    {
        var settings = ServerNotifySettings.Load(profile.Id, _agentsDir);
        return settings.Routes.Values.Any(v => v.Count > 0) ? (profile, settings) : null;
    }

    /// <summary>Отпустить сторожей тех, кого сторожить больше не нужно: подписки, замки, их опросы и проверки.</summary>
    private void Release(Dictionary<string, (GameProfile Profile, ServerNotifySettings Settings)> wanted)
    {
        foreach (var id in _watches.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            _watches[id].Dispose();
            _watches.Remove(id);
        }
        // профиль удалили, он не у хостера или маршруты выключены — его прежние ошибки отправки уже ни к чему
        // (открытая вкладка профиля узнаёт об этом и убирает их из строки ошибок)
        foreach (var id in _failures.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            _failures.Remove(id);
            SendRecovered?.Invoke(id);
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
    /// не повторяется и не роняет окно — она приходит в <see cref="SendFailed"/> и помнится до удачной отправки в канал.
    /// </summary>
    private void Notify(GameProfile profile, NotifyEvent e, string title, NotifyLine[] details)
    {
        var settings = ServerNotifySettings.Load(profile.Id, _agentsDir) with { ServerName = profile.Name };
        if (!settings.ChannelsFor(e).Any()) return;
        var id = profile.Id;
        // итог по каналам — в Tracked: там виден сам канал, а не только его имя
        var notifier = new ServerNotifier(() => settings, NotifyChannelsViewModel.Http, (_, _) => { }, Tracked(id, remember: true));
        var previous = _sending.GetValueOrDefault(id) ?? Task.CompletedTask;
        _sending[id] = previous.ContinueWith(_ => notifier.NotifyAsync(e, title, details), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Проверочное сервера у хостера — так же, как уйдут настоящие (этим окном, во все включённые каналы). Ответ — ошибки по
    /// каналам («Telegram: …»), пусто — всё ушло. Дошло до канала — его прежняя ошибка отправки забыта.
    /// </summary>
    public Task<IReadOnlyList<string>> TestAsync(string profileId, string title, params NotifyLine[] details) =>
        new ServerNotifier(() => ServerNotifySettings.Load(profileId, _agentsDir), NotifyChannelsViewModel.Http, (_, _) => { },
            Tracked(profileId, remember: false)).TestAsync(title, details);

    /// <summary>
    /// Отправка в канал с учётом итога (в пуле потоков; что запомнить — в поток окна). Ушло — прежняя ошибка канала забыта;
    /// не ушло и <paramref name="remember"/> — запомнена (проверочное свою ошибку показывает само). Ошибка идёт дальше,
    /// к <see cref="ServerNotifier"/>: он собирает их по каналам, как раньше.
    /// </summary>
    private Func<NotifyChannel, NotifyMessage, Task> Tracked(string profileId, bool remember) => async (channel, message) =>
    {
        try
        {
            await (Send?.Invoke(channel, message) ?? new Notifier(NotifyChannelsViewModel.Http).SendAsync(channel, message)).ConfigureAwait(false);
        }
        catch (NotifyException ex)
        {
            if (remember) Ui.Post(() => Failed(profileId, channel, ex.Message));
            throw;
        }
        Ui.Post(() => Recovered(profileId, channel.Id));
    };

    private void Failed(string profileId, NotifyChannel channel, string error)
    {
        if (!_failures.TryGetValue(profileId, out var byChannel)) _failures[profileId] = byChannel = new(StringComparer.Ordinal);
        byChannel[channel.Id] = new HostedSendFailure(channel, error);
        SendFailed?.Invoke(profileId);
    }

    private void Recovered(string profileId, string channelId)
    {
        if (!_failures.TryGetValue(profileId, out var byChannel) || !byChannel.Remove(channelId)) return;
        if (byChannel.Count == 0) _failures.Remove(profileId);
        SendRecovered?.Invoke(profileId);
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Ошибка, которую повтор не вылечит: неверный пароль, недоверенный сертификат — правят в «Профилях».</summary>
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
        private HostedEndpoint? _haltedOn; // ошибка, которую повтор не вылечит, — с этим подключением: ждём его правки или удачного чтения лога
        private bool _crashAgain;          // поиск вылета сорвался из-за сети
        private int _polls;                // опросы, пришедшие сторожу (считаются в потоке опроса)
        private int _pollsAtFailure;       // сколько их было к последнему сбою: паузу и отказ снимает только опрос после него
        private string? _endPending;       // запуск кончился между опросами: упал он или остановлен — скажет поиск вылета
        private DateTime _modsNotBefore = owner.UtcNow + ModsFirstDelay;

        private string Id => _profile.Id;

        public HostedLogs? Logs { get; private set; }

        public bool IsIdle => !_busy && !_processing && _queue.Count == 0;

        /// <summary>Можно в сеть: вход не отказан и пауза после сбоя прошла.</summary>
        private bool CanConnect => _haltedOn is null && owner.UtcNow >= _retryAt;

        public async Task TickAsync(GameProfile current, ServerNotifySettings settings)
        {
            if (_busy || _disposed) return;
            _busy = true;
            try
            {
                _profile = current; // имя могли поменять — оно в заголовках
                _lease ??= HostedWatchLease.TryTake(Id, owner._agentsDir);
                if (_lease is null) return; // за этим сервером уже следит другое окно eViSTool на этой машине
                // пароль не прочитать на этой машине — нечем входить; правят в «Профилях», проверим на следующем проходе
                if (ModTarget.For(_profile) is not { IsHosted: true } target)
                {
                    Detach();
                    return;
                }
                // подключение поправили (пароль, сертификат) — прежний отказ во входе уже не про него
                if (_haltedOn is { } halted && halted != target.Hosted!.Endpoint) _haltedOn = null;
                // копия мира этого сервера держит его соединение всю передачу: опрос ждал бы её конца и задержал бы весь
                // проход, а с ним — остальные серверы. Лог и обновления модов — после неё
                var busy = owner._jobs?.Running(Id) is not null;
                if (LogEvents.Any(e => settings.ChannelsFor(e).Any()))
                {
                    var logs = HostedLogsPool.For(target); // подключение поменяли — экземпляр новый: подписаться заново
                    if (!busy && !ReferenceEquals(logs, Logs)) await AttachAsync(logs); // подписка ждёт конца идущего опроса
                    var polled = !busy && Logs is { } attached && CanConnect && owner.UtcNow - _lastPoll >= Interval - Slack && await PollAsync(attached);
                    // поиск вылета — в общей очереди с разбором опросов. Сорвался из-за сети — повтор с той же паузой, что у
                    // опроса. Вылет текущего запуска ещё не отдан — ищем только после своего опроса: пока лог читает раздел
                    // «Сервер», вылет ищет он сам, а поиск сразу за поиском не оставил бы логам времени подождать
                    // недописанный отчёт (FindCrashAsync ждёт его один вызов); свой следующий опрос — через минуту
                    if (!_disposed && Logs is { } l && CanConnect
                        && (_crashAgain || polled && l.State == HostedServerState.Crashed && !l.CurrentCrashReported))
                        Enqueue(null);
                }
                else Detach();
                if (!_disposed && !busy && settings.ChannelsFor(NotifyEvent.ModUpdates).Any()) await CheckModsAsync(target);
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
            Action<HostedLogPoll> polled = poll =>
            {
                var number = Interlocked.Increment(ref _polls);
                Ui.Post(() => OnPolled(logs, poll, number));
            };
            Action<HostedCrash> crashed = crash => Ui.Post(() => OnCrashed(logs, crash));
            // экземпляр запоминаем до подписки: опрос, начатый сразу после неё, уже наш
            (Logs, _polled, _crashed) = (logs, polled, crashed);
            _baseline = false;
            _state = HostedServerState.Unknown;
            _run = null;
            _overloads = new OverloadWatch();
            _since = owner._time.GetLocalNow().DateTime;
            _lastPoll = DateTime.MinValue;
            Recovered();
            _crashAgain = false;
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

        /// <summary>Свой опрос — только если лог давно никто не читал. Удался — true; сбой — см. <see cref="Failed"/>.</summary>
        private async Task<bool> PollAsync(HostedLogs logs)
        {
            try
            {
                await logs.RefreshAsync(_cts.Token); // строки придут в OnPolled, как от любого опроса
                _lastPoll = owner.UtcNow;
                Recovered();
                return true;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                Failed(ex, logs.Target);
                return false;
            }
        }

        /// <summary>
        /// Сбой сети или входа — у опроса и у поиска вылета одно правило. Неверный пароль, недоверенный сертификат: в сеть
        /// больше не ходим, пока подключение не поправят или лог не прочитают удачно (раздел «Сервер»), — раз в минуту
        /// это тысячи неудачных входов в сутки, и хостер может закрыть адрес. Остальное — тихий повтор с паузой побольше.
        /// </summary>
        private void Failed(Exception ex, ModTarget target)
        {
            if (_disposed || _cts.IsCancellationRequested) return;
            // опрос внутри поиска вылета мог удаться до сбоя, а его разбор придёт позже — сбой он не отменяет
            _pollsAtFailure = Volatile.Read(ref _polls);
            if (IsHopeless(ex)) _haltedOn = target.Hosted!.Endpoint;
            else
            {
                _retry = _retry == TimeSpan.Zero ? FirstRetry : TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, MaxRetry.Ticks));
                _retryAt = owner.UtcNow + _retry;
            }
        }

        /// <summary>Лог прочитан: связь и вход в порядке — пауз и отказа во входе больше нет.</summary>
        private void Recovered()
        {
            _haltedOn = null;
            _retry = TimeSpan.Zero;
            _retryAt = DateTime.MinValue;
        }

        // ---------- разбор опросов

        /// <param name="number">Номер опроса у сторожа: по нему видно, был ли опрос после последнего сбоя.</param>
        private void OnPolled(HostedLogs logs, HostedLogPoll poll, int number)
        {
            if (_disposed || !ReferenceEquals(logs, Logs)) return;
            _lastPoll = owner.UtcNow;
            // лог прочитали после сбоя (кто угодно: раздел после обслуживания у хостера) — сторож снова может ходить сам
            if (number > _pollsAtFailure) Recovered();
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
            // упал — это не «остановлен»: о вылете скажет его разбор. Вылет ищет не этот разбор, а тот, кто опрашивал:
            // раздел «Сервер» — сам, сторож — после своего опроса (TickAsync)
            if (poll.State == HostedServerState.Stopped && from != HostedServerState.Stopped)
                Notify(NotifyEvent.ServerStopped, Loc.T("notify.ev.stopped"));
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
                Failed(ex, logs.Target);
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
        /// агента; тот же список второй раз не приходит. Пока опрос лога знает, что вход отказан, — не проверяем.
        /// </summary>
        private async Task CheckModsAsync(ModTarget target)
        {
            var now = owner.UtcNow;
            if (_haltedOn is not null || now < _modsNotBefore) return;
            var state = HostedNotifyState.Load(Id, owner._agentsDir);
            if (state.ModsCheckedAt is { } at && now - at < ModsInterval) return;
            HostedUpdatesFound found;
            try
            {
                found = await owner._updates.CheckAsync(target, _cts.Token);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                if (_disposed) return;
                // нет связи или модбаза молчит — не повод тревожить. Вход отказан — снова только в срок следующей проверки:
                // без опроса лога (включены одни моды) удачного чтения, которое сняло бы отказ, не будет
                _modsNotBefore = owner.UtcNow + (IsHopeless(ex) ? ModsInterval : ModsRetry);
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

/// <summary>
/// Неудачная отправка оповещения сервера у хостера: канал — каким он был настроен в ту минуту (поправят в «Настройках» —
/// это уже другой канал), и что ответил сервис.
/// </summary>
public sealed record HostedSendFailure(NotifyChannel Channel, string Error)
{
    /// <summary>«▸ Оповещение в «Telegram» не ушло: …» — на текущем языке.</summary>
    public string Text => Loc.T("notify.sendFailed", Channel.Name, Error);
}
