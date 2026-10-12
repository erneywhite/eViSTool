using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Desktop.Controls;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// График статистики: настоящая отрисовка Skia на выдуманных сутках (ступеньки игроков, линии памяти и процессора,
/// вылеты, подсказка под мышью). Переменная EVISTOOL_CHART_PNG = папка — туда ложатся PNG для сверки с графиком на WPF.
/// </summary>
public sealed class StatsChartTests : IDisposable
{
    private const int Margin = 20, Width = 700;
    private static readonly Color Surface = Color.Parse("#273129");
    private static readonly Color Accent = Color.Parse("#D4BD83");
    private static readonly Color Danger = Color.Parse("#E89B96");
    private static readonly Color Raised = Color.Parse("#303D32");

    private readonly CultureInfo? _uiCulture = CultureInfo.DefaultThreadCurrentUICulture;
    private readonly string _language = Loc.Instance.Language;

    // подписи на графике — по-русски, как на снимках окна (числа и часы — по региону машины, проверки от них не зависят)
    public StatsChartTests() => Loc.Instance.SetLanguage("ru");

    public void Dispose()
    {
        Loc.Instance.SetLanguage(_language);
        CultureInfo.DefaultThreadCurrentUICulture = _uiCulture;
    }

    private static (Window Window, StatsChart Chart) Show(StatsChartKind kind, double height)
    {
        var chart = new StatsChart
        {
            Report = StatsSamples.Day(), Kind = kind, Width = Width, Height = height,
            Margin = new Thickness(Margin), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        var window = new Window
        {
            Width = Width + 2 * Margin, Height = height + 2 * Margin, Background = new SolidColorBrush(Surface),
            Content = new Panel { Children = { chart } },
        };
        // кисти Forest.* и шрифт окна — как в программе
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://eViSTool.Desktop.Tests"))
        {
            Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml"),
        });
        TextElement.SetFontFamily(window, new FontFamily(Program.UiFont));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, chart);
    }

    private static WriteableBitmap Frame(Window window, string? name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("кадр не отрисовался");
        if (name is not null && Environment.GetEnvironmentVariable("EVISTOOL_CHART_PNG") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name + ".avalonia.png"), PngBitmapEncoderOptions.Default);
        }
        return frame;
    }

    /// <summary>Цвет пикселя в координатах графика (без полей окна).</summary>
    private static Color Pixel(WriteableBitmap frame, double x, double y)
    {
        var (px, py) = ((int)(x + Margin), (int)(y + Margin));
        var bytes = new byte[4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(px, py, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        return frame.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    private static Color Blend(Color under, Color over, double alpha) => Color.FromRgb(
        (byte)Math.Round(under.R + (over.R - under.R) * alpha),
        (byte)Math.Round(under.G + (over.G - under.G) * alpha),
        (byte)Math.Round(under.B + (over.B - under.B) * alpha));

    private static void Near(Color expected, Color actual, int tolerance = 6) =>
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance
                                                                 && Math.Abs(expected.B - actual.B) <= tolerance,
            $"ждали {expected}, а там {actual}");

    /// <summary>Тонкая линия со сглаживанием: цвет близок, но не точен.</summary>
    private static bool Close(Color expected, Color actual) =>
        Math.Abs(expected.R - actual.R) <= 40 && Math.Abs(expected.G - actual.G) <= 40 && Math.Abs(expected.B - actual.B) <= 40;

    [AvaloniaFact]
    public void Players_are_drawn_as_filled_steps_with_crash_marks()
    {
        var (window, chart) = Show(StatsChartKind.Players, 150);
        var frame = Frame(window, "players");
        var g = new StatsChartLayout(chart.Report, StatsChartKind.Players, Width, 150);

        // середина шага, где кто-то играл: заливка — песок с прозрачностью 0,14 поверх карточки
        var busy = chart.Report!.Points.FindIndex(p => p.Players >= 2);
        Assert.True(busy >= 0);
        Near(Blend(Surface, Accent, 0.14), Pixel(frame, g.X(busy) + (g.X(1) - g.X(0)) / 2, g.Y(0.5, g.Max)));

        // пропуск (сервер не работал) — пусто: цвет карточки
        var off = chart.Report.Points.FindIndex(p => p.Players is null);
        Near(Surface, Pixel(frame, g.X(off) + (g.X(1) - g.X(0)) / 2, g.Y(0.5, g.Max)));

        // черта вылета — красный пунктир: хоть одна из трёх соседних точек по высоте — красная
        var crash = g.CrashXs()[0];
        var column = Enumerable.Range(0, 3).Select(dy => Pixel(frame, crash - 0.5, g.PlotBottom - 30 + dy)).ToList();
        Assert.Contains(column, c => Close(Danger, c));
        // два вылета почти подряд — подпись одна
        Assert.Equal(2, g.CrashXs().Count);
        Assert.Single(g.CrashLabels(30, 15));
        window.Close();
    }

    [AvaloniaFact]
    public void Resources_draw_both_lines_and_the_right_scale()
    {
        var (window, chart) = Show(StatsChartKind.Resources, 130);
        var frame = Frame(window, "resources");
        var g = new StatsChartLayout(chart.Report, StatsChartKind.Resources, Width, 130);
        Assert.Equal(4, g.Max); // пик памяти около 3 ГБ — шкала до 4
        Assert.Equal(["0 %", "50 %", "100 %"], g.Ticks().Select(t => t.RightLabel));

        // посреди отрезка линии — её цвет: память зелёная, процессор янтарный
        var memory = g.MemoryLines()[0];
        var cpu = g.CpuLines()[0];
        Assert.True(Close(Color.Parse("#B0C898"), Pixel(frame, memory[10].X, memory[10].Y)));
        Assert.True(Close(Color.Parse("#EDBF82"), Pixel(frame, cpu[10].X, cpu[10].Y)));
        window.Close();
    }

    [AvaloniaFact]
    public void Hover_shows_line_and_tooltip_and_leaving_hides_them()
    {
        var (window, chart) = Show(StatsChartKind.Players, 150);
        var g = new StatsChartLayout(chart.Report, StatsChartKind.Players, Width, 150);
        var index = chart.Report!.Points.FindIndex(p => p.Players >= 2);
        var x = g.HoverX(index);

        window.MouseMove(new Point(x + Margin, 60 + Margin));
        Assert.Equal(index, chart.HoverIndex);
        var frame = Frame(window, "players-hover");
        // подсказка справа от черты: в её левом поле — приподнятый фон
        Near(Raised, Pixel(frame, x + 14, StatsChartLayout.Top + 4 + 10));

        window.MouseMove(new Point(2, 2)); // ушли с графика на поле окна
        Assert.Null(chart.HoverIndex);
        Near(Surface, Pixel(Frame(window, null), x + 14, StatsChartLayout.Top + 4 + 10));
        window.Close();
    }

    [AvaloniaFact]
    public void Hover_on_resources_lists_memory_cpu_and_crash()
    {
        var (window, chart) = Show(StatsChartKind.Resources, 130);
        var g = new StatsChartLayout(chart.Report, StatsChartKind.Resources, Width, 130);
        var crash = chart.Report!.Crashes.Min();
        var index = chart.Report.Points.FindIndex(p => crash >= p.Time && crash < p.Time + chart.Report.Step);

        window.MouseMove(new Point(g.HoverX(index) + Margin, 60 + Margin));
        Assert.Equal(index, chart.HoverIndex);
        Frame(window, "resources-hover");
        var tones = g.TipLines(index).Select(l => l.Tone).ToList();
        Assert.Equal(StatsChartTone.Muted, tones[0]);
        Assert.Contains(StatsChartTone.Danger, tones);
        window.Close();
    }

    [AvaloniaFact]
    public void Chart_is_an_image_for_automation_with_its_id_and_title()
    {
        var chart = new StatsChart { Kind = StatsChartKind.Resources };
        Avalonia.Automation.AutomationProperties.SetAutomationId(chart, "StatsChartResources");
        var window = new Window { Content = chart };
        window.Show();

        var peer = ControlAutomationPeer.CreatePeerForElement(chart);
        Assert.Equal("StatsChartResources", peer.GetAutomationId());
        Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
        Assert.Equal(Loc.T("stats.chartResources"), peer.GetName());
        Assert.True(peer.IsControlElement());
        Assert.False(chart.Focusable);
        window.Close();
    }

    [AvaloniaFact]
    public void Empty_report_draws_nothing_and_has_no_hover()
    {
        var chart = new StatsChart { Report = null, Width = 300, Height = 120 };
        var window = new Window { Content = chart, Width = 300, Height = 120 };
        window.Show();
        window.MouseMove(new Point(150, 60));
        Assert.Null(chart.HoverIndex);
        Assert.NotNull(window.CaptureRenderedFrame());
        window.Close();
    }
}

/// <summary>
/// Выдуманные сутки сервера: два вылета почти подряд, остановка, запуск на три минуты (одна точка), пропуски, трое
/// игроков (один сейчас в игре), всплеск процессора. На этих данных график сверялся с графиком окна на WPF при переносе.
/// </summary>
internal static class StatsSamples
{
    public static readonly DateTime Now = new(2026, 10, 9, 18, 7, 0);

    public static StatsReport Day() => StatsReport.Build(Entries(), StatsPeriod.Day, Now);

    private static readonly (double From, double To, StatsKind? End)[] Ups =
    [
        (-30, -14, StatsKind.Crash),
        (-13.95, -13.9, StatsKind.Crash),
        (-13.3, -6, StatsKind.Down),
        (-5.2, -5.15, StatsKind.Down),
        (-4, 0, null),
    ];

    private static readonly (string Name, double From, double To)[] Sessions =
    [
        ("Erney", -26, -20), ("Lev", -22, -14.5), ("Masha", -21, -16), ("Erney", -12, -7), ("Lev", -11.5, -10),
        ("Masha", -3, 0), ("Erney", -2.5, -0.2), ("Gosha", -1, -0.5),
    ];

    public static List<StatsEntry> Entries()
    {
        var list = new List<StatsEntry>();
        foreach (var (from, to, end) in Ups)
        {
            var start = Now.AddMinutes(Math.Round(from * 60));
            var stop = Now.AddMinutes(Math.Round(to * 60));
            list.Add(new StatsEntry(start, StatsKind.Up));
            for (var t = start; t < stop; t = t.AddMinutes(1))
            {
                var players = Sessions.Count(s => t >= Now.AddHours(s.From) && t < Now.AddHours(s.To));
                var minutes = (t - start).TotalMinutes;
                var memory = (long)(1500 + 500 * (1 - Math.Exp(-minutes / 90)) + players * 220 + 60 * Math.Sin(minutes / 17));
                var spike = Math.Abs((t - Now.AddHours(-9)).TotalMinutes) < 6 ? 70 : 0;
                var cpu = Math.Clamp(4 + players * 7 + 3 * Math.Sin(minutes / 11) + spike, 0, 100);
                list.Add(new StatsEntry(t, StatsKind.Sample, players, memory, Math.Round(cpu, 1)));
            }
            if (end is { } kind) list.Add(new StatsEntry(stop, kind));
        }
        foreach (var (name, from, to) in Sessions)
        {
            list.Add(new StatsEntry(Now.AddHours(from), StatsKind.Join, Name: name));
            if (to < 0) list.Add(new StatsEntry(Now.AddHours(to), StatsKind.Leave, Name: name));
        }
        return list;
    }
}
