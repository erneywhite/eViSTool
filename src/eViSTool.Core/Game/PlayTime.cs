using System.Globalization;
using eViSTool.Core.Diagnostics;
using Newtonsoft.Json;

namespace eViSTool.Core.Game;

/// <summary>Сессия в мире: вход и выход (UTC) и куда — адрес сервера; null — одиночная игра.</summary>
public sealed record PlaySession(DateTime StartUtc, DateTime EndUtc, string? Server)
{
    [JsonIgnore]
    public TimeSpan Length => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
}

/// <summary>
/// Сессии из client-main.log: «Connecting to адрес...» — куда идём, «Received level finalize» — игрок в мире,
/// «Destroying game session» / «Exiting …» — вышел. Лог оборвался посреди сессии (игра ещё идёт или вылетела) — конец
/// по последней записи; при следующем чтении того же лога сессия удлинится (<see cref="PlayLedger.Merge"/>).
/// </summary>
public static class PlaySessions
{
    /// <param name="culture">Дата в логе — в формате системы игрока: читаем в том же (тесты задают свой).</param>
    public static IReadOnlyList<PlaySession> FromLog(IEnumerable<string> lines, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var result = new List<PlaySession>();
        string? server = null;
        DateTime? start = null, last = null;
        foreach (var e in GameLog.Parse(lines))
        {
            if (!DateTime.TryParse(e.Time, culture, DateTimeStyles.AssumeLocal, out var local)) continue;
            var at = local.ToUniversalTime();
            last = at;
            var msg = e.Message;
            if (msg.StartsWith("Connecting to ", StringComparison.Ordinal) && start is null)
                server = Address(msg["Connecting to ".Length..]);
            else if (msg.StartsWith("Received level finalize", StringComparison.Ordinal) && start is null)
                start = at;
            else if (msg.StartsWith("Destroying game session", StringComparison.Ordinal) || msg.StartsWith("Exiting current game", StringComparison.Ordinal)
                     || msg.StartsWith("Exiting game now", StringComparison.Ordinal))
            {
                if (start is { } s) result.Add(new PlaySession(s, at, server));
                start = null;
                server = null;
            }
        }
        if (start is { } open && last is { } end) result.Add(new PlaySession(open, end, server));
        return result;
    }

    /// <summary>Приставка «сервера» одиночной игры, когда мир известен: «world:имя мира».</summary>
    public const string WorldPrefix = "world:";

    /// <summary>
    /// Миры одиночной игры из server-main.log того же запуска: «Savegame …\Saves\имя.vcdbs loaded» — когда и какой.
    /// </summary>
    public static IReadOnlyList<(DateTime Utc, string Name)> Worlds(IEnumerable<string> lines, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var result = new List<(DateTime, string)>();
        foreach (var e in GameLog.Parse(lines))
        {
            if (!e.Message.StartsWith("Savegame ", StringComparison.Ordinal) || !e.Message.EndsWith(" loaded", StringComparison.Ordinal)) continue;
            if (!DateTime.TryParse(e.Time, culture, DateTimeStyles.AssumeLocal, out var local)) continue;
            var path = e.Message["Savegame ".Length..^" loaded".Length];
            var file = path[(path.LastIndexOfAny(['/', '\\']) + 1)..]; // путь бывает виндовый и на Linux
            var name = file.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase) ? file[..^".vcdbs".Length] : file;
            if (name.Length > 0) result.Add((local.ToUniversalTime(), name));
        }
        return result;
    }

    /// <summary>Одиночным сессиям — мир, загруженный последним перед входом.</summary>
    public static IReadOnlyList<PlaySession> WithWorlds(IReadOnlyList<PlaySession> sessions, IReadOnlyList<(DateTime Utc, string Name)> worlds) =>
        worlds.Count == 0 ? sessions
        : [.. sessions.Select(s => s.Server is null && worlds.LastOrDefault(w => w.Utc <= s.StartUtc) is { Name: { } name }
            ? s with { Server = WorldPrefix + name } : s)];

    /// <summary>«192.168.31.31...» → «192.168.31.31»; стандартный порт игры отбрасываем — «host» и «host:42420» один сервер.</summary>
    public static string Address(string text)
    {
        var a = text.Trim().TrimEnd('.').Trim().ToLowerInvariant();
        var suffix = ":" + PlayTargets.DefaultGamePort;
        return a.EndsWith(suffix, StringComparison.Ordinal) ? a[..^suffix.Length] : a;
    }

    /// <summary>Логи клиента в папке данных: текущий и из архива игры (Logs\Archive\&lt;дата&gt;\client-main.log).</summary>
    public static IEnumerable<(string Key, string Path)> LogFiles(string dataDir)
    {
        var logs = System.IO.Path.Combine(dataDir, "Logs");
        var archive = System.IO.Path.Combine(logs, "Archive");
        if (Directory.Exists(archive))
            foreach (var dir in Directory.EnumerateDirectories(archive))
                if (System.IO.Path.Combine(dir, "client-main.log") is var file && File.Exists(file))
                    yield return (System.IO.Path.GetFileName(dir), file);
        var current = System.IO.Path.Combine(logs, "client-main.log");
        if (File.Exists(current)) yield return ("", current);
    }
}

