namespace eViSTool.Core.Diagnostics;

/// <summary>
/// Наблюдение за одним запуском игры: новые строки client-main.log и server-main.log (встроенный сервер одиночной
/// игры) с момента запуска; появился ли client-crash.log. Пока игра идёт — ловит закрытие мира с ошибкой
/// (<see cref="Poll"/>), после выхода — вылет (<see cref="Finish"/>). Каждое событие сообщается один раз.
/// </summary>
public sealed class GameSession
{
    private const int MaxLines = 50_000; // хвост лога, которого хватает для разбора (лог может пухнуть от ошибок)
    private const string DisconnectedMarker = "Exiting current game to disconnected screen";
    private const string MainMenuMarker = "Exiting current game to main menu";

    private readonly LogTail _client;
    private readonly LogTail _server;
    private readonly string _crashFile;
    private readonly DateTime _crashBaseline;
    private readonly List<string> _clientLines = [];
    private readonly List<string> _serverLines = [];
    // ошибки за весь запуск (для «Ошибок модов») — их не сбрасывает и закрытие мира; клиентский и серверный логи —
    // каждый своим сборщиком: склеенные, записи спутались бы на стыке
    private readonly ModErrorCollector _clientErrors = new();
    private readonly ModErrorCollector _serverErrors = new();

    /// <param name="dataDir">Папка данных профиля (там Logs).</param>
    /// <param name="startedUtc">Когда запущена игра: лог, начатый после этого, читается с начала, иначе — с текущего конца.</param>
    public GameSession(string dataDir, DateTime startedUtc)
    {
        var logs = Path.Combine(dataDir, "Logs");
        _client = Tail(Path.Combine(logs, "client-main.log"), startedUtc);
        _server = Tail(Path.Combine(logs, "server-main.log"), startedUtc);
        _crashFile = Path.Combine(logs, "client-crash.log");
        _crashBaseline = File.Exists(_crashFile) ? File.GetLastWriteTimeUtc(_crashFile) : DateTime.MinValue;
        StartedUtc = startedUtc;
    }

    public DateTime StartedUtc { get; }

    private static LogTail Tail(string path, DateTime startedUtc) =>
        // лог этого запуска (создан после старта игры) — целиком; лог прежнего — только новое
        new(path, fromStart: File.Exists(path) && File.GetCreationTimeUtc(path) >= startedUtc.AddSeconds(-5));

    /// <summary>Игра ещё идёт: закрылся ли мир с ошибкой с прошлой проверки. null — нет.</summary>
    public CrashFinding? Poll(Func<ModFingerprints> mods)
    {
        var fresh = Read();
        if (!fresh.Any(l => l.Contains(DisconnectedMarker, StringComparison.Ordinal) || l.Contains(MainMenuMarker, StringComparison.Ordinal)))
            return null;
        var finding = CrashAnalyzer.Analyze(GameLog.Parse(_clientLines), null, GameLog.Parse(_serverLines), mods());
        // мир закрыт — разобрали; следующий мир той же игры начинаем с чистого листа
        _clientLines.Clear();
        _serverLines.Clear();
        return finding;
    }

    /// <summary>Игра закрылась: был ли вылет (или закрытие мира, которое не успели заметить). null — обычный выход.</summary>
    public CrashFinding? Finish(Func<ModFingerprints> mods)
    {
        var client = _client.Flush();
        var server = _server.Flush();
        _clientLines.AddRange(client);
        _serverLines.AddRange(server);
        _clientErrors.Add(client, final: true);
        _serverErrors.Add(server, final: true);
        string? report = null;
        if (File.Exists(_crashFile) && File.GetLastWriteTimeUtc(_crashFile) > _crashBaseline)
        {
            try { report = File.ReadAllText(_crashFile); }
            catch (IOException) { }
        }
        return CrashAnalyzer.Analyze(GameLog.Parse(_clientLines), report, GameLog.Parse(_serverLines), mods());
    }

    /// <summary>«Ошибки модов» за этот запуск: игра их проглотила, но они копятся (и бывают фризы).</summary>
    public ModErrorReport ErrorReport(Func<ModFingerprints> mods)
    {
        Read();
        return ModErrorReport.Build([.. _clientErrors.Errors, .. _serverErrors.Errors], mods(), StartedUtc.ToLocalTime());
    }

    private IReadOnlyList<string> Read()
    {
        var fresh = _client.ReadNew();
        var server = _server.ReadNew();
        Keep(_clientLines, fresh);
        Keep(_serverLines, server);
        _clientErrors.Add(fresh);
        _serverErrors.Add(server);
        return fresh;
    }

    private static void Keep(List<string> into, IReadOnlyList<string> lines)
    {
        into.AddRange(lines);
        if (into.Count > MaxLines) into.RemoveRange(0, into.Count - MaxLines);
    }
}
