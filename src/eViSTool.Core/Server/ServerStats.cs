using System.Globalization;

namespace eViSTool.Core.Server;

/// <summary>Что записано в статистику: замер раз в минуту или событие.</summary>
public enum StatsKind
{
    /// <summary>Замер: игроков в игре, память сервера (МБ), процессор (% всей машины).</summary>
    Sample,
    Join,
    Leave,
    /// <summary>Сервер запустился (дошёл до работы).</summary>
    Up,
    /// <summary>Сервер остановили.</summary>
    Down,
    /// <summary>Сервер остановился сам — упал.</summary>
    Crash,
}

public sealed record StatsEntry(DateTime Time, StatsKind Kind, int Players = 0, long MemoryMb = 0, double Cpu = 0, string? Name = null);

/// <summary>За какой срок показывать.</summary>
public enum StatsPeriod
{
    Day,
    Week,
    Month,
}

/// <summary>
/// Статистика сервера: папка agents/&lt;профиль&gt;.stats рядом с настройками агента, по файлу на день
/// («2026-10-09.log», строка — «ЧЧ:ММ:СС ⇥ вид ⇥ данные»). Пишет агент (и при закрытом окне), хранится
/// <see cref="KeepDays"/> дней. Сбор включается отметкой «enabled» в той же папке — отдельно от расписания,
/// чтобы вкладки «Расписание» и «Статистика» не перезаписывали настройки друг друга.
/// </summary>
public sealed class StatsStore(string dir)
{
    public const int KeepDays = 30;
    private const string DayFormat = "yyyy-MM-dd";
    private const string Ext = ".log";
    private const string EnabledMark = "enabled";
    private readonly object _lock = new();

    public string Dir => dir;

    public static string DirFor(string profileId, string? agentsDir = null) =>
        Path.Combine(agentsDir ?? AgentProtocol.DefaultAgentsDir, $"{profileId}.stats");

    public bool IsEnabled => File.Exists(Path.Combine(dir, EnabledMark));

    public void SetEnabled(bool on)
    {
        var mark = Path.Combine(dir, EnabledMark);
        if (on)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(mark, "");
        }
        else if (File.Exists(mark)) File.Delete(mark);
    }

    public void Append(StatsEntry e)
    {
        var data = e.Kind switch
        {
            StatsKind.Sample => $"s\t{e.Players}\t{e.MemoryMb}\t{e.Cpu.ToString("0.#", CultureInfo.InvariantCulture)}",
            StatsKind.Join => $"j\t{e.Name}",
            StatsKind.Leave => $"l\t{e.Name}",
            StatsKind.Up => "up",
            StatsKind.Down => "down",
            _ => "crash",
        };
        lock (_lock)
        {
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, e.Time.ToString(DayFormat, CultureInfo.InvariantCulture) + Ext),
                e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + data + "\n");
        }
    }

    /// <summary>Убрать дни старше <see cref="KeepDays"/>.</summary>
    public void Prune(DateTime now)
    {
        foreach (var (day, path) in Days())
            if (day < now.Date.AddDays(-KeepDays))
                try { File.Delete(path); } catch (IOException) { }
    }

    /// <summary>Стереть всё собранное (отметка «собирать» остаётся как была).</summary>
    public void Clear()
    {
        foreach (var (_, path) in Days()) File.Delete(path);
    }

    /// <summary>Записи с from по to, по порядку. Испорченные строки пропускаются.</summary>
    public List<StatsEntry> Read(DateTime from, DateTime to)
    {
        var list = new List<StatsEntry>();
        foreach (var (day, path) in Days().Where(d => d.Day >= from.Date && d.Day <= to.Date).OrderBy(d => d.Day))
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (IOException) { continue; }
            foreach (var line in lines)
                if (Parse(day, line) is { } e && e.Time >= from && e.Time <= to) list.Add(e);
        }
        return list;
    }

    private IEnumerable<(DateTime Day, string Path)> Days()
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(dir, "*" + Ext))
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                yield return (day, path);
    }

    private static StatsEntry? Parse(DateTime day, string line)
    {
        var p = line.Split('\t');
        if (p.Length < 2 || !TimeSpan.TryParseExact(p[0], @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var tod)) return null;
        var t = day + tod;
        return p[1] switch
        {
            "s" when p.Length >= 5 && int.TryParse(p[2], CultureInfo.InvariantCulture, out var n) && long.TryParse(p[3], CultureInfo.InvariantCulture, out var mem)
                                   && double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu)
                => new StatsEntry(t, StatsKind.Sample, n, mem, cpu),
            "j" when p.Length >= 3 => new StatsEntry(t, StatsKind.Join, Name: p[2]),
            "l" when p.Length >= 3 => new StatsEntry(t, StatsKind.Leave, Name: p[2]),
            "up" => new StatsEntry(t, StatsKind.Up),
            "down" => new StatsEntry(t, StatsKind.Down),
            "crash" => new StatsEntry(t, StatsKind.Crash),
            _ => null,
        };
    }

    /// <summary>Итоги за срок до now — то, что показывает вкладка.</summary>
    public StatsReport Report(StatsPeriod period, DateTime now)
    {
        var from = now - StatsReport.SpanOf(period);
        // на день раньше: кто был в игре к началу срока, зашёл до него
        return StatsReport.Build(Read(from.AddDays(-1), now), period, now, IsEnabled);
    }
}

