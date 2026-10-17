using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// График статистики сервера: точки с равным шагом, пропуски (сервер не работал) — разрывы, вылеты — красные черты.
/// Наведи мышь — вертикальная черта и подсказка со временем и значениями. Рисуется сам, без сторонних библиотек;
/// раскладку (шкалы, деления, ступеньки, подписи) считает <see cref="StatsChartLayout"/>, здесь — только кисти и текст.
/// </summary>
public sealed class StatsChart : Control
{
    public static readonly StyledProperty<StatsReport?> ReportProperty =
        AvaloniaProperty.Register<StatsChart, StatsReport?>(nameof(Report));

    public static readonly StyledProperty<StatsChartKind> KindProperty =
        AvaloniaProperty.Register<StatsChart, StatsChartKind>(nameof(Kind));

    // фокуса не берёт: у простого Control он и так выключен
    static StatsChart() => AffectsRender<StatsChart>(ReportProperty, KindProperty);

    public StatsReport? Report
    {
        get => GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    public StatsChartKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private int? _hover;

    /// <summary>Шаг под мышью (null — мыши над графиком нет); для тестов.</summary>
    internal int? HoverIndex => _hover;

    /// <summary>Раскладка под нынешний размер элемента.</summary>
    private StatsChartLayout Measured() => new(Report, Kind, Bounds.Width, Bounds.Height);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var index = Measured().IndexAt(e.GetPosition(this).X);
        if (index == _hover) return;
        _hover = index;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    private IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private IBrush Tone(StatsChartTone tone) => tone switch
    {
        StatsChartTone.Text => Res("Forest.Text", Brushes.White),
        StatsChartTone.Good => Res("Forest.Good", Brushes.YellowGreen),
        StatsChartTone.Warn => Res("Forest.Warn", Brushes.Orange),
        StatsChartTone.Danger => Res("Forest.Danger", Brushes.IndianRed),
        _ => Res("Forest.Muted", Brushes.Gray),
    };

    public override void Render(DrawingContext dc)
    {
        // прозрачный фон по всей площади: мышь «видит» график целиком, а не только нарисованное
        // (без него подсказка мигала бы между линиями)
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var g = Measured();
        if (!g.CanDraw) return;

        var lineBrush = Res("Forest.Line", Brushes.DimGray);
        var line = new Pen(lineBrush, 1);
        var dashed = Dashed(lineBrush, 1, 3, 4);
        var muted = Tone(StatsChartTone.Muted);

        // ---- шкалы
        foreach (var t in g.Ticks())
        {
            dc.DrawLine(t.IsBase ? line : dashed, new Point(StatsChartLayout.Left, t.Y), new Point(g.PlotRight, t.Y));
            Text(dc, t.Label, StatsChartLayout.Left - 6, t.Y, muted, HorizontalAlignment.Right);
            if (t.RightLabel is { } right) Text(dc, right, g.PlotRight + 6, t.Y, muted, HorizontalAlignment.Left);
        }

        // ---- подписи времени
        foreach (var label in g.TimeLabels())
            Text(dc, label.Text, label.X, g.PlotBottom + 12, muted, HorizontalAlignment.Center);

        // ---- данные
        if (Kind == StatsChartKind.Players)
        {
            var accent = Res("Forest.Accent", Brushes.Goldenrod);
            IBrush fill = accent is ISolidColorBrush solid ? new ImmutableSolidColorBrush(solid.Color, 0.14) : accent;
            var top = new Pen(accent, 1.6);
            foreach (var area in g.PlayerAreas()) dc.DrawGeometry(fill, null, Figure(area, closed: true));
            foreach (var edge in g.PlayerTops()) dc.DrawGeometry(null, top, Figure(edge, closed: false));
        }
        else
        {
            // изломы линий — скруглённые: острым углом линия выстреливала бы иглой
            var memory = new Pen(Tone(StatsChartTone.Good), 1.6, lineJoin: PenLineJoin.Round);
            var cpu = new Pen(Tone(StatsChartTone.Warn), 1.4, lineJoin: PenLineJoin.Round);
            foreach (var l in g.MemoryLines()) dc.DrawGeometry(null, memory, Figure(l, closed: false));
            foreach (var l in g.CpuLines()) dc.DrawGeometry(null, cpu, Figure(l, closed: false));

            // предел памяти машины, когда пик к нему подобрался: пунктир цветом памяти и подпись над ним слева
            if (g is { LimitY: { } limitY, LimitGb: { } limitGb })
            {
                var good = Tone(StatsChartTone.Good);
                dc.DrawLine(Dashed(good, 1, 4, 3), new Point(StatsChartLayout.Left, limitY), new Point(g.PlotRight, limitY));
                var label = Format(Loc.T("stats.limit", ServerStatsViewModel.LimitGb(limitGb * 1024)), good, 11);
                dc.DrawText(label, new Point(StatsChartLayout.Left + 6, Math.Max(StatsChartLayout.Top, limitY - label.Height - 1)));
            }
        }

        // ---- вылеты: черта на обоих графиках, подпись — на верхнем, справа от черты (у края — слева)
        var danger = Tone(StatsChartTone.Danger);
        var crashPen = Dashed(danger, 1.5, 2, 2);
        var crashes = g.CrashXs();
        foreach (var x in crashes)
            dc.DrawLine(crashPen, new Point(x, StatsChartLayout.Top), new Point(x, g.PlotBottom));
        if (Kind == StatsChartKind.Players && crashes.Count > 0)
        {
            var mark = Format(Loc.T("stats.crashMark"), danger, 11);
            foreach (var at in g.CrashLabels(mark.Width, mark.Height))
                dc.DrawText(mark, new Point(at.X, at.Y));
        }

        // ---- подсказка под мышью
        if (_hover is { } hi && hi < Report!.Points.Count)
        {
            var x = g.HoverX(hi);
            dc.DrawLine(Dashed(Tone(StatsChartTone.Text), 1, 2, 2),
                new Point(x, StatsChartLayout.Top), new Point(x, g.PlotBottom));
            Tooltip(dc, g, g.TipLines(hi), x);
        }
    }

    private void Tooltip(DrawingContext dc, StatsChartLayout g, IReadOnlyList<StatsChartTipLine> lines, double x)
    {
        var texts = lines.Select(l => Format(l.Text, Tone(l.Tone), 12)).ToList();
        var width = texts.Max(t => t.Width) + 20;
        var height = texts.Sum(t => t.Height) + 14;
        var box = new Rect(g.TipLeft(x, width), StatsChartLayout.Top + 4, width, height);
        dc.DrawRectangle(Res("Forest.Raised", Brushes.Black), new Pen(Res("Forest.Line", Brushes.Gray), 1), box, 6, 6);
        var y = box.Top + 7;
        foreach (var t in texts)
        {
            dc.DrawText(t, new Point(box.Left + 10, y));
            y += t.Height;
        }
    }

    /// <summary>
    /// Пунктир: штрих и промежуток — в толщинах линии. Концы штрихов квадратные (DashCap=Square):
    /// штрих выходит длиннее промежутка на толщину, с плоскими концами пунктир был бы заметно реже.
    /// </summary>
    private static Pen Dashed(IBrush brush, double thickness, double dash, double gap) =>
        new(brush, thickness, new DashStyle([dash, gap], 0), PenLineCap.Square);

    private static StreamGeometry Figure(IReadOnlyList<StatsChartPoint> points, bool closed)
    {
        var geometry = new StreamGeometry();
        using var c = geometry.Open();
        c.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: closed);
        foreach (var p in points.Skip(1)) c.LineTo(new Point(p.X, p.Y));
        c.EndFigure(closed);
        return geometry;
    }

    private FormattedText Format(string text, IBrush brush, double size) =>
        new(text, Loc.Culture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this)), size, brush);

    private void Text(DrawingContext dc, string text, double x, double y, IBrush brush, HorizontalAlignment align)
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

    protected override AutomationPeer OnCreateAutomationPeer() => new ChartPeer(this);

    /// <summary>
    /// График для специальных возможностей и стенда: картинка с AutomationId из разметки и названием графика (у
    /// стандартного пира простого элемента его нет в дереве совсем — график не находился бы по AutomationId).
    /// </summary>
    private sealed class ChartPeer(StatsChart owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(StatsChart);

        protected override string? GetNameCore() =>
            base.GetNameCore() is { Length: > 0 } name ? name
                : Loc.T(owner.Kind == StatsChartKind.Players ? "stats.chartPlayers" : "stats.chartResources");

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }
}
