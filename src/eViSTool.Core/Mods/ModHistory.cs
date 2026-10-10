using Newtonsoft.Json;

namespace eViSTool.Core.Mods;

/// <summary>
/// Строка файла истории: изменение мода или отметка о запуске игры/сервера (<see cref="Event"/> = «launch», мода нет).
/// У изменения <see cref="From"/> null — мод поставлен впервые, <see cref="To"/> null — удалён. <see cref="Op"/> — нажатие,
/// которым оно сделано; <see cref="Undoes"/> — какой сеанс откатывает. Время — UTC: историю удалённого сервера пишет его
/// агент, окно переводит в свой пояс.
/// </summary>
public sealed record ModHistoryEntry(string Op, DateTime At, string Source, string ModId, string? Name, string? From, string? To,
    string? Undoes = null, string? Event = null)
{
    public const string Launch = "launch";

    [JsonIgnore] public bool IsLaunch => Event == Launch;
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Name) ? ModId : Name;
}

/// <summary>
/// Сеанс — то, что человек видит одной строкой истории: всё, что поменялось в модах между двумя запусками игры (или
/// сервера). Без запуска сеанс заканчивается, когда изменения прервались больше чем на полчаса.
/// <see cref="LaunchedAt"/> — запуск, которым он закрылся (null — после него игру ещё не запускали или был перерыв).
/// </summary>
public sealed record ModHistorySession(string Id, DateTime Start, DateTime End, DateTime? LaunchedAt, IReadOnlyList<ModHistoryEntry> Changes)
{
    /// <summary>Какой сеанс откатывает этот (null — это не откат).</summary>
    public string? Undoes => Changes[0].Undoes;

    /// <summary>Откуда изменения сеанса, без повторов, в порядке появления.</summary>
    public IReadOnlyList<string> Sources => [.. Changes.Select(c => c.Source).Distinct()];

    /// <summary>
    /// Итог по модам: какая версия была до сеанса и какая стала после (мод обновили дважды — одна строка 1.0 → 1.2).
    /// Мод, вернувшийся к той же версии, пропускается. Порядок — по первому изменению мода.
    /// </summary>
    public IReadOnlyList<ModHistoryEntry> Net =>
        [.. Changes.GroupBy(c => c.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last() with { From = g.First().From, Name = g.Last().Name ?? g.First().Name })
            .Where(c => !string.Equals(c.From, c.To, StringComparison.OrdinalIgnoreCase))];
}

/// <summary>Откуда взялось изменение — для подписи в истории.</summary>
public static class ModHistorySource
{
    public const string Manual = "manual";       // кнопки у одного мода: удалить, поставить ту же
    public const string Update = "update";       // «Обновить» у одного мода
    public const string UpdateAll = "updateAll"; // «Обновить всё»
    public const string Catalog = "catalog";     // установка из каталога (вместе с «Поставить также в…»)
    public const string Zip = "zip";             // «Добавить» архивы
    public const string Pack = "pack";           // импорт модпака
    public const string Dependencies = "deps";   // «Исправить» зависимости
    public const string Schedule = "schedule";   // обновление модов при перезапуске по расписанию (агент)
    public const string Rollback = "rollback";   // откат: окно «Версии» или строка истории
}

/// <summary>
/// История изменений модов профиля: файл <c>ModHistory/&lt;профиль&gt;.jsonl</c> в данных eViSTool, по строке на мод
/// и отметки запусков. Пишет её то, что меняет моды: окно для своих профилей, агент для сервера (в том числе по просьбе
/// окна с другого компьютера — нажатие окна приходит в заголовках). Запуск игры отмечает окно, запуск сервера — агент.
/// <para>
/// В строки истории (<see cref="ModHistorySession"/>) записи собираются при чтении: граница — запуск или перерыв
/// больше <see cref="Gap"/>. Хранится <see cref="KeepDays"/> дней, но последние <see cref="KeepSessions"/> сеанса —
/// всегда: кто месяц не играл, после отпуска всё равно увидит, что менял перед ним.
/// </para>
/// </summary>
public static class ModHistory
{
    public const int KeepDays = 30;
    public const int KeepSessions = 3;
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(30);

    public static string Dir => Path.Combine(AppPaths.Root, "ModHistory");

    public static string FileFor(string profileId, string? dir = null) => Path.Combine(dir ?? Dir, $"{profileId}.jsonl");

    // ---- текущее нажатие

    /// <summary>Нажатие, внутри которого идёт работа: его id, источник и какой сеанс оно откатывает.</summary>
    public sealed record Scope(string Id, string Source, string? Undoes);

    private static readonly AsyncLocal<Scope?> _current = new();

    public static Scope? Current => _current.Value;

    /// <summary>
    /// Начать нажатие. Вложенный вызов внутри уже начатого ничего не меняет: модпак зовёт установку каждого мода,
    /// а источник у них один — «модпак».
    /// </summary>
    public static IDisposable Begin(string source, string? undoes = null) =>
        _current.Value is null ? Enter(new Scope(NewId(), source, undoes)) : Nothing.Instance;

    /// <summary>Продолжить нажатие, начатое в другом процессе (агент получил его от окна). Без id — как <see cref="Begin"/>.</summary>
    public static IDisposable Join(string? id, string? source, string? undoes = null) =>
        IsId(id) ? Enter(new Scope(id!, Known(source), IsId(undoes) ? undoes : null)) : Begin(Known(source), IsId(undoes) ? undoes : null);

