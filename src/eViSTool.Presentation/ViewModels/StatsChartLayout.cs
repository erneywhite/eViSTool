using System.Globalization;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Что рисует график статистики.</summary>
public enum StatsChartKind
{
    /// <summary>Игроки в игре — ступеньками с заливкой.</summary>
    Players,
    /// <summary>Память (ГБ, ось слева) и процессор (%, ось справа) — двумя линиями.</summary>
    Resources,
}

/// <summary>Цвет подписи на графике; кисть темы под него выбирает окно.</summary>
public enum StatsChartTone
{
    Muted,
    Text,
    Good,
    Warn,
    Danger,
}

/// <summary>Точка на графике в координатах элемента.</summary>
public readonly record struct StatsChartPoint(double X, double Y);

/// <summary>Деление шкалы: значение, высота линии (уже на середине пикселя), подпись слева и (у ресурсов) справа.</summary>
public readonly record struct StatsChartTick(double Value, double Y, string Label, string? RightLabel)
{
    /// <summary>Нулевое деление рисуется сплошной линией, остальные — штрихами.</summary>
    public bool IsBase => Value == 0;
}

/// <summary>Подпись времени под графиком: середина подписи по X.</summary>
public readonly record struct StatsChartTimeLabel(double X, string Text);

/// <summary>Строка подсказки под мышью: текст и его цвет.</summary>
public readonly record struct StatsChartTipLine(string Text, StatsChartTone Tone);

/// <summary>
/// Раскладка графика статистики сервера: шкалы, деления, подписи времени, ступеньки игроков, линии памяти и процессора,
/// черты вылетов, точка под мышью и строки подсказки. Только арифметика — рисует элемент окна (StatsChart), ему остаются
/// кисти, перья и размеры текста. Точки идут с равным шагом, пропуск (сервер не работал) разрывает линию.
/// </summary>
public sealed class StatsChartLayout
{
    /// <summary>Поля вокруг области данных: слева подписи шкалы, справа — проценты у ресурсов, снизу — время.</summary>
    public const double Left = 36, Right = 40, Top = 8, Bottom = 22;

    /// <summary>Верх шкалы процессора — всегда 100 %.</summary>
    public const double CpuMax = 100;

    public StatsChartLayout(StatsReport? report, StatsChartKind kind, double width, double height)
    {
        Report = report;
        Kind = kind;
        PlotWidth = width - Left - Right;
        PlotHeight = height - Top - Bottom;
        var points = report?.Points ?? [];
        Max = points.Count == 0 ? 0
            : kind == StatsChartKind.Players ? Math.Max(2, points.Max(p => p.Players ?? 0))
            : NiceGb(points.Max(p => p.MemoryMb ?? 0) / 1024);
    }

    public StatsReport? Report { get; }
    public StatsChartKind Kind { get; }

    /// <summary>Ширина и высота области данных (без полей).</summary>
    public double PlotWidth { get; }
    public double PlotHeight { get; }

    public double PlotRight => Left + PlotWidth;
    public double PlotBottom => Top + PlotHeight;

    /// <summary>Верх основной шкалы: игроков (не меньше 2) или гигабайт памяти (1, 2, 4, 6, 8, 12…).</summary>
    public double Max { get; }

    private int Count => Report?.Points.Count ?? 0;

    /// <summary>Есть что рисовать: точки есть, и место не меньше 20 px в каждую сторону.</summary>
    public bool CanDraw => Count > 0 && PlotWidth > 20 && PlotHeight > 20;

    /// <summary>Левый край шага i.</summary>
    public double X(int i) => Left + PlotWidth * i / Count;

    /// <summary>Место момента времени на оси (от начала первого шага).</summary>
    public double Xt(DateTime t) =>
        Left + PlotWidth * (t - Report!.Points[0].Time).Ticks / (double)(Report.Step.Ticks * Count);

    public double Y(double value, double max) => Top + PlotHeight - PlotHeight * Math.Clamp(value / max, 0, 1);

    /// <summary>Линия в 1 px — на середину пикселя, иначе она размазывается на два.</summary>
    public static double Snap(double v) => Math.Round(v) + 0.5;

    /// <summary>Деления шкалы: у игроков — целые (шаг 1, 2, 5 или 10), у ресурсов — 0, половина и верх.</summary>
    public IReadOnlyList<StatsChartTick> Ticks()
    {
        var culture = CultureInfo.CurrentCulture;
        var players = Kind == StatsChartKind.Players;
        var values = players ? IntTicks((int)Max) : [0, Max / 2, Max];
        return [.. values.Select(t => new StatsChartTick(t, Snap(Y(t, Max)),
            players ? t.ToString("0", culture) : t.ToString("0.#", culture),
            players ? null : (t / Max * 100).ToString("0", culture) + " %"))];
    }

