namespace eViSTool.Core.Server;

public enum ConsoleLineKind
{
    /// <summary>Обычный вывод сервера (stdout).</summary>
    Output,
    /// <summary>stderr сервера.</summary>
    Error,
    /// <summary>Команда, которую отправили мы.</summary>
    Input,
    /// <summary>Сообщение самого eViSTool (запуск, остановка, сторож).</summary>
    System,
}

/// <summary>Строка консоли. Seq растёт всегда — по нему клиент забирает «всё новее N».</summary>
public sealed record ConsoleLine(long Seq, DateTime Time, ConsoleLineKind Kind, string Text)
{
    /// <summary>Уровень из строки лога VS: "[Notification]", "[Warning]", "[Error]"… — для подсветки.</summary>
    public string? Level
    {
        get
        {
            var open = Text.IndexOf(" [", StringComparison.Ordinal);
            if (open < 0) return null;
            var close = Text.IndexOf(']', open);
            return close > open ? Text[(open + 2)..close] : null;
        }
    }
}

/// <summary>Кольцевой буфер консоли: последние N строк, потокобезопасно.</summary>
public sealed class ServerConsole(int capacity = 5000)
{
    private readonly LinkedList<ConsoleLine> _lines = new();
    private readonly object _lock = new();
    private long _seq;

    public event Action<ConsoleLine>? LineAdded;

    public long LastSeq
    {
        get { lock (_lock) return _seq; }
    }

    public ConsoleLine Add(ConsoleLineKind kind, string text)
    {
        ConsoleLine line;
        lock (_lock)
        {
            line = new ConsoleLine(++_seq, DateTime.Now, kind, text);
            _lines.AddLast(line);
            while (_lines.Count > capacity) _lines.RemoveFirst();
        }
        LineAdded?.Invoke(line);
        return line;
    }

    /// <summary>Строки новее seq (не больше max). seq=0 — с самой старой из хранящихся.</summary>
    public IReadOnlyList<ConsoleLine> GetSince(long seq, int max = 1000)
    {
        lock (_lock)
        {
            return _lines.Where(l => l.Seq > seq).Take(max).ToList();
        }
    }
}