    /// <summary>Новое нажатие, не делая его текущим: очередь окна запоминает его у пунктов и входит в него при установке.</summary>
    public static Scope NewScope(string source, string? undoes = null) => new(NewId(), source, undoes);

    private static bool IsId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Known(string? source) => source is { Length: > 0 and <= 32 } && source.All(char.IsAsciiLetterOrDigit)
        ? source : ModHistorySource.Manual;

    private static IDisposable Enter(Scope scope)
    {
        var previous = _current.Value;
        _current.Value = scope;
        return new Restore(previous);
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private sealed class Restore(Scope? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }

    // ---- запись

    /// <summary>Записать изменение мода (в текущее нажатие или отдельным, «вручную»).</summary>
    public static void Record(string file, string modId, string? name, string? from, string? to, DateTime? at = null)
    {
        var scope = Current ?? new Scope(NewId(), ModHistorySource.Manual, null);
        Append(file, new ModHistoryEntry(scope.Id, at ?? DateTime.UtcNow, scope.Source, modId, name, from, to, scope.Undoes));
    }

    /// <summary>
    /// Отметить запуск игры или сервера — границу сеанса. Пишется, только если с прошлой отметки моды менялись: иначе
    /// ежедневные запуски без изменений раздували бы файл.
    /// </summary>
    public static void MarkLaunch(string file, DateTime? at = null)
    {
        var entries = ReadEntries(file);
        if (entries.Count == 0 || entries[^1].IsLaunch) return;
        Append(file, new ModHistoryEntry("", at ?? DateTime.UtcNow, "", "", null, null, null, Event: ModHistoryEntry.Launch));
    }

    private static void Append(string file, ModHistoryEntry entry)
    {
        try
        {
            Retry(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream))
                    writer.WriteLine(JsonConvert.SerializeObject(entry, Settings));
                PruneIfOld(file);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // история — подсказка, а не условие: мод уже поставлен, сбой записи не должен отменять установку
        }
    }

    private static readonly JsonSerializerSettings Settings = new() { NullValueHandling = NullValueHandling.Ignore };

    // ---- чтение

    /// <summary>Строки истории, которые хранятся (за срок и последние сеансы), новые сверху.</summary>
    public static IReadOnlyList<ModHistorySession> Read(string file, DateTime? now = null) => Kept(Sessions(ReadEntries(file)), now);

    /// <summary>
    /// Записи → сеансы: граница — отметка запуска, перерыв между изменениями больше <see cref="Gap"/> или откат.
    /// Новые сверху. Id сеанса — нажатие, с которого он начался (по нему откат ссылается на сеанс).
    /// </summary>
    public static IReadOnlyList<ModHistorySession> Sessions(IEnumerable<ModHistoryEntry> entries)
    {
        var sessions = new List<ModHistorySession>();
        var current = new List<ModHistoryEntry>();
        void Close(DateTime? launched)
        {
            if (current.Count == 0) return;
            sessions.Add(new ModHistorySession(current[0].Op, current[0].At, current[^1].At, launched, [.. current]));
            current = [];
        }
        foreach (var e in entries.OrderBy(e => e.At))
        {
            if (e.IsLaunch)
            {
                Close(e.At);
                continue;
            }
            // откат — всегда своя строка: иначе он слился бы с тем, что откатывает, в «ничего не поменялось»,
            // и ни исходный сеанс, ни сам откат было бы не увидеть и не вернуть
            if (current.Count > 0 && (e.At - current[^1].At > Gap || (e.Undoes is not null && e.Op != current[^1].Op))) Close(null);
            current.Add(e);
        }
        Close(null);
        sessions.Reverse();
        return sessions;
    }

    /// <summary>Сеансы, которые храним: закончившиеся не раньше срока и, в любом случае, последние несколько.</summary>
    public static IReadOnlyList<ModHistorySession> Kept(IReadOnlyList<ModHistorySession> sessions, DateTime? now = null)
    {
        var since = (now ?? DateTime.UtcNow).AddDays(-KeepDays);
        return [.. sessions.Where((s, i) => i < KeepSessions || s.End >= since)];
    }

    public static IReadOnlyList<ModHistoryEntry> ReadEntries(string file)
    {
        if (!File.Exists(file)) return [];
        string[] lines;
        try
        {
            lines = Retry(() =>
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var list = new List<ModHistoryEntry>();
        foreach (var line in lines)
        {
            try
            {
                if (JsonConvert.DeserializeObject<ModHistoryEntry>(line) is { } e && (e.IsLaunch || e is { Op.Length: > 0, ModId.Length: > 0 }))
                    list.Add(e with { At = DateTime.SpecifyKind(e.At, DateTimeKind.Utc) });
            }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>
    /// Убрать из файла то, что больше не хранится. Переписываем, только когда лишнее старше начала хранимого больше чем
    /// на неделю: иначе каждая установка переписывала бы весь файл.
    /// </summary>
    private static void PruneIfOld(string file)
    {
        var entries = ReadEntries(file);
        var kept = Kept(Sessions(entries));
        if (entries.Count == 0 || kept.Count == 0) return;
        var from = kept.Min(s => s.Start);
        if (entries[0].At >= from.AddDays(-7)) return;
        var tmp = file + ".tmp";
        File.WriteAllLines(tmp, entries.Where(e => e.At >= from).Select(e => JsonConvert.SerializeObject(e, Settings)));
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>Файл может быть занят второй копией (окно и агент на одной машине) — несколько попыток.</summary>
    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (IOException) when (attempt < 10) { Thread.Sleep(50); }
        }
    }

    private static void Retry(Action action) => Retry<bool>(() => { action(); return true; });
}
