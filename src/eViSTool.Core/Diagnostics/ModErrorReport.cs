using Newtonsoft.Json;

namespace eViSTool.Core.Diagnostics;

/// <summary>Мод и его ошибки за запуск: сколько, пример (последняя), как узнали мод, путь (чтобы выключить).</summary>
public sealed record ModErrorLine(string ModId, string Name, string? Version, string? Path, int Count, string Example, CulpritSource Source);

/// <summary>
/// «Ошибки модов» за запуск игры или сервера: игра их проглатывает и работает дальше, но лог пухнет и бывают фризы.
/// Чьи — по тем же признакам, что и при вылете (стек, метка в сообщении, строка перевода).
/// </summary>
public sealed record ModErrorReport
{
    /// <summary>Сколько ошибок за запуск — от этого «!» у профиля.</summary>
    public const int NoisyFrom = 10;

    public DateTime At { get; init; }
    public int Total { get; init; }
    public int Unattributed { get; init; }
    public IReadOnlyList<ModErrorLine> Mods { get; init; } = [];

    /// <summary>Игрок посмотрел и скрыл «!» — до следующего запуска с ошибками.</summary>
    public bool Dismissed { get; init; }

    [JsonIgnore]
    public bool IsNoisy => Total >= NoisyFrom;

    public static ModErrorReport Build(IReadOnlyList<LogEntry> entries, ModFingerprints mods, DateTime at)
    {
        var errors = CrashAnalyzer.Errors(entries).ToList();
        var attributed = errors.Select(e => (Error: e, Culprit: CrashAnalyzer.Attribute(e, mods))).ToList();
        var lines = attributed.Where(a => a.Culprit is not null)
            .GroupBy(a => a.Culprit!.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var c = g.Last().Culprit!;
                return new ModErrorLine(c.ModId, c.Mod?.Name ?? c.ModId, c.Version, c.Mod?.Path, g.Count(), Example(g.Last().Error), c.Source);
            })
            .OrderByDescending(l => l.Count)
            .ToList();
        return new ModErrorReport
        {
            At = at,
            Total = errors.Count,
            Unattributed = attributed.Count(a => a.Culprit is null),
            Mods = lines,
        };
    }

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