    /// <summary>Подписи времени, которые попадают на ось (с запасом в пиксель по краям).</summary>
    public IReadOnlyList<StatsChartTimeLabel> TimeLabels()
    {
        if (Report is null || Count == 0) return [];
        return [.. TimeLabels(Report)
            .Select(l => new StatsChartTimeLabel(Xt(l.Time), l.Text))
            .Where(l => l.X >= Left - 1 && l.X <= PlotRight + 1)];
    }

    /// <summary>Заливка игроков: по замкнутому многоугольнику-ступенькам на каждый непрерывный отрезок, от нуля.</summary>
    public IReadOnlyList<IReadOnlyList<StatsChartPoint>> PlayerAreas()
    {
        var list = new List<IReadOnlyList<StatsChartPoint>>();
        foreach (var run in PlayerRuns())
        {
            var shape = new List<StatsChartPoint> { new(X(run[0].I), Y(0, Max)) };
            shape.AddRange(Steps(run));
            shape.Add(new StatsChartPoint(X(run[^1].I + 1), Y(0, Max)));
            list.Add(Distinct(shape));
        }
        return list;
    }

    /// <summary>Верхний край ступенек игроков — линия по каждому непрерывному отрезку.</summary>
    public IReadOnlyList<IReadOnlyList<StatsChartPoint>> PlayerTops() =>
        [.. PlayerRuns().Select(run => Distinct(Steps(run)))];

    private List<List<(int I, double V)>> PlayerRuns() =>
        Report is null ? [] : Runs(Report.Points, p => p.Players is { } v ? v : null);

    private IEnumerable<StatsChartPoint> Steps(List<(int I, double V)> run)
    {
        foreach (var (i, v) in run)
        {
            yield return new StatsChartPoint(X(i), Y(v, Max));
            yield return new StatsChartPoint(X(i + 1), Y(v, Max));
        }
    }

    /// <summary>Память, ГБ: ломаная через середины шагов по каждому непрерывному отрезку.</summary>
    public IReadOnlyList<IReadOnlyList<StatsChartPoint>> MemoryLines() => Lines(p => p.MemoryMb / 1024, Max);

    /// <summary>Процессор, %: ломаная через середины шагов, шкала до 100.</summary>
    public IReadOnlyList<IReadOnlyList<StatsChartPoint>> CpuLines() => Lines(p => p.Cpu, CpuMax);

    private IReadOnlyList<IReadOnlyList<StatsChartPoint>> Lines(Func<StatsPoint, double?> value, double max)
    {
        if (Report is null) return [];
        var half = (X(1) - X(0)) / 2;
        var list = new List<IReadOnlyList<StatsChartPoint>>();
        foreach (var run in Runs(Report.Points, value))
        {
            var line = run.Select(p => new StatsChartPoint(X(p.I) + half, Y(p.V, max))).ToList();
            // одна точка — короткая черта до конца шага, иначе её не видно
            if (run.Count == 1) line.Add(new StatsChartPoint(X(run[0].I + 1), Y(run[0].V, max)));
            list.Add(line);
        }
        return list;
    }

    /// <summary>Черты вылетов: X (на середине пикселя) по порядку времени, только те, что на оси.</summary>
    public IReadOnlyList<double> CrashXs() =>
        Report is null || Count == 0 ? []
            : [.. Report.Crashes.Order().Select(c => Snap(Xt(c))).Where(x => x >= Left && x <= PlotRight)];

    /// <summary>
    /// Где подписать вылеты (только на верхнем графике): справа от черты, у правого края — слева. Вылеты почти подряд:
    /// подписи наползали бы друг на друга — вторую не подписываем, хватает черты.
    /// </summary>
    public IReadOnlyList<StatsChartPoint> CrashLabels(double labelWidth, double labelHeight)
    {
        var list = new List<StatsChartPoint>();
        if (Kind != StatsChartKind.Players) return list;
        (double L, double T, double R, double B)? last = null;
        foreach (var x in CrashXs())
        {
            var at = new StatsChartPoint(x + 4 + labelWidth > PlotRight ? x - 4 - labelWidth : x + 4, Top);
            var box = (L: at.X - 4, T: at.Y, R: at.X + labelWidth + 4, B: at.Y + labelHeight);
            // касание краями — тоже пересечение
            if (last is { } p && p.L <= box.R && box.L <= p.R && p.T <= box.B && box.T <= p.B) continue;
            list.Add(at);
            last = box;
        }
        return list;
    }

    /// <summary>Шаг под мышью; вне оси (или точек нет) — null.</summary>
    public int? IndexAt(double x)
    {
        if (Count == 0 || PlotWidth <= 0 || x < Left || x > PlotRight) return null;
        return Math.Clamp((int)((x - Left) / PlotWidth * Count), 0, Count - 1);
    }

    /// <summary>Черта под мышью — посередине шага, на середине пикселя.</summary>
    public double HoverX(int index) => Snap(X(index) + PlotWidth / Count / 2);

