using System.Text.RegularExpressions;

namespace eViSTool.Core.Diagnostics;

/// <summary>Запись лога игры: «7.10.2026 00:18:55 [Error] Exception: …» и строки под ней (стек, подробности).</summary>
public sealed record LogEntry(string Time, string Level, string Message, IReadOnlyList<string> Details)
{
    public bool IsError => Level is "Error" or "Fatal";

    /// <summary>Строки стека «at Namespace.Type.Method(…)» — без «at» и без хвоста « in файл:line N».</summary>
    public IEnumerable<string> StackFrames => GameLog.Frames(Details.Prepend(Message));
}

/// <summary>
/// Разбор логов Vintage Story (client-main.log, server-main.log, client-crash.log). Дата — в формате системы игрока
/// (у русской Windows «7.10.2026 00:18:55», у английской «10/7/2026 12:18:55 AM»), поэтому запись узнаём по уровню
/// в квадратных скобках после даты и времени.
/// </summary>
public static partial class GameLog
{
    [GeneratedRegex(@"^(?<time>\S+ \d{1,2}:\d{2}:\d{2}(?: [AP]M)?) \[(?<level>[A-Za-z]+)\] (?<msg>.*)$")]
    private static partial Regex EntryLine();

    // «   at A.B.C(x)»; лог иногда склеивает два стека в одну строку — «…(x)   at D.E(y)»
    [GeneratedRegex(@"(?:^\s*|\s{2,})at (?<frame>[^\s(]+)\(")]
    private static partial Regex FrameLine();

    public static IReadOnlyList<LogEntry> Parse(IEnumerable<string> lines)
    {
        var result = new List<LogEntry>();
        string? time = null, level = null, message = null;
        var details = new List<string>();
        foreach (var line in lines)
        {
            var m = EntryLine().Match(line);
            if (m.Success)
            {
                if (message is not null) result.Add(new LogEntry(time!, level!, message, [.. details]));
                (time, level, message) = (m.Groups["time"].Value, m.Groups["level"].Value, m.Groups["msg"].Value);
                details.Clear();
            }
            else if (message is not null) details.Add(line);
            // строки до первой записи (хвост прошлой записи при чтении с середины файла) — пропускаем
        }
        if (message is not null) result.Add(new LogEntry(time!, level!, message, [.. details]));
        return result;
    }

    /// <summary>Строка начинает новую запись лога (дата, время, [уровень]).</summary>
    public static bool IsEntryStart(string line) => EntryLine().IsMatch(line);

    public static IReadOnlyList<LogEntry> Parse(string text) => Parse(text.Split('\n').Select(l => l.TrimEnd('\r')));

    /// <summary>«at A.B.C(x)» — «A.B.C»; в одной строке бывает несколько «at» (лог склеивает стеки).</summary>
    public static IEnumerable<string> Frames(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            foreach (Match m in FrameLine().Matches(line))
                yield return m.Groups["frame"].Value;
    }
}
