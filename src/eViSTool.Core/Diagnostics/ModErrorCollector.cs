namespace eViSTool.Core.Diagnostics;

/// <summary>
/// Копит ошибки за запуск из новых строк лога (или вывода сервера), подаваемых кусками: хранит только записи-ошибки
/// (и строку перевода за каждой), а не весь лог. Последняя запись куска может быть недописана — стек придёт следующим
/// куском, — её придерживаем до следующего раза.
/// </summary>
public sealed class ModErrorCollector
{
    private const int MaxErrors = 50_000;
    private readonly List<string> _pending = [];
    private readonly List<LogEntry> _errors = [];

    public IReadOnlyList<LogEntry> Errors => _errors;

    /// <param name="final">Больше строк не будет (игра закрылась) — разобрать и недописанное.</param>
    public void Add(IEnumerable<string> lines, bool final = false)
    {
        _pending.AddRange(lines);
        var cut = final ? _pending.Count : _pending.FindLastIndex(GameLog.IsEntryStart);
        if (cut <= 0) return;
        var entries = GameLog.Parse(_pending.Take(cut).ToList());
        _pending.RemoveRange(0, cut);
        for (var i = 0; i < entries.Count; i++)
            if (entries[i].IsError || (i > 0 && entries[i - 1].IsError && entries[i].Level == "Warning"))
                _errors.Add(entries[i]);
        if (_errors.Count > MaxErrors) _errors.RemoveRange(0, _errors.Count - MaxErrors);
    }
}
