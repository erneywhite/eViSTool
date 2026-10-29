using Newtonsoft.Json;

namespace eViSTool.Core.Diagnostics;

/// <summary>
/// Мод и его ошибки за запуск: сколько, пример (последняя), как узнали мод, путь (чтобы выключить) и виды ошибок
/// (<see cref="ModErrorKind"/>) — по ним «Игнорировать» узнаёт, что ошибка новая.
/// </summary>
/// <param name="Kinds">Нет у отчётов старых агентов: тогда вид — по примеру (<see cref="KindList"/>).</param>
public sealed record ModErrorLine(string ModId, string Name, string? Version, string? Path, int Count, string Example, CulpritSource Source,
    IReadOnlyList<string>? Kinds = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> KindList => Kinds is { Count: > 0 } k ? k : [ModErrorKind.Of(Example)];
}

/// <summary>
/// «Ошибки модов» за запуск игры или сервера: игра их проглатывает и работает дальше, но лог пухнет и бывают фризы.
/// Чьи — по тем же признакам, что и при вылете (стек, метка в сообщении, строка перевода).
/// </summary>
public sealed record ModErrorReport
{
    /// <summary>Сколько ошибок за запуск (не в игноре) — от этого «!» у профиля.</summary>
    public const int NoisyFrom = 10;

    /// <summary>
    /// Больше видов у одного мода не храним. shortcut: у мода с сотней разных ошибок часть видов не запомнится, и
    /// игнор может слететь на следующем запуске; если такое встретится — хранить виды по частоте.
    /// </summary>
    private const int MaxKinds = 100;

    public DateTime At { get; init; }
    public int Total { get; init; }
    public int Unattributed { get; init; }
    public IReadOnlyList<ModErrorLine> Mods { get; init; } = [];

    /// <summary>Пример и виды ошибок без признаков мода (код самой игры).</summary>
    public string? UnattributedExample { get; init; }
    public IReadOnlyList<string>? UnattributedKinds { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> UnattributedKindList =>
        UnattributedKinds is { Count: > 0 } k ? k : UnattributedExample is { } e ? [ModErrorKind.Of(e)] : [];

    public static ModErrorReport Build(IReadOnlyList<LogEntry> entries, ModFingerprints mods, DateTime at)
    {
        var errors = CrashAnalyzer.Errors(entries).ToList();
        var attributed = errors.Select(e => (Error: e, Culprit: CrashAnalyzer.Attribute(e, mods))).ToList();
        var lines = attributed.Where(a => a.Culprit is not null)
            .GroupBy(a => a.Culprit!.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var c = g.Last().Culprit!;
                return new ModErrorLine(c.ModId, c.Mod?.Name ?? c.ModId, c.Version, c.Mod?.Path, g.Count(), Example(g.Last().Error), c.Source,
                    KindsOf(g.Select(a => a.Error)));
            })
            .OrderByDescending(l => l.Count)
            .ToList();
        var game = attributed.Where(a => a.Culprit is null).Select(a => a.Error).ToList();
        return new ModErrorReport
        {
            At = at,
            Total = errors.Count,
            Unattributed = game.Count,
            Mods = lines,
            UnattributedExample = game.Count > 0 ? Example(game[^1]) : null,
            UnattributedKinds = game.Count > 0 ? KindsOf(game) : null,
        };
    }

    private static List<string> KindsOf(IEnumerable<CrashAnalyzer.ErrorEntry> errors) =>
        errors.Select(e => ModErrorKind.Of(e.Entry.Message)).Distinct(StringComparer.Ordinal).Take(MaxKinds).ToList();

    /// <summary>Пример ошибки для показа: сообщение, а у ошибки перевода — и сама строка.</summary>
    private static string Example(CrashAnalyzer.ErrorEntry e)
    {
        var text = e.Entry.Message.Length > 300 ? e.Entry.Message[..300] + "…" : e.Entry.Message;
        return e.Translation is { } t ? $"{text} — \"{t}\"" : text;
    }

    /// <summary>Сводки по профилям — на диске (data\mod-errors\&lt;профиль&gt;.json), чтобы пережить перезапуск окна.</summary>
    public static string FileFor(string profileId) => System.IO.Path.Combine(AppPaths.Root, "mod-errors", profileId + ".json");

    public void Save(string profileId)
    {
        var path = FileFor(profileId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    public static ModErrorReport? Load(string profileId)
    {
        try
        {
            var path = FileFor(profileId);
            return File.Exists(path) ? JsonConvert.DeserializeObject<ModErrorReport>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
