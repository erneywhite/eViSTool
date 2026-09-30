using System.Text.RegularExpressions;

namespace eViSTool.Core.Server;

/// <summary>Игрок, который сейчас на сервере.</summary>
public sealed record OnlinePlayer(string Name, DateTime Since, string? Address);

/// <summary>
/// Кто сейчас на сервере — по строкам консоли. Опирается на три строки, которые сервер пишет по-английски при любом
/// языке (в отличие от «Игрок X вышел.»):
/// <c>Client 1 uid … attempting identification. Name: Erney</c> — имя клиента с этим номером;
/// <c>Erney 192.168.31.220:63829 joins.</c> — игрок вошёл;
/// <c>Client 1 disconnected</c> — клиент с этим номером отключился (и тот, кто так и не вошёл, — тоже: тогда ничего не меняется).
/// </summary>
public sealed partial class PlayerTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<int, string> _names = [];                        // номер клиента → имя
    private readonly Dictionary<string, OnlinePlayer> _online = new(StringComparer.Ordinal);

    /// <summary>Состав игроков изменился.</summary>
    public event Action? Changed;

    /// <summary>Игроки на сервере, по времени входа.</summary>
    public IReadOnlyList<OnlinePlayer> Players
    {
        get { lock (_lock) return [.. _online.Values.OrderBy(p => p.Since)]; }
    }

    /// <summary>Разобрать строку консоли сервера (с отметкой времени и уровнем или без них).</summary>
    public void Process(string line, DateTime time)
    {
        bool changed;
        lock (_lock) changed = Apply(line, time);
        if (changed) Changed?.Invoke();
    }

    /// <summary>Сервер остановлен или запускается заново — на нём никого.</summary>
    public void Reset()
    {
        bool changed;
        lock (_lock)
        {
            changed = _online.Count > 0;
            _online.Clear();
            _names.Clear();
        }
        if (changed) Changed?.Invoke();
    }

    private bool Apply(string line, DateTime time)
    {
        if (Identification().Match(line) is { Success: true } id)
        {
            _names[int.Parse(id.Groups[1].Value)] = id.Groups[2].Value.Trim();
            return false;
        }

        if (Joins().Match(line) is { Success: true } join)
        {
            var name = join.Groups[1].Value;
            // вошёл повторно, не успев «выйти» (обрыв связи): время входа — новое
            _online[name] = new OnlinePlayer(name, time, join.Groups[2].Value);
            return true;
        }

        if (Disconnected().Match(line) is { Success: true } gone
            && _names.Remove(int.Parse(gone.Groups[1].Value), out var who)
            // тот же игрок мог уже войти снова под другим номером клиента — тогда он по-прежнему на сервере
            && !_names.ContainsValue(who))
            return _online.Remove(who);

        return false;
    }

    [GeneratedRegex(@"Client (\d+) uid \S+ attempting identification\. Name: (.+)$")]
    private static partial Regex Identification();

    // только событие сервера: фраза «… joins.» в чате входом не считается
    [GeneratedRegex(@"\[(?:Server )?Event\] (\S+) (\S+) joins\.$")]
    private static partial Regex Joins();

    [GeneratedRegex(@"\] Client (\d+) disconnected[.:]")]
    private static partial Regex Disconnected();
}