/// <summary>
/// Время в игре по профилю: сессии, собранные из логов (data\playtime\&lt;профиль&gt;.json). Игра держит только
/// текущий лог и несколько последних в архиве, поэтому копим сами: время считается с первого чтения логов, а сессии,
/// сыгранные без программы, подхватываются, пока их лог ещё в архиве.
/// </summary>
public sealed class PlayLedger
{
    [JsonProperty] private List<PlaySession> Sessions { get; set; } = [];

    /// <summary>Папки архива логов, которые уже прочитаны (архив не меняется — второй раз не читаем).</summary>
    [JsonProperty] private HashSet<string> ScannedArchives { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public IReadOnlyList<PlaySession> All => Sessions;

    public TimeSpan Total => Sessions.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Length);

    public PlaySession? Last => Sessions.MaxBy(s => s.StartUtc);

    /// <summary>С какой сессии считаем: самая ранняя.</summary>
    public DateTime? SinceUtc => Sessions.Count == 0 ? null : Sessions.Min(s => s.StartUtc);

    /// <summary>По серверам: адрес (null — одиночная игра) → время, от большего.</summary>
    public IReadOnlyList<(string? Server, TimeSpan Time)> ByServer() =>
        [.. Sessions.GroupBy(s => s.Server).Select(g => (g.Key, g.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Length)))
            .OrderByDescending(x => x.Item2)];

    /// <summary>Добавить сессии: та же сессия (вход в ту же секунду) не повторяется, а удлиняется, если лог дописался.</summary>
    public bool Merge(IEnumerable<PlaySession> sessions)
    {
        var changed = false;
        foreach (var s in sessions)
        {
            var i = Sessions.FindIndex(o => o.StartUtc == s.StartUtc);
            if (i < 0) { Sessions.Add(s); changed = true; }
            else if (s.EndUtc > Sessions[i].EndUtc || (Sessions[i].Server is null && s.Server is not null))
            {
                // лог дописался — сессия длиннее; или мир одиночной игры узнали позже
                Sessions[i] = s with { EndUtc = s.EndUtc > Sessions[i].EndUtc ? s.EndUtc : Sessions[i].EndUtc, Server = s.Server ?? Sessions[i].Server };
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Прочитать логи папки данных: архив — только новые папки, текущий лог — всегда. true — что-то добавилось.</summary>
    public bool Scan(string dataDir, CultureInfo? culture = null)
    {
        var changed = false;
        foreach (var (key, path) in PlaySessions.LogFiles(dataDir))
        {
            if (key.Length > 0 && ScannedArchives.Contains(key)) continue;
            try
            {
                var sessions = PlaySessions.FromLog(ReadShared(path), culture);
                var server = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "server-main.log");
                if (sessions.Any(s => s.Server is null) && File.Exists(server))
                    sessions = PlaySessions.WithWorlds(sessions, PlaySessions.Worlds(ReadShared(server), culture));
                changed |= Merge(sessions);
                if (key.Length > 0) changed |= ScannedArchives.Add(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // лог занят или удалён между перечислением и чтением — прочитаем в следующий раз
            }
        }
        return changed;
    }

    /// <summary>Лог целиком; игра в это время может его дописывать.</summary>
    private static List<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    public static string FileFor(string profileId) => System.IO.Path.Combine(AppPaths.Root, "playtime", profileId + ".json");

    /// <summary>Записать через временный файл: оборвётся запись (выключили свет) — прежний файл цел.</summary>
    public void Save(string profileId)
    {
        var path = FileFor(profileId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Учёт профиля. Файл испорчен — он откладывается рядом (.broken), а не затирается новым учётом: накопленное
    /// время логи уже не вернут, а из отложенного его можно достать руками.
    /// </summary>
    public static PlayLedger Load(string profileId)
    {
        var path = FileFor(profileId);
        try
        {
            if (File.Exists(path) && JsonConvert.DeserializeObject<PlayLedger>(File.ReadAllText(path)) is { Sessions: not null } ledger)
            {
                ledger.ScannedArchives = new HashSet<string>(ledger.ScannedArchives ?? [], StringComparer.OrdinalIgnoreCase);
                return ledger;
            }
            if (File.Exists(path)) PutAside(path);
        }
        catch (JsonException)
        {
            PutAside(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return new PlayLedger();
    }

    private static void PutAside(string path)
    {
        try { File.Move(path, path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".broken"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