    /// <summary>Подсказка к шагу: время, значения (или «не работал»), вылет в этом шаге.</summary>
    public IReadOnlyList<StatsChartTipLine> TipLines(int index)
    {
        var r = Report!;
        var p = r.Points[index];
        var culture = CultureInfo.CurrentCulture;
        var lines = new List<StatsChartTipLine> { new(TimeRange(p.Time, r.Step), StatsChartTone.Muted) };
        if (p.Players is null) lines.Add(new(Loc.T("stats.tipOff"), StatsChartTone.Muted));
        else if (Kind == StatsChartKind.Players) lines.Add(new(Loc.T("stats.tipPlayers", p.Players), StatsChartTone.Text));
        else
        {
            lines.Add(new(Loc.T("stats.tipMemory", ((p.MemoryMb ?? 0) / 1024).ToString("0.0", culture)), StatsChartTone.Good));
            lines.Add(new(Loc.T("stats.tipCpu", (p.Cpu ?? 0).ToString("0", culture)), StatsChartTone.Warn));
        }
        var crash = r.Crashes.Where(c => c >= p.Time && c < p.Time + r.Step).ToList();
        if (crash.Count > 0) lines.Add(new(Loc.T("stats.tipCrash", Dates.Clock(crash[0])), StatsChartTone.Danger));
        return lines;
    }

    /// <summary>Левый край подсказки шириной boxWidth: справа от черты, не влезает — слева, но не за левый край элемента.</summary>
    public double TipLeft(double x, double boxWidth) => Math.Max(0, x + 10 + boxWidth > PlotRight ? x - 10 - boxWidth : x + 10);

    // ---- чистые расчёты

    /// <summary>Непрерывные отрезки: пропуск (null) разрывает линию.</summary>
    public static List<List<(int I, double V)>> Runs(IReadOnlyList<StatsPoint> points, Func<StatsPoint, double?> value)
    {
        var runs = new List<List<(int, double)>>();
        List<(int, double)>? cur = null;
        for (var i = 0; i < points.Count; i++)
        {
            if (value(points[i]) is { } v)
            {
                if (cur is null) runs.Add(cur = []);
                cur.Add((i, v));
            }
            else cur = null;
        }
        return runs;
    }

    /// <summary>Верх шкалы памяти: 1, 2, 4, 6, 8, 12, 16… ГБ (с запасом 5 %, чтобы линия не легла на край).</summary>
    public static double NiceGb(double gb)
    {
        foreach (var step in new double[] { 1, 2, 4, 6, 8, 12, 16, 24, 32, 48, 64 })
            if (gb <= step * 0.95) return step;
        return Math.Ceiling(gb / 16) * 16;
    }

    /// <summary>Целые деления от 0 до max: шаг 1 (до 4), 2 (до 10), 5 (до 25), дальше 10.</summary>
    public static List<double> IntTicks(int max)
    {
        var step = max <= 4 ? 1 : max <= 10 ? 2 : max <= 25 ? 5 : 10;
        var list = new List<double>();
        for (var v = 0; v <= max; v += step) list.Add(v);
        return list;
    }

    /// <summary>
    /// Подписи времени за срок: сутки — каждые 3 часа («12:00»), неделя — дни недели посередине дня, месяц — дата раз в
    /// 5 дней, считая от сегодняшнего.
    /// </summary>
    public static IEnumerable<(DateTime Time, string Text)> TimeLabels(StatsReport r)
    {
        switch (r.Period)
        {
            case StatsPeriod.Day:
                for (var t = r.From.Date.AddHours(r.From.Hour / 3 * 3 + 3); t < r.To; t = t.AddHours(3))
                    yield return (t, Dates.Clock(t));
                break;
            case StatsPeriod.Week:
                for (var t = r.From.Date.AddDays(1); t < r.To; t = t.AddDays(1))
                    yield return (t.AddHours(12), Dates.Weekday(t));
                break;
            default:
                for (var t = r.From.Date.AddDays(1); t < r.To; t = t.AddDays(1))
                    if ((r.To.Date - t).Days % 5 == 0) yield return (t, Dates.DayMonth(t));
                break;
        }
    }

    /// <summary>Первая строка подсказки: «чт 08.10, 21:10–21:15».</summary>
    public static string TimeRange(DateTime t, TimeSpan step) =>
        $"{Dates.Weekday(t)} {Dates.DayMonth(t)}, {Dates.Clock(t)}–{Dates.Clock(t + step)}";

    /// <summary>Подряд одинаковые точки убрать: от них только лишние нулевые отрезки.</summary>
    private static List<StatsChartPoint> Distinct(IEnumerable<StatsChartPoint> points)
    {
        var list = new List<StatsChartPoint>();
        foreach (var p in points)
            if (list.Count == 0 || list[^1] != p) list.Add(p);
        return list;
    }
}
