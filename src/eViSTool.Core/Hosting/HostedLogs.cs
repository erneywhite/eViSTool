using System.Security.Cryptography;
using System.Text;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Mods;
using eViSTool.Core.Server;

namespace eViSTool.Core.Hosting;

/// <summary>Итог опроса лога сервера у хостера (<see cref="HostedLogs.PollAsync"/>).</summary>
/// <param name="State">Состояние сервера после опроса.</param>
/// <param name="Restarted">С прошлого опроса сервер запущен заново: прежние строки — прошлого запуска, показывать с чистого листа.</param>
/// <param name="NewLines">Новые целые строки текущего запуска; при первом опросе и после перезапуска — всё прочитанное.</param>
/// <param name="Players">Кто на сервере; время входа — время сервера.</param>
public sealed record HostedLogUpdate(HostedServerState State, bool Restarted, IReadOnlyList<string> NewLines,
    IReadOnlyList<OnlinePlayer> Players);

/// <summary>Прошлый запуск сервера — папка Logs/Archive/&lt;yyyy-MM-dd_HH_mm_ss&gt;.</summary>
/// <param name="Name">Имя папки.</param>
/// <param name="Path">Папка на FTP.</param>
/// <param name="Started">Начало запуска по имени папки — время сервера.</param>
/// <param name="Until">Начало следующего запуска (время сервера); null — не знаем.</param>
public sealed record HostedRun(string Name, string Path, DateTime Started, DateTime? Until);

/// <summary>server-main.log прошлого запуска, прочитанный по требованию.</summary>
/// <param name="Outcome">Чем кончился: Crashed, Stopped или Unknown — лог оборвался без остановки (сервер убили).</param>
/// <param name="Truncated">Лог больше <see cref="HostedLogs.RunReadLimit"/>: прочитан только его конец.</param>
/// <param name="CrashLog">server-crash.log этого запуска на FTP, если есть (сам не читается).</param>
public sealed record HostedRunLog(HostedRun Run, IReadOnlyList<string> Lines, HostedServerState Outcome, bool Truncated, string? CrashLog);

/// <summary>Вылет сервера у хостера — для окна вылетов.</summary>
/// <param name="Info">То же, что агент отдаёт окну про свой сервер. Id постоянный для запуска: окно узнает вылет и после
/// своего перезапуска; At — время этой машины.</param>
/// <param name="Finding">Разбор <see cref="CrashAnalyzer.AnalyzeServer"/>.</param>
/// <param name="Run">Запуск — имя его папки в архиве (время начала по часам сервера).</param>
/// <param name="CrashLog">server-crash.log на FTP, если был.</param>
/// <param name="Report">Его текст.</param>
/// <param name="Current">Вылет текущего запуска: сервер ещё не запускали заново.</param>
public sealed record HostedCrash(ServerCrashInfo Info, CrashFinding Finding, string Run, string? CrashLog, string? Report, bool Current);

/// <summary>
/// Лог сервера у хостера по FTP: опрос текущего запуска (листинг папки Logs и дочитывание нового хвоста
/// server-main.log), состояние сервера, игроки онлайн, прошлые запуски из Logs/Archive и поиск вылета. Окно держит
/// один экземпляр на профиль и опрашивает его по таймеру; опросы одного экземпляра идут по одному. Вся сеть — через
/// очередь соединения профиля (<see cref="HostedSessions"/>). server-debug.log не читается никогда.
/// <para>
/// Время: в строках лога и в именах папок архива — местное время сервера (у G-Portal UTC+2); время файлов из MLSD —
/// UTC, из LIST — снова время сервера, но до минуты. Напрямую UTC и время сервера не сравниваются: сдвиг часов сервера
/// (<see cref="ServerUtcOffset"/>) оценивается по последней строке лога и времени его файла.
/// </para>
/// </summary>
public sealed class HostedLogs
{
    public const string MainLog = "server-main.log";
    public const string CrashLog = "server-crash.log";
    public const string ArchiveDir = "Archive";

    /// <summary>Сколько читать при первом опросе и после перезапуска: лог запуска — 20–30 КБ, огромный читается с конца.</summary>
    public const int FirstReadLimit = 512 * 1024;

    /// <summary>Сколько читать из лога прошлого запуска; больше — только конец.</summary>
    public const int RunReadLimit = 8 * 1024 * 1024;

    /// <summary>Сколько читать из server-crash.log (с конца).</summary>
    public const int ReportLimit = 1024 * 1024;

