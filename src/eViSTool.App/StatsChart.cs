using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;

namespace eViSTool.App;

/// <summary>Что рисует график статистики.</summary>
public enum StatsChartKind
{
    /// <summary>Игроки в игре — ступеньками с заливкой.</summary>
    Players,
    /// <summary>Память (ГБ, ось слева) и процессор (%, ось справа) — двумя линиями.</summary>
    Resources,
}

/// <summary>
/// График статистики сервера: точки с равным шагом, пропуски (сервер не работал) — разрывы, вылеты — красные черты.
/// Наведи мышь — вертикальная черта и подсказка со временем и значениями. Рисуется сам, без сторонних библиотек.
/// </summary>
public sealed class StatsChart : FrameworkElement
{
    public static readonly DependencyProperty ReportProperty = DependencyProperty.Register(nameof(Report), typeof(StatsReport), typeof(StatsChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(StatsChartKind), typeof(StatsChart),
        new FrameworkPropertyMetadata(StatsChartKind.Players, FrameworkPropertyMetadataOptions.AffectsRender));

    public StatsReport? Report
    {
        get => (StatsReport?)GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    public StatsChartKind Kind
    {
        get => (StatsChartKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private const double Left = 36, Right = 40, Top = 8, Bottom = 22;
    private int? _hover;

    public StatsChart()
    {
        Focusable = false;
        // без фона мышь видна только над нарисованным — подсказка мигала бы между линиями
        SnapsToDevicePixels = true;
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = IndexAt(e.GetPosition(this).X);
        if (index == _hover) return;
        _hover = index;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    private int? IndexAt(double x)
    {
        if (Report is not { Points.Count: > 0 } r) return null;
        var w = ActualWidth - Left - Right;
        if (w <= 0 || x < Left || x > Left + w) return null;
        return Math.Clamp((int)((x - Left) / w * r.Points.Count), 0, r.Points.Count - 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth - Left - Right;
        var h = ActualHeight - Top - Bottom;
        // прозрачный фон по всей площади: мышь «видит» график целиком
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Report is not { Points.Count: > 0 } r || w <= 20 || h <= 20) return;

        var line = new Pen(Res("Forest.Line", Brushes.DimGray), 1);
        var dashed = new Pen(line.Brush, 1) { DashStyle = new DashStyle([3, 4], 0) };
        var muted = Res("Forest.Muted", Brushes.Gray);
        var n = r.Points.Count;
        double X(int i) => Left + w * i / n;
        double Xt(DateTime t) => Left + w * (t - r.Points[0].Time).Ticks / (double)(r.Step.Ticks * n);

        // ---- шкалы
        double max1, max2 = 100;
        if (Kind == StatsChartKind.Players)
            max1 = Math.Max(2, r.Points.Max(p => p.Players ?? 0));
        else
            max1 = NiceGb(r.Points.Max(p => p.MemoryMb ?? 0) / 1024);
        double Y(double v, double max) => Top + h - h * Math.Clamp(v / max, 0, 1);

        var ticks = Kind == StatsChartKind.Players ? IntTicks((int)max1) : [0, max1 / 2, max1];
        foreach (var t in ticks)
        {
            var y = Math.Round(Y(t, max1)) + 0.5;
            dc.DrawLine(t == 0 ? line : dashed, new Point(Left, y), new Point(Left + w, y));
            Text(dc, Kind == StatsChartKind.Players ? t.ToString("0", CultureInfo.CurrentCulture) : t.ToString("0.#", CultureInfo.CurrentCulture),
                Left - 6, y, muted, HorizontalAlignment.Right);
            if (Kind == StatsChartKind.Resources)
                Text(dc, (t / max1 * 100).ToString("0", CultureInfo.CurrentCulture) + " %", Left + w + 6, y, muted, HorizontalAlignment.Left);
        }

        // ---- подписи времени
        foreach (var (t, label) in TimeLabels(r))
        {
            var x = Xt(t);
            if (x < Left - 1 || x > Left + w + 1) continue;
            Text(dc, label, x, Top + h + 12, muted, HorizontalAlignment.Center);
        }

        // ---- данные
        if (Kind == StatsChartKind.Players)
        {
            var accent = Res("Forest.Accent", Brushes.Goldenrod);
            var fill = accent.Clone();
            fill.Opacity = 0.14;
            foreach (var run in Runs(r.Points, p => p.Players))
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(X(run[0].I), Y(0, max1)), true, true);
                    foreach (var (i, v) in run)
                    {
                        c.LineTo(new Point(X(i), Y(v, max1)), true, false);
                        c.LineTo(new Point(X(i + 1), Y(v, max1)), true, false);
                    }
                    c.LineTo(new Point(X(run[^1].I + 1), Y(0, max1)), true, false);
                }
                dc.DrawGeometry(fill, null, g);
                var top = new StreamGeometry();
                using (var c = top.Open())
                {
                    c.BeginFigure(new Point(X(run[0].I), Y(run[0].V, max1)), false, false);
                    foreach (var (i, v) in run)
                    {
                        c.LineTo(new Point(X(i), Y(v, max1)), true, false);
                        c.LineTo(new Point(X(i + 1), Y(v, max1)), true, false);
                    }
                }
                dc.DrawGeometry(null, new Pen(accent, 1.6), top);
            }
        }
        else
        {
            DrawLines(dc, r.Points, p => p.MemoryMb / 1024, v => Y(v, max1), X, new Pen(Res("Forest.Good", Brushes.YellowGreen), 1.6));
            DrawLines(dc, r.Points, p => p.Cpu, v => Y(v, max2), X, new Pen(Res("Forest.Warn", Brushes.Orange), 1.4));
        }

        // ---- вылеты
        var danger = Res("Forest.Danger", Brushes.IndianRed);
        var crashPen = new Pen(danger, 1.5) { DashStyle = new DashStyle([2, 2], 0) };
        var lastLabel = Rect.Empty; // вылеты почти подряд: подписи наползали друг на друга, вторую не рисуем — хватает черты
        foreach (var crash in r.Crashes.OrderBy(c => c))
        {
            var x = Math.Round(Xt(crash)) + 0.5;
            if (x < Left || x > Left + w) continue;
            dc.DrawLine(crashPen, new Point(x, Top), new Point(x, Top + h));
            // подпись — на верхнем графике, справа от черты (у края — слева)
            if (Kind == StatsChartKind.Players)
            {
                var label = Format(Loc.T("stats.crashMark"), danger, 11);
                var at = new Point(x + 4 + label.Width > Left + w ? x - 4 - label.Width : x + 4, Top);
                var box = new Rect(at.X - 4, at.Y, label.Width + 8, label.Height);
                if (lastLabel.IntersectsWith(box)) continue;
                dc.DrawText(label, at);
                lastLabel = box;
            }
        }

        // ---- подсказка под мышью
        if (_hover is { } hi && hi < n)
        {
            var p = r.Points[hi];
            var x = Math.Round(X(hi) + w / n / 2) + 0.5;
            dc.DrawLine(new Pen(Res("Forest.Text", Brushes.White), 1) { DashStyle = new DashStyle([2, 2], 0) }, new Point(x, Top), new Point(x, Top + h));
            var lines = new List<(string Text, Brush Brush)> { (TimeRange(p.Time, r.Step), muted) };
            if (p.Players is null) lines.Add((Loc.T("stats.tipOff"), muted));
            else if (Kind == StatsChartKind.Players) lines.Add((Loc.T("stats.tipPlayers", p.Players), Res("Forest.Text", Brushes.White)));
            else
            {
                lines.Add((Loc.T("stats.tipMemory", ((p.MemoryMb ?? 0) / 1024).ToString("0.0", CultureInfo.CurrentCulture)), Res("Forest.Good", Brushes.YellowGreen)));
                lines.Add((Loc.T("stats.tipCpu", (p.Cpu ?? 0).ToString("0", CultureInfo.CurrentCulture)), Res("Forest.Warn", Brushes.Orange)));
            }
            var crashHere = r.Crashes.Where(c => c >= p.Time && c < p.Time + r.Step).ToList();
            if (crashHere.Count > 0) lines.Add((Loc.T("stats.tipCrash", crashHere[0].ToString("HH:mm", CultureInfo.CurrentCulture)), danger));
            Tooltip(dc, lines, x, w);
        }
    }

    private void Tooltip(DrawingContext dc, List<(string Text, Brush Brush)> lines, double x, double w)
    {
        var texts = lines.Select(l => Format(l.Text, l.Brush, 12)).ToList();
        var bw = texts.Max(t => t.Width) + 20;
        var bh = texts.Sum(t => t.Height) + 14;
        var bx = x + 10 + bw > Left + w ? x - 10 - bw : x + 10;
        var box = new Rect(Math.Max(0, bx), Top + 4, bw, bh);
        dc.DrawRoundedRectangle(Res("Forest.Raised", Brushes.Black), new Pen(Res("Forest.Line", Brushes.Gray), 1), box, 6, 6);
        var y = box.Top + 7;
        foreach (var t in texts)
        {
            dc.DrawText(t, new Point(box.Left + 10, y));
            y += t.Height;
        }
    }

    private static void DrawLines(DrawingContext dc, IList<StatsPoint> points, Func<StatsPoint, double?> value, Func<double, double> y,
        Func<int, double> x, Pen pen)
    {
        foreach (var run in Runs(points, value))
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x(run[0].I) + (x(1) - x(0)) / 2, y(run[0].V)), false, false);
                foreach (var (i, v) in run.Skip(1)) c.LineTo(new Point(x(i) + (x(1) - x(0)) / 2, y(v)), true, true);
                // одна точка — короткая черта, иначе её не видно
                if (run.Count == 1) c.LineTo(new Point(x(run[0].I + 1), y(run[0].V)), true, true);
            }
            dc.DrawGeometry(null, pen, g);
        }
    }

    /// <summary>Непрерывные отрезки: пропуск (null) разрывает линию.</summary>
    private static List<List<(int I, double V)>> Runs(IList<StatsPoint> points, Func<StatsPoint, double?> value)
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

    private static List<List<(int I, double V)>> Runs(IList<StatsPoint> points, Func<StatsPoint, int?> value) =>
        Runs(points, p => value(p) is { } v ? (double?)v : null);

    /// <summary>Верх шкалы памяти: 1, 2, 4, 6, 8, 12, 16… ГБ.</summary>
    private static double NiceGb(double gb)
    {
        foreach (var step in new double[] { 1, 2, 4, 6, 8, 12, 16, 24, 32, 48, 64 })
            if (gb <= step * 0.95) return step;
        return Math.Ceiling(gb / 16) * 16;
    }

    private static List<double> IntTicks(int max)
    {
        var step = max <= 4 ? 1 : max <= 10 ? 2 : max <= 25 ? 5 : 10;
        var list = new List<double>();
        for (var v = 0; v <= max; v += step) list.Add(v);
        return list;
    }

    private static IEnumerable<(DateTime, string)> TimeLabels(StatsReport r)
    {
        var c = Loc.Culture;
        switch (r.Period)
        {
            case StatsPeriod.Day:
                for (var t = r.From.Date.AddHours(r.From.Hour / 3 * 3 + 3); t < r.To; t = t.AddHours(3))
                    yield return (t, t.ToString("HH:mm", c));
                break;
            case StatsPeriod.Week:
                for (var t = r.From.Date.AddDays(1); t < r.To; t = t.AddDays(1))
                    yield return (t.AddHours(12), t.ToString("ddd", c));
                break;
            default:
                for (var t = r.From.Date.AddDays(1); t < r.To; t = t.AddDays(1))
                    if ((r.To.Date - t).Days % 5 == 0) yield return (t, t.ToString("dd.MM", c));
                break;
        }
    }

    private static string TimeRange(DateTime t, TimeSpan step)
    {
        var c = Loc.Culture;
        return $"{t.ToString("ddd dd.MM, HH:mm", c)}–{(t + step).ToString("HH:mm", c)}";
    }

    private FormattedText Format(string text, Brush brush, double size) =>
        new(text, Loc.Culture, FlowDirection.LeftToRight,
            new Typeface(TextElementFont(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private FontFamily TextElementFont() => (FontFamily)GetValue(System.Windows.Documents.TextElement.FontFamilyProperty);

    private void Text(DrawingContext dc, string text, double x, double y, Brush brush, HorizontalAlignment align)
    {
        var f = Format(text, brush, 11);
        var left = align switch
        {
            HorizontalAlignment.Right => x - f.Width,
            HorizontalAlignment.Center => x - f.Width / 2,
            _ => x,
        };
        dc.DrawText(f, new Point(left, y - f.Height / 2));
    }
}
