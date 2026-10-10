using Newtonsoft.Json;

namespace eViSTool.Core.Mods;

/// <summary>
/// Одно изменение мода в истории. <see cref="From"/> null — мод поставлен впервые, <see cref="To"/> null — удалён.
/// <see cref="Op"/> — действие, в которое оно входит: «Обновить всё» на пять модов — пять записей с одним Op.
/// Время — UTC: историю удалённого сервера пишет его агент, окно переводит в свой пояс.
/// </summary>
public sealed record ModHistoryEntry(string Op, DateTime At, string Source, string ModId, string? Name, string? From, string? To,
    string? Undoes = null)
{
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Name) ? ModId : Name;
}

/// <summary>Действие целиком — то, что видит человек одной строкой: «Обновлено 5 модов», «Откат: Footprints».</summary>
public sealed record ModHistoryOp(string Id, DateTime At, string Source, string? Undoes, IReadOnlyList<ModHistoryEntry> Changes);

/// <summary>Откуда взялось действие — для подписи в истории.</summary>
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
/// История изменений модов профиля: файл <c>ModHistory/&lt;профиль&gt;.jsonl</c> в данных eViSTool, по строке на мод.
/// Пишет её то, что меняет моды: окно для своих профилей, агент для сервера (в том числе по просьбе окна с другого
/// компьютера — тогда действие окна приходит в заголовках и записи агента встают в ту же пачку). Хранится 90 дней.
/// <para>
/// Пачка задаётся областью <see cref="Begin"/>: всё, что поставлено или удалено внутри неё (и в вызванных из неё
/// задачах), — одно действие. Изменение вне области — отдельное действие с источником «вручную».
/// </para>
/// </summary>
public static class ModHistory
{
    public const int KeepDays = 90;

    public static string Dir => Path.Combine(AppPaths.Root, "ModHistory");

    public static string FileFor(string profileId, string? dir = null) => Path.Combine(dir ?? Dir, $"{profileId}.jsonl");

    // ---- текущее действие

    /// <summary>Действие, внутри которого идёт работа: его id, источник и какое действие оно откатывает.</summary>
    public sealed record Scope(string Id, string Source, string? Undoes);

    private static readonly AsyncLocal<Scope?> _current = new();

    public static Scope? Current => _current.Value;

    /// <summary>
    /// Начать действие. Вложенный вызов внутри уже начатого ничего не меняет: «Обновить всё» зовёт установку каждого
    /// мода, и всё это — одна пачка.
    /// </summary>
    public static IDisposable Begin(string source, string? undoes = null) =>
        _current.Value is null ? Enter(new Scope(NewId(), source, undoes)) : Nothing.Instance;

    /// <summary>
    /// Продолжить действие, начатое в другом процессе (агент получил его от окна). Без id — как <see cref="Begin"/>.
    /// </summary>
    public static IDisposable Join(string? id, string? source, string? undoes = null) =>
        id is { Length: > 0 } && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? Enter(new Scope(id, Known(source), undoes is { Length: > 0 and <= 64 } ? undoes : null))
            : Begin(Known(source), undoes);

    private static string Known(string? source) => source is { Length: > 0 and <= 32 } && source.All(char.IsAsciiLetterOrDigit)
        ? source : ModHistorySource.Manual;

    private static IDisposable Enter(Scope scope)
    {
        var previous = _current.Value;
        _current.Value = scope;
        return new Restore(previous);
    }

    /// <summary>Новое действие, не делая его текущим: очередь окна запоминает его у пунктов и входит в него при установке.</summary>
    public static Scope NewScope(string source, string? undoes = null) => new(NewId(), source, undoes);

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

    // ---- запись и чтение

    /// <summary>Записать изменение мода в историю файла <paramref name="file"/> (в текущее действие или отдельным).</summary>
    public static void Record(string file, string modId, string? name, string? from, string? to, DateTime? at = null)
    {
        var scope = Current ?? new Scope(NewId(), ModHistorySource.Manual, null);
        var entry = new ModHistoryEntry(scope.Id, at ?? DateTime.UtcNow, scope.Source, modId, name, from, to, scope.Undoes);
        try
        {
            Retry(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream))
                    writer.WriteLine(JsonConvert.SerializeObject(entry));
                PruneIfOld(file);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // история — подсказка, а не условие: мод уже поставлен, сбой записи не должен отменять установку
        }
    }

    /// <summary>Действия за последние <paramref name="days"/> дней, новые сверху. Битые строки пропускаются.</summary>
    public static IReadOnlyList<ModHistoryOp> Read(string file, int days = KeepDays, DateTime? now = null)
    {
        var since = (now ?? DateTime.UtcNow).AddDays(-days);
        return Group(ReadEntries(file).Where(e => e.At >= since));
    }

    /// <summary>Записи → действия: по Op, в порядке записи; действие датировано первым изменением; новые сверху.</summary>
    public static IReadOnlyList<ModHistoryOp> Group(IEnumerable<ModHistoryEntry> entries) =>
        [.. entries.GroupBy(e => e.Op)
            .Select(g => new ModHistoryOp(g.Key, g.Min(e => e.At), g.First().Source, g.First().Undoes, [.. g]))
            .OrderByDescending(op => op.At)];

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
                if (JsonConvert.DeserializeObject<ModHistoryEntry>(line) is { Op.Length: > 0, ModId.Length: > 0 } e)
                    list.Add(e with { At = DateTime.SpecifyKind(e.At, DateTimeKind.Utc) });
            }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>
    /// Убрать записи старше срока. Переписываем файл, только когда старейшая запись вышла за срок больше чем на неделю:
    /// иначе каждая установка переписывала бы весь файл.
    /// </summary>
    private static void PruneIfOld(string file)
    {
        var entries = ReadEntries(file);
        var limit = DateTime.UtcNow.AddDays(-KeepDays);
        if (entries.Count == 0 || entries[0].At >= limit.AddDays(-7)) return;
        var tmp = file + ".tmp";
        File.WriteAllLines(tmp, entries.Where(e => e.At >= limit).Select(e => JsonConvert.SerializeObject(e)));
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