    /// <summary>Остановка начата, а лог не растёт столько — сервер остановлен (убит посреди остановки).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Лог не менялся так долго по времени файла, что остановка точно закончилась, даже если окно только открыли и само
    /// роста не видело (иначе после запуска окна сервер, убитый посреди остановки, ещё 2 минуты «останавливался»).
    /// </summary>
    public static readonly TimeSpan LongStaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>Сколько ждать быстрой проверки (<see cref="CheckNowAsync"/>).</summary>
    public static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(5);

    // лог мог дорасти между листингом и чтением: просим с запасом, чтобы дочитать до конца файла — передачу, оборванную
    // посередине, G-Portal не прощает (управляющее соединение умирает)
    private const int Slack = 64 * 1024;
    private const int MaxLines = 20_000;
    private const int MaxPartial = 1024 * 1024;

    private readonly Lock _lock = new();               // то, что читает окно (State, Lines, …)
    private readonly SemaphoreSlim _gate = new(1, 1);  // опросы по одному: позиция чтения меняется только в Apply
    private readonly List<string> _lines = [];
    private readonly List<string> _pending = [];       // новые строки, ещё не отданные PollAsync (их могли прочитать проверки)
    private bool _pendingRestart;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal); // запуски, чей вылет уже отдан (под _lock: его читает окно)
    private readonly HashSet<string> _checked = new(StringComparer.Ordinal);  // папки архива, уже проверенные на вылет
    private readonly HashSet<string> _waited = new(StringComparer.Ordinal);   // запуски, чей недописанный отчёт уже ждали
    private bool _unnamedReported;    // вылет текущего запуска отдан под ключом «current»: начала запуска не видно
    private DateTime? _unnamedEnded;  // такой запуск кончился: время его последней строки — по нему узнаем его папку в архиве

    private string? _logsDir;
    private bool _known;            // лог текущего запуска уже читали: _offset что-то значит
    private bool _missing;          // при прошлом опросе лога не было
    private long _offset;           // докуда прочитан файл
    private byte[] _partial = [];   // недописанная последняя строка — байтами: UTF-8 мог разрезаться посреди буквы
    private bool _archiveSeen;
    private DateTime? _archiveStamp;
    private DateTime _lastGrowth;   // когда лог рос по нашим наблюдениям (UTC этой машины)
    private HostedRunMarks _marks = new();
    private RemoteEntry? _crash;    // server-crash.log из последнего листинга
    private bool _crashOfRun;       // и он записан во время текущего запуска
    private HostedServerState _state = HostedServerState.Unknown;
    private TimeSpan? _serverOffset;

    public HostedLogs(ModTarget target) =>
        Target = target.IsHosted ? target : throw new ArgumentException("not a hosted target", nameof(target));

    public ModTarget Target { get; }

    /// <summary>Часы этой машины (для тестов «лог давно не растёт»).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Игроки текущего запуска. Changed приходит из потока опроса.</summary>
    public PlayerTracker Players { get; } = new();

    public HostedServerState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>Строки текущего запуска (последние 20 000).</summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_lock) return [.. _lines]; }
    }

    /// <summary>Имя, под которым текущий запуск ляжет в архив («2026-10-17_13_27_08»); не знаем — null.</summary>
    public string? RunName
    {
        get { lock (_lock) return _known ? _marks.RunName : null; }
    }

    /// <summary>Начало текущего запуска — время сервера.</summary>
    public DateTime? RunStarted
    {
        get { lock (_lock) return _known ? _marks.Started : null; }
    }

    /// <summary>Часы сервера минус UTC (у G-Portal +2 ч); не знаем — null.</summary>
    public TimeSpan? ServerUtcOffset
    {
        get { lock (_lock) return _serverOffset; }
    }

    /// <summary>
    /// Вылет текущего запуска уже отдан <see cref="FindCrashAsync"/>: пока сервер не запущен заново, искать вылет снова
    /// незачем — поиск начинается с ещё одного листинга.
    /// </summary>
    public bool CurrentCrashReported
    {
        get { lock (_lock) return _known && _reported.Contains(_marks.RunName ?? "current"); }
    }

    /// <summary>Папка логов на FTP; ещё не искали — null.</summary>
    public string? LogsDir
    {
        get { lock (_lock) return _logsDir; }
    }

    /// <summary>
    /// Текущий запуск начался не позже этого (время сервера): его начало, а если лог читали с середины, — первая
    /// прочитанная строка. Лога сейчас нет (уехал в архив) или его ещё не читали — null.
    /// </summary>
    private DateTime? CurrentSince
    {
        get { lock (_lock) return _known && !_missing ? _marks.Started ?? _marks.FirstTime : null; }
    }

    private HostedTarget Hosted => Target.Hosted!;
    private string Id => Target.ProfileId;
    private string Root => HostedLayoutProbe.Root(Hosted.Settings);

    /// <summary>Сервер точно не работает — вопрос «останови сервер в панели хостера» можно не задавать.</summary>
    public static bool IsSurelyDown(HostedServerState state) => state is HostedServerState.Stopped or HostedServerState.Crashed;

    // ---------- текущий запуск

    /// <summary>
    /// Опрос: листинг Logs и новый хвост server-main.log с прошлой позиции до конца файла. Недописанная последняя строка
    /// ждёт следующего опроса. Файл стал меньше, пропадал или в Logs/Archive появилась папка — сервер запущен заново:
    /// <see cref="HostedLogUpdate.Restarted"/> и чтение с начала. Первое чтение — весь лог, но не больше
    /// <see cref="FirstReadLimit"/> с конца. Строки, прочитанные с прошлого вызова проверками (<see cref="CheckNowAsync"/>,
    /// <see cref="FindCrashAsync"/>), тоже приходят здесь. Сбой сети — <see cref="RemoteFilesException"/>, состояние
    /// остаётся прежним.
    /// </summary>
    public async Task<HostedLogUpdate> PollAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var update = await PollCoreAsync(ct).ConfigureAwait(false);
            lock (_lock)
            {
                update = update with { Restarted = _pendingRestart, NewLines = [.. _pending] };
                _pending.Clear();
                _pendingRestart = false;
            }
            return update;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Быстрая проверка перед вопросом «останови сервер в панели хостера»: один листинг и дочитать хвост, не дольше
    /// <paramref name="timeout"/> (по умолчанию <see cref="QuickTimeout"/>). Сбой сети или нехватка времени — Unknown:
    /// тогда вопрос задаётся как раньше. Не спрашивать можно только при <see cref="IsSurelyDown"/>.
    /// </summary>
    public async Task<HostedServerState> CheckNowAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? QuickTimeout);
        try
        {
            await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                // строки этого опроса остаются окну: их отдаст следующий PollAsync
                return (await PollCoreAsync(cts.Token).ConfigureAwait(false)).State;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return HostedServerState.Unknown;
        }
        catch (IOException)
        {
            return HostedServerState.Unknown; // RemoteFilesException тоже IOException
        }
    }

    private async Task<HostedLogUpdate> PollCoreAsync(CancellationToken ct)
    {
        // работа в очереди только читает поля и сеть; меняет их Apply — уже после, так повтор после обрыва безопасен
        var fetch = await HostedSessions.UseAsync(Id, Hosted.Endpoint, files => FetchAsync(files, ct), readOnly: true, ct).ConfigureAwait(false);
        return Apply(fetch);
    }

    /// <summary>Что принёс опрос: листинг папки логов и новые байты лога с позиции <paramref name="From"/>.</summary>
    /// <param name="KeepArchive">Перемену в Logs/Archive пока не запоминать: перезапуск ещё не виден целиком.</param>
    private sealed record Fetch(string LogsDir, RemoteEntry? Main, RemoteEntry? Archive, RemoteEntry? Crash, bool Restart, long From,
        byte[] Bytes, bool FromMiddle, bool KeepArchive = false);

    private async Task<Fetch> FetchAsync(IRemoteFiles files, CancellationToken ct)
    {
        var logsDir = await LogsDirAsync(files, ct).ConfigureAwait(false);
        var list = await new HostedListings(files, Root).ListAsync(logsDir, ct).ConfigureAwait(false);
        var main = list?.FirstOrDefault(e => !e.IsDirectory && e.Name == MainLog);
        var archive = list?.FirstOrDefault(e => e.IsDirectory && e.Name == ArchiveDir);
        var crash = list?.FirstOrDefault(e => !e.IsDirectory && e.Name == CrashLog);
        if (main is null) return new Fetch(logsDir, null, archive, crash, false, 0, [], false);

        // запуск заново: при старте сервер уносит прошлые логи в Archive/<время> и начинает лог с нуля
        var archived = _known && ArchiveChanged(archive);
        // папка в архиве уже есть, а лог в листинге старше её: сервер запускается и ещё не унёс прошлый лог. Это прежний
        // лог — перезапуск узнаем по новому, а перемену в архиве до тех пор не запоминаем. Время из LIST — до минуты:
        // в одну минуту с папкой прежний лог сочтётся новым, как и раньше
        var moving = archived && main.Modified is { } written && archive?.Modified is { } created && written < created;
        var restart = _known && (main.Size < _offset || _missing || archived && !moving);
        long from;
        var fromMiddle = false;
        if (!_known || restart)
        {
            from = Math.Max(0, main.Size - FirstReadLimit);
            fromMiddle = from > 0;
        }
        else if (main.Size - _offset > FirstReadLimit)
        {
            // shortcut: между опросами лог вырос больше предела (поток ошибок) — середина пропускается, вошедшие и
            // вышедшие в ней игроки не видны. Переделать, если такое будет встречаться
            from = main.Size - FirstReadLimit;
            fromMiddle = true;
        }
        else from = _offset;
        byte[] bytes;
        try
        {
            bytes = main.Size > from ? await ReadFromAsync(files, main, from, ct).ConfigureAwait(false) : [];
        }
        catch (RemoteFilesException ex) when (ex.Kind == RemoteFailure.NotFound)
        {
            // лог пропал между листингом и чтением — сервер как раз запускается заново: как будто лога нет
            return new Fetch(logsDir, null, archive, crash, false, 0, [], false);
        }
        return new Fetch(logsDir, main, archive, crash, restart, from, bytes, fromMiddle, KeepArchive: moving && !restart);
    }

    /// <summary>В Logs/Archive появилась папка (или сама Archive) — значит, сервер запускался.</summary>
    private bool ArchiveChanged(RemoteEntry? archive) =>
        archive is not null && (!_archiveSeen || archive.Modified is { } stamp && _archiveStamp is { } before && stamp != before);

    private HostedLogUpdate Apply(Fetch fetch)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var fresh = new List<(string Line, DateTime? Time)>();
        bool newRun, restarted;
        HostedServerState state;
        lock (_lock)
        {
            _logsDir = fetch.LogsDir;
            if (!fetch.KeepArchive)
            {
                _archiveSeen = fetch.Archive is not null;
                _archiveStamp = fetch.Archive?.Modified;
            }
            _crash = fetch.Crash;
            if (_unnamedReported && (fetch.Main is null ? _known : fetch.Restart))
            {
                // запуск, чей вылет отдан под «current», кончился (лог унесён в архив): его папку узнаем по времени
                _unnamedEnded = _marks.LastTime ?? DateTime.MaxValue;
                _unnamedReported = false;
            }
            if (fetch.Main is not { } main)
            {
                // лога нет: сервер ещё не запускали — или он прямо сейчас запускается заново (прошлый лог уезжает в архив)
                _missing = _known;
                _crashOfRun = false;
                _state = state = HostedServerState.Unknown;
                newRun = true;
                restarted = false;
            }
            else
            {
                restarted = fetch.Restart;
                newRun = !_known || restarted;
                if (newRun)
                {
                    _marks = new HostedRunMarks(fetch.FromMiddle);
                    _lines.Clear();
                    _partial = [];
                }
                else if (fetch.FromMiddle) _partial = []; // середину пропустили: недописанная строка уже не продолжится

                var lines = SplitLines(_partial, fetch.Bytes, skipFirst: fetch.FromMiddle, atStart: fetch.From == 0, out _partial);
                foreach (var line in lines) fresh.Add((line, _marks.Add(line)));
                _lines.AddRange(lines);
                if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
                if (restarted)
                {
                    // строки прошлого запуска, не отданные окну, уже не нужны: окно начнёт с чистого листа
                    _pending.Clear();
                    _pendingRestart = true;
                }
                _pending.AddRange(lines);
                if (_pending.Count > MaxLines) _pending.RemoveRange(0, _pending.Count - MaxLines);

                if (fetch.Bytes.Length > 0 || newRun) _lastGrowth = now;
                _offset = fetch.From + fetch.Bytes.Length;
                _known = true;
                _missing = false;
                _serverOffset = EstimateOffset(main.Modified, _marks.LastTime) ?? _serverOffset;
                // начала не видно (лог читали с середины) — запуск начался не позже первой прочитанной строки
                _crashOfRun = fetch.Crash is { } crash && IsOfRun(crash.Modified, _marks.Started ?? _marks.FirstTime, null, _serverOffset);
                _state = state = _crashOfRun ? HostedServerState.Crashed : _marks.State(IsStale(main, now));
            }
        }

        // игроки — вне замка: подписчик Changed может читать состояние
        if (newRun) Players.Reset();
        foreach (var (line, time) in fresh) Players.Process(line, time ?? default);
        if (IsSurelyDown(state)) Players.Reset(); // упал или остановлен — на нём никого, даже если «вышел» не дописалось
        return new HostedLogUpdate(state, restarted, [.. fresh.Select(f => f.Line)], Players.Players);
    }

    /// <summary>
    /// Лог давно не растёт: и по времени файла (MLSD — UTC), и по своим наблюдениям. Одним часам сервера не верим:
    /// часы этой машины могут убегать, а ошибка здесь — пропущенный вопрос «останови сервер».
    /// </summary>
    private bool IsStale(RemoteEntry main, DateTime now)
    {
        var written = main.Modified is { Kind: DateTimeKind.Utc } utc ? utc : DateTime.MinValue;
        if (written != DateTime.MinValue && now - written > LongStaleAfter) return true;
        return now - (written > _lastGrowth ? written : _lastGrowth) > StaleAfter;
    }

    // ---------- прошлые запуски

    /// <summary>Прошлые запуски из Logs/Archive — новые сверху. Папки с другими именами пропускаются; архива нет — пусто.</summary>
    public Task<IReadOnlyList<HostedRun>> RunsAsync(CancellationToken ct) =>
        HostedSessions.UseAsync(Id, Hosted.Endpoint, files => RunsCoreAsync(files, ct), readOnly: true, ct);

    /// <summary>
    /// server-main.log прошлого запуска целиком (больше <see cref="RunReadLimit"/> — конец) и чем запуск кончился.
    /// Остальные логи папки не читаются.
    /// </summary>
    public async Task<HostedRunLog> ReadRunAsync(HostedRun run, CancellationToken ct) =>
        (await HostedSessions.UseAsync(Id, Hosted.Endpoint, files => ReadRunCoreAsync(files, run, ct), readOnly: true, ct).ConfigureAwait(false)).Log;

    private async Task<IReadOnlyList<HostedRun>> RunsCoreAsync(IRemoteFiles files, CancellationToken ct)
    {
        var dir = RemotePath.Combine(await LogsDirAsync(files, ct).ConfigureAwait(false), ArchiveDir);
        var runs = new List<HostedRun>();
        foreach (var e in await new HostedListings(files, Root).ListAsync(dir, ct).ConfigureAwait(false) ?? [])
            if (e.IsDirectory && HostedRunMarks.TryParseRunName(e.Name, out var started))
                runs.Add(new HostedRun(e.Name, e.Path, started, null));
        runs.Sort((a, b) => b.Started.CompareTo(a.Started));
        // конец запуска — начало следующего; у самого свежего — начало текущего, если тот начался позже
        var until = RunStarted;
        for (var i = 0; i < runs.Count; i++)
        {
            if (until is { } u && u <= runs[i].Started) until = null;
            runs[i] = runs[i] with { Until = until };
            until = runs[i].Started;
        }
        return runs;
    }

    /// <summary>Прочитанный прошлый запуск: сам лог, его признаки, записи листинга и сдвиг часов сервера.</summary>
    private sealed record RunRead(HostedRunLog Log, HostedRunMarks Marks, RemoteEntry? Main, RemoteEntry? Crash, TimeSpan? Offset);

    private async Task<RunRead> ReadRunCoreAsync(IRemoteFiles files, HostedRun run, CancellationToken ct)
    {
        var ls = new HostedListings(files, Root);
        var entries = await ls.ListAsync(run.Path, ct).ConfigureAwait(false) ?? [];
        var main = entries.FirstOrDefault(e => !e.IsDirectory && e.Name == MainLog);
        var marks = new HostedRunMarks();
        var lines = new List<string>();
        var truncated = false;
        if (main is { Size: > 0 })
        {
            truncated = main.Size > RunReadLimit;
            var from = truncated ? main.Size - RunReadLimit : 0;
            lines = SplitLines([], await ReadFromAsync(files, main, from, ct).ConfigureAwait(false), skipFirst: truncated, atStart: from == 0, out var rest);
            if (rest.Length > 0) lines.Add(Encoding.UTF8.GetString(rest).TrimEnd('\r')); // запуск окончен: строка без перевода — тоже строка
            marks = new HostedRunMarks(truncated);
            foreach (var line in lines) marks.Add(line);
        }
        var offset = EstimateOffset(main?.Modified, marks.LastTime) ?? ServerUtcOffset;

        // отчёт о вылете — в папке запуска (уехал в архив вместе с логами) или ещё в Logs, если записан во время этого запуска.
        // Конец самого свежего запуска не знаем, когда текущий читали с середины: тогда он не позже первой прочитанной
        // строки текущего, иначе отчёт текущего запуска приписался бы этому
        var until = run.Until ?? (CurrentSince is { } since && since > run.Started ? since : null);
        var crash = entries.FirstOrDefault(e => !e.IsDirectory && e.Name == CrashLog);
        if (crash is null
            && (await ls.ListAsync(await LogsDirAsync(files, ct).ConfigureAwait(false), ct).ConfigureAwait(false))?
                .FirstOrDefault(e => !e.IsDirectory && e.Name == CrashLog) is { } loose
            && IsOfRun(loose.Modified, run.Started, until, offset))
            crash = loose;

        var outcome = marks.Crashed || crash is not null ? HostedServerState.Crashed
            : marks.Stopped || marks.StopBegun ? HostedServerState.Stopped
            : HostedServerState.Unknown;
        return new RunRead(new HostedRunLog(run, lines, outcome, truncated, crash?.Path), marks, main, crash, offset);
    }

    // ---------- вылеты

    /// <summary>
    /// Вылет для окна вылетов: текущего запуска (свежий server-crash.log или строки вылета), а если сервер уже запущен
    /// заново — самого свежего запуска из архива. Разбор — <see cref="CrashAnalyzer.AnalyzeServer"/>, моды — из
    /// <paramref name="mods"/> или последнего скана профиля (<see cref="HostedMods.LastScanMods"/>), по modid и меткам,
    /// без кода (файлов модов на этой машине нет). Один вылет отдаётся один раз; старый вылет при первом вызове окно
    /// отсекает само (по <see cref="ServerCrashInfo.At"/>, как у своего сервера). Звать, когда опрос показал Crashed
    /// или Restarted, — на каждом таком опросе: вылет, видный пока только по пустому отчёту, придёт со следующим вызовом,
    /// когда сервер допишет отчёт. Сбой сети — <see cref="RemoteFilesException"/>, вылет тогда найдётся в следующий раз.
    /// </summary>
    public async Task<HostedCrash?> FindCrashAsync(CancellationToken ct, IReadOnlyList<LocalMod>? mods = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var poll = await PollCoreAsync(ct).ConfigureAwait(false);
            var prints = ModFingerprints.WithoutCode(mods ?? HostedMods.LastScanMods(Id) ?? []);
            return poll.State == HostedServerState.Crashed
                ? await CurrentCrashAsync(prints, ct).ConfigureAwait(false)
                : await ArchivedCrashAsync(prints, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HostedCrash?> CurrentCrashAsync(ModFingerprints prints, CancellationToken ct)
    {
        string run;
        bool unnamed, byLines;
        RemoteEntry? crash;
        List<string> lines;
        DateTime? last;
        TimeSpan? offset;
        lock (_lock)
        {
            // shortcut: начала запуска не видно (огромный лог читали с конца) — один общий ключ «current». Два таких
            // вылета подряд: второй покажется только после перезапуска, из архива. Переделать, если встретятся
            unnamed = _marks.RunName is null;
            run = _marks.RunName ?? "current";
            byLines = _marks.Crashed;
            crash = _crashOfRun ? _crash : null;
            lines = [.. _lines];
            last = _marks.LastTime;
            offset = _serverOffset;
        }
        if (Reported(run)) return null;
        var report = crash is null ? null
            : await HostedSessions.UseAsync<string?>(Id, Hosted.Endpoint, files => ReadReportAsync(files, crash, ct), readOnly: true, ct).ConfigureAwait(false);
        // вылет виден только по отчёту, а он пуст или пропал — сервер, видимо, ещё пишет его: ждём один вызов. Строки
        // вылета в логе сервер пишет уже после отчёта, с ними ждать нечего
        if (crash is not null && report is null && !byLines && _waited.Add(run)) return null;
        var finding = CrashAnalyzer.AnalyzeServer(GameLog.Parse(lines), report, crashedExit: true, prints)!;
        var at = At(crash?.Modified ?? last, offset);
        lock (_lock)
        {
            _reported.Add(run);
            if (unnamed) _unnamedReported = true;
        }
        return Crash(finding, at, run, crash, report, current: true);
    }

    /// <param name="Read">null — этот запуск не читали: его вылет уже отдан или он уже проверен.</param>
    private sealed record ArchivedRead(HostedRun Run, RunRead? Read, string? Report);

    private async Task<HostedCrash?> ArchivedCrashAsync(ModFingerprints prints, CancellationToken ct)
    {
        var unnamedEnded = _unnamedEnded;
        var found = await HostedSessions.UseAsync<ArchivedRead?>(Id, Hosted.Endpoint, async files =>
        {
            var newest = (await RunsCoreAsync(files, ct).ConfigureAwait(false)).FirstOrDefault();
            if (newest is null) return null;
            if (_checked.Contains(newest.Name) || Reported(newest.Name) || newest.Started <= unnamedEnded)
                return new ArchivedRead(newest, null, null);
            var read = await ReadRunCoreAsync(files, newest, ct).ConfigureAwait(false);
            var report = read.Crash is { } crash ? await ReadReportAsync(files, crash, ct).ConfigureAwait(false) : null;
            return new ArchivedRead(newest, read, report);
        }, readOnly: true, ct).ConfigureAwait(false);
        if (found is null) return null;
        if (unnamedEnded is { } ended)
        {
            // запуск, чей вылет отдан под «current», начался не позже своей последней строки: это самый свежий в архиве.
            // Если самый свежий начался позже, тот уже старше его — и сюда больше не попадёт
            _unnamedEnded = null;
            if (found.Run.Started <= ended) MarkReported(found.Run.Name);
        }
        if (found.Read is not { } read) return null;

        // папка без лога — архив ещё переезжает (сервер как раз запускается): проверим в следующий раз
        if (read.Main is not null) _checked.Add(found.Run.Name);
        if (read.Log.Outcome != HostedServerState.Crashed) return null;
        var finding = CrashAnalyzer.AnalyzeServer(GameLog.Parse(read.Log.Lines), found.Report, crashedExit: true, prints)!;
        var at = At(read.Crash?.Modified ?? read.Marks.LastTime ?? found.Run.Started, read.Offset);
        MarkReported(found.Run.Name);
        return Crash(finding, at, found.Run.Name, read.Crash, found.Report, current: false);
    }

    private bool Reported(string run)
    {
        lock (_lock) return _reported.Contains(run);
    }

    private void MarkReported(string run)
    {
        lock (_lock) _reported.Add(run);
    }

    /// <summary>
    /// Последний отчёт из server-crash.log (читается не больше <see cref="ReportLimit"/> с конца); пропал или пуст — null.
    /// Каждый отчёт начинается строкой «Game Version: …»; если сервер дописывает вылеты в конец файла, нужен последний.
    /// </summary>
    private static async Task<string?> ReadReportAsync(IRemoteFiles files, RemoteEntry crash, CancellationToken ct)
    {
        string text;
        try
        {
            ReadOnlySpan<byte> bytes = await ReadFromAsync(files, crash, Math.Max(0, crash.Size - ReportLimit), ct).ConfigureAwait(false);
            text = Encoding.UTF8.GetString(bytes.StartsWith(Utf8Bom) ? bytes[3..] : bytes);
        }
        catch (RemoteFilesException ex) when (ex.Kind is RemoteFailure.NotFound or RemoteFailure.Denied)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        var last = text.LastIndexOf("\nGame Version:", StringComparison.Ordinal);
        return last < 0 ? text : text[(last + 1)..];
    }

    private HostedCrash Crash(CrashFinding finding, DateTime at, string run, RemoteEntry? crash, string? report, bool current) =>
        new(ServerCrashInfo.From(finding, at) with { Id = CrashId(run) }, finding, run, crash?.Path, report, current);

    /// <summary>Id вылета — постоянный для запуска профиля: окно узнает уже показанный вылет и после своего перезапуска.</summary>
    private string CrashId(string run) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Id + "|" + run)))[..32].ToLowerInvariant();

    /// <summary>Когда случился вылет — по времени этой машины: время файла из MLSD (UTC) или время сервера.</summary>
    private DateTime At(DateTime? when, TimeSpan? offset) => when switch
    {
        { Kind: DateTimeKind.Utc } utc => utc.ToLocalTime(),
        { } server => ToLocal(server, offset),
        null => Time.GetLocalNow().DateTime,
    };

    // ---------- время сервера

    /// <summary>Время сервера (строки лога, имена папок архива) → время этой машины.</summary>
    public DateTime ToLocal(DateTime serverTime) => ToLocal(serverTime, ServerUtcOffset);

    private static DateTime ToLocal(DateTime serverTime, TimeSpan? offset) =>
        offset is { } shift
            ? DateTime.SpecifyKind(serverTime - shift, DateTimeKind.Utc).ToLocalTime()
            // shortcut: пояс сервера неизвестен (LIST без MLSD, лог без отметок времени) — считаем его как у этой машины.
            // Переделать, если встретится такой хостер
            : DateTime.SpecifyKind(serverTime, DateTimeKind.Local);

    /// <summary>Сдвиг часов сервера от UTC: время последней строки лога минус время файла из MLSD, до четверти часа.</summary>
    private static TimeSpan? EstimateOffset(DateTime? modified, DateTime? lastLine)
    {
        if (modified is not { Kind: DateTimeKind.Utc } utc || lastLine is not { } last) return null;
        var quarter = TimeSpan.FromMinutes(15).Ticks;
        return TimeSpan.FromTicks((long)Math.Round((double)(last - utc).Ticks / quarter) * quarter);
    }

    /// <summary>
    /// Файл записан во время запуска, начатого в <paramref name="started"/> и (если известно) законченного к
    /// <paramref name="until"/>; запуск — по часам сервера. Время из MLSD (UTC) переводится сдвигом часов сервера, время
    /// из LIST уже по ним, но до минуты. Сдвига или начала не знаем — false.
    /// </summary>
    private static bool IsOfRun(DateTime? modified, DateTime? started, DateTime? until, TimeSpan? offset)
    {
        if (modified is not { } m || started is not { } from) return false;
        if (m.Kind == DateTimeKind.Utc)
        {
            if (offset is not { } shift) return false;
            var at = DateTime.SpecifyKind(m + shift, DateTimeKind.Unspecified);
            return at >= from && (until is not { } end || at < end);
        }
        // LIST: файл записан где-то в этой минуте — запуска, только если в нём вся минута. Отчёт прошлого запуска в минуту
        // старта (вылет и автоперезапуск за секунды) иначе сделал бы упавшим работающий сервер; ошибка в эту сторону —
        // лишь заданный вопрос «останови сервер»
        return m >= from && (until is not { } stop || m.AddMinutes(1) <= stop);
    }

    // ---------- чтение

    /// <summary>
    /// Папка логов — Logs в папке данных (там, где serverconfig.json; у G-Portal — корень). Папка данных — из
    /// закреплённого в профиле конфига, из раскладки последнего скана, а если их нет — раскладка ищется один раз.
    /// </summary>
    private async Task<string> LogsDirAsync(IRemoteFiles files, CancellationToken ct)
    {
        if (_logsDir is { } known) return known;
        var settings = Hosted.Settings;
        var root = Root;
        string? dataDir = null;
        if (!string.IsNullOrWhiteSpace(settings.ConfigPath))
        {
            try
            {
                dataDir = RemotePath.Parent(RemotePath.Combine(root, settings.ConfigPath));
            }
            catch (InvalidDataException)
            {
                // в настройках мусор («..») — найдём по раскладке
            }
        }
        if (dataDir is null && HostedMods.LastLayout(Id) is { } layout && layout.Root == root) dataDir = layout.DataDir ?? root;
        dataDir ??= (await HostedLayoutProbe.ReadAsync(files, settings, ct).ConfigureAwait(false)).Layout.DataDir ?? root;
        var dir = RemotePath.Combine(dataDir, "Logs");
        lock (_lock) _logsDir = dir;
        return dir;
    }

    /// <summary>Файл с позиции до конца. Сервер не читает с позиции — файл целиком, и от него хвост.</summary>
    private static async Task<byte[]> ReadFromAsync(IRemoteFiles files, RemoteEntry file, long from, CancellationToken ct)
    {
        if (files.Features.RangeReads || from == 0)
        {
            try
            {
                var count = (int)Math.Min(file.Size - from + Slack, int.MaxValue);
                return await files.ReadRangeAsync(file.Path, from, count, ct).ConfigureAwait(false);
            }
            catch (RemoteFilesException ex) when (ex.Kind == RemoteFailure.RangeUnsupported)
            {
                // REST нет — качаем целиком
            }
        }
        // shortcut: без REST лог качается целиком на каждом опросе, где он вырос. Переделать, если встретится хостер без
        // REST с большими логами
        using var buffer = new MemoryStream();
        await files.DownloadAsync(file.Path, buffer, null, ct).ConfigureAwait(false);
        return from >= buffer.Length ? [] : buffer.GetBuffer().AsSpan((int)from, (int)(buffer.Length - from)).ToArray();
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Целые строки из недописанного с прошлого раза и новых байтов. Перевод строки — «\n» (байт 0x0A не встречается
    /// внутри многобайтовой буквы UTF-8), «\r» отрезается. Хвост без «\n» — в <paramref name="rest"/>. С середины файла
    /// (<paramref name="skipFirst"/>) первая, обрезанная строка пропускается.
    /// </summary>
    private static List<string> SplitLines(byte[] partial, byte[] bytes, bool skipFirst, bool atStart, out byte[] rest)
    {
        byte[] data = partial.Length == 0 ? bytes : [.. partial, .. bytes];
        ReadOnlySpan<byte> span = data;
        if (atStart && partial.Length == 0 && span.StartsWith(Utf8Bom)) span = span[3..];
        if (skipFirst)
        {
            var first = span.IndexOf((byte)'\n');
            span = first < 0 ? ReadOnlySpan<byte>.Empty : span[(first + 1)..];
        }
        var lines = new List<string>();
        for (var nl = span.IndexOf((byte)'\n'); nl >= 0; nl = span.IndexOf((byte)'\n'))
        {
            lines.Add(Encoding.UTF8.GetString(span[..nl]).TrimEnd('\r'));
            span = span[(nl + 1)..];
        }
        if (span.Length > MaxPartial)
        {
            // shortcut: мегабайт без перевода строки — отдаём как есть, иначе копился бы без конца
            lines.Add(Encoding.UTF8.GetString(span).TrimEnd('\r'));
            span = ReadOnlySpan<byte>.Empty;
        }
        rest = span.ToArray();
        return lines;
    }
}