/// <summary>Точка графика: max игроков, средние память и процессор за шаг; null — сервер не работал (или не собирали).</summary>
public sealed record StatsPoint(DateTime Time, int? Players, double? MemoryMb, double? Cpu);

public sealed record PlayerStats(string Name, TimeSpan Played, int Sessions, DateTime LastSeen, bool Online);

/// <summary>Итоги статистики за срок: точки графиков, игроки, плитки.</summary>
public sealed record StatsReport
{
    public bool Enabled { get; init; }
    public StatsPeriod Period { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public TimeSpan Step { get; init; }
    public List<StatsPoint> Points { get; init; } = [];
    public List<PlayerStats> Players { get; init; } = [];
    public List<DateTime> Crashes { get; init; } = [];
    public TimeSpan TotalPlayed { get; init; }
    public int PeakPlayers { get; init; }
    public DateTime? PeakAt { get; init; }
    public int Restarts { get; init; }

    /// <summary>Доля времени, когда сервер работал (от начала сбора в этом сроке); null — данных нет.</summary>
    public double? Uptime { get; init; }

    public bool HasData => Points.Any(p => p.Players is not null) || Players.Count > 0;

    /// <summary>Замер раз в минуту: больше нет записей — агент не работал.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(5);

    public static TimeSpan SpanOf(StatsPeriod period) => period switch
    {
        StatsPeriod.Day => TimeSpan.FromDays(1),
        StatsPeriod.Week => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(StatsStore.KeepDays),
    };

    /// <summary>Шаг графика: около 300 точек на любой срок.</summary>
    public static TimeSpan StepOf(StatsPeriod period) => period switch
    {
        StatsPeriod.Day => TimeSpan.FromMinutes(5),
        StatsPeriod.Week => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromHours(2),
    };

    public static StatsReport Build(IReadOnlyList<StatsEntry> entries, StatsPeriod period, DateTime now, bool enabled = true)
    {
        var to = now;
        var from = now - SpanOf(period);
        var step = StepOf(period);
        entries = [.. entries.OrderBy(e => e.Time)];
        var inside = entries.Where(e => e.Time >= from && e.Time <= to).ToList();
        var samples = inside.Where(e => e.Kind == StatsKind.Sample).ToList();

        // точки графика: шаги от начала срока (выровненного по шагу)
        var start = new DateTime(from.Ticks - from.Ticks % step.Ticks, from.Kind);
        var points = new List<StatsPoint>();
        var i = 0;
        for (var t = start; t < to; t += step)
        {
            var bucket = new List<StatsEntry>();
            while (i < samples.Count && samples[i].Time < t + step)
            {
                if (samples[i].Time >= t) bucket.Add(samples[i]);
                i++;
            }
            points.Add(bucket.Count == 0
                ? new StatsPoint(t, null, null, null)
                : new StatsPoint(t, bucket.Max(b => b.Players), bucket.Average(b => (double)b.MemoryMb), bucket.Average(b => b.Cpu)));
        }

        // сеансы игроков: вход — выход; остановка, падение или пропуск в записях (агент не работал) закрывают все
        var sessions = new List<(string Name, DateTime Start, DateTime End)>();
        var open = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        DateTime? last = null;
        void CloseAll(DateTime at)
        {
            foreach (var (name, startAt) in open) sessions.Add((name, startAt, at));
            open.Clear();
        }
        foreach (var e in entries)
        {
            if (last is { } l && e.Time - l > Gap) CloseAll(l);
            switch (e.Kind)
            {
                case StatsKind.Join when e.Name is { } n && !open.ContainsKey(n):
                    open[n] = e.Time;
                    break;
                case StatsKind.Leave when e.Name is { } n && open.Remove(n, out var s):
                    sessions.Add((n, s, e.Time));
                    break;
                case StatsKind.Down or StatsKind.Crash or StatsKind.Up:
                    CloseAll(e.Time);
                    break;
            }
            last = e.Time;
        }
        var alive = last is { } la && now - la <= Gap;
        var online = alive ? open.Keys.ToHashSet(StringComparer.Ordinal) : [];
        CloseAll(alive ? now : last ?? now);

        var players = sessions
            .Where(s => s.End > from && s.Start < to)
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => new PlayerStats(g.Key,
                TimeSpan.FromTicks(g.Sum(s => (Min(s.End, to) - Max(s.Start, from)).Ticks)),
                g.Count(), g.Max(s => s.End), online.Contains(g.Key)))
            .OrderByDescending(p => p.Played)
            .ToList();

        var peak = samples.OrderByDescending(s => s.Players).ThenBy(s => s.Time).FirstOrDefault();
        double? uptime = null;
        if (samples.Count > 0)
        {
            // от начала сбора в этом сроке: включили вчера — неделя не считается простоем
            var since = Max(from, entries[0].Time);
            var minutes = Math.Max(1, (to - since).TotalMinutes);
            uptime = Math.Min(1, samples.Count / minutes);
        }

        return new StatsReport
        {
            Enabled = enabled,
            Period = period,
            From = from,
            To = to,
            Step = step,
            Points = points,
            Players = players,
            Crashes = [.. inside.Where(e => e.Kind == StatsKind.Crash).Select(e => e.Time)],
            TotalPlayed = TimeSpan.FromTicks(players.Sum(p => p.Played.Ticks)),
            PeakPlayers = peak?.Players ?? 0,
            PeakAt = peak is { Players: > 0 } ? peak.Time : null,
            Restarts = inside.Count(e => e.Kind == StatsKind.Up),
            Uptime = uptime,
        };
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

/// <summary>Процессор сервера между замерами: доля всей машины, как в диспетчере задач.</summary>
public sealed class CpuMeter
{
    private TimeSpan? _cpu;
    private DateTime _at;

    /// <summary>Новый замер; первый (или после перезапуска процесса) — null.</summary>
    public double? Next(TimeSpan? processorTime, DateTime now)
    {
        if (processorTime is not { } cpu)
        {
            _cpu = null;
            return null;
        }
        double? result = null;
        if (_cpu is { } prev && cpu >= prev && now > _at)
            result = Math.Clamp((cpu - prev).TotalMilliseconds / (now - _at).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100);
        (_cpu, _at) = (cpu, now);
        return result;
    }
}
