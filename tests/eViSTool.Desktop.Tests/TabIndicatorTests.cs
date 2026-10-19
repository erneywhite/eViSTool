using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Полоска выбранной вкладки (docs/design.md, раздел 3): одна на ряд, едет к новой вкладке и подстраивает ширину за
/// 250 мс — только RenderTransform, раскладка не трогается; первый показ, новое место той же вкладки, спрятанный ряд и
/// системное «без анимаций» — сразу на месте. Где полоска на экране, смотрим по кадрам: после каждого тика отрисовки —
/// то, что было бы нарисовано.
/// </summary>
public sealed class TabIndicatorTests
{
    private static readonly Uri Host = new("avares://eViSTool.Desktop.Tests");

    /// <summary>Ряд вкладок-счётчиков в лесной теме: под рядом линия 1 px, в панели-обёртке — полоска; выбрана первая.</summary>
    private sealed class Stand
    {
        public required Window Window { get; init; }
        public required Panel Host { get; init; }
        public required RadioButton[] Tabs { get; init; }
        public required TabIndicator Bar { get; init; }
    }

    private static Stand Show(bool still = false, params string[] titles)
    {
        if (titles.Length == 0) titles = ["4 установлено", "0 обновлений", "0 проблем", "1 выключено"];
        var window = new Window { Width = 900, Height = 300 };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(Host) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(Host) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") });
        window.Styles.Add(new StyleInclude(Host) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") });
        if (still) Motion.SetEnabled(window, false);
        var theme = (ControlTheme)window.FindResource("Forest.StatTab")!;
        // без GroupName: соседние радиокнопки одной панели и так одна группа, а отметку здесь ставит сам тест
        var tabs = titles.Select(t => new RadioButton { Theme = theme, Content = t }).ToArray();
        tabs[0].IsChecked = true;
        var bar = new TabIndicator { Background = Brushes.Gray };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.AddRange(tabs);
        var host = new Panel { Children = { row, bar } };
        window.Content = new Border
        {
            Margin = new Thickness(20), Padding = new Thickness(0, 14, 0, 0), BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Brushes.DimGray, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = host,
        };
        window.Show();
        Mouse.Prepare(window);
        return new Stand { Window = window, Host = host, Tabs = tabs, Bar = bar };
    }

    /// <summary>Где полоска на экране (в координатах панели-обёртки): левый край, верх и ширина — по её RenderTransform.</summary>
    private static Rect Seen(TabIndicator bar)
    {
        var m = bar.RenderTransform?.Value ?? Matrix.Identity;
        return new Rect(m.M31, m.M32, TabIndicator.BaseWidth * m.M11, bar.Height);
    }

    /// <summary>Где полоска должна стоять под вкладкой: во всю её ширину, нижний край — на линии ряда (1 px ниже вкладки).</summary>
    private static Rect Under(RadioButton tab, Visual host)
    {
        var at = tab.TranslatePoint(default, host)!.Value;
        return new Rect(at.X, at.Y + tab.Bounds.Height + TabIndicator.Drop - 2, tab.Bounds.Width, 2);
    }

    private static void AssertAt(Rect expected, Rect seen)
    {
        Assert.Equal(expected.X, seen.X, 3);
        Assert.Equal(expected.Y, seen.Y, 3);
        Assert.Equal(expected.Width, seen.Width, 3);
    }

    /// <summary>Кадры за <paramref name="ms"/> миллисекунд: после каждого тика отрисовки — где полоска.</summary>
    private static async Task<List<Rect>> Frames(TabIndicator bar, int ms)
    {
        var frames = new List<Rect>();
        var watch = Stopwatch.StartNew();
        do
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            frames.Add(Seen(bar));
        } while (watch.ElapsedMilliseconds < ms);
        return frames;
    }

    [AvaloniaFact]
    public async Task First_show_puts_the_bar_under_the_checked_tab_at_once()
    {
        var s = Show();
        Assert.True(s.Bar.IsVisible);
        Assert.Same(s.Tabs[0], s.Bar.Tab);
        Assert.False(s.Bar.IsMoving);
        AssertAt(Under(s.Tabs[0], s.Host), Seen(s.Bar));
        Assert.All(await Frames(s.Bar, 150), seen => AssertAt(Under(s.Tabs[0], s.Host), seen));

        // полоска лежит на линии ряда: её нижний край — нижний край рамки под вкладками
        var border = (Border)s.Window.Content!;
        Assert.Equal(border.TranslatePoint(new Point(0, border.Bounds.Height), s.Window)!.Value.Y,
            s.Host.TranslatePoint(new Point(0, Seen(s.Bar).Bottom), s.Window)!.Value.Y, 3);
        // своего подчёркивания у вкладки-счётчика больше нет: полоска одна на ряд
        Assert.DoesNotContain(s.Tabs[0].GetVisualDescendants().OfType<Border>(), b => b.Name == "Underline");
        s.Window.Close();
    }

    [AvaloniaFact]
    public async Task Bar_rides_to_the_new_tab_and_takes_its_width()
    {
        var s = Show();
        var from = Under(s.Tabs[0], s.Host);
        var to = Under(s.Tabs[3], s.Host);
        Assert.NotEqual(from.Width, to.Width, 3); // ширина тоже меняется

        s.Tabs[3].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(s.Tabs[3], s.Bar.Tab);
        Assert.True(s.Bar.IsMoving);
        var frames = await Frames(s.Bar, 450);

        // едет, а не прыгает: по дороге — положения между вкладками, всё время вправо, ширина — между двумя
        var between = frames.Where(f => f.X > from.X + 0.5 && f.X < to.X - 0.5).ToList();
        Assert.True(between.Count >= 2, $"кадров по дороге: {between.Count}");
        Assert.All(between, f => Assert.InRange(f.Width, Math.Min(from.Width, to.Width) - 0.01, Math.Max(from.Width, to.Width) + 0.01));
        for (var i = 1; i < frames.Count; i++) Assert.True(frames[i].X >= frames[i - 1].X - 0.001, $"кадр {i}: назад");

        // приехала (за 250 мс) и стоит на месте; в раскладке ничего не менялось — та же ширина и место
        Assert.Equal(TimeSpan.FromMilliseconds(250), TabIndicator.MoveDuration);
        Assert.False(s.Bar.IsMoving);
        AssertAt(to, Seen(s.Bar));
        Assert.Equal(new Rect(0, 0, TabIndicator.BaseWidth, 2), s.Bar.Bounds);
        s.Window.Close();
    }

    [AvaloniaFact]
    public async Task Switching_again_on_the_way_continues_from_where_the_bar_is()
    {
        var s = Show();
        s.Tabs[1].IsChecked = true;
        var before = await Frames(s.Bar, 80);
        var watch = Stopwatch.StartNew();
        s.Tabs[3].IsChecked = true;
        var first = await Frames(s.Bar, 0); // один кадр
        var quick = watch.ElapsedMilliseconds < 120;
        var after = first.Concat(await Frames(s.Bar, 450)).ToList();

        // не вернулась к началу и не прыгнула к концу: путь сплошной, вправо, первый кадр после смены — ещё в пути
        var to = Under(s.Tabs[3], s.Host);
        var frames = before.Concat(after).ToList();
        for (var i = 1; i < frames.Count; i++) Assert.True(frames[i].X >= frames[i - 1].X - 0.001, $"кадр {i}: назад");
        if (quick && before[^1].X < to.X - 1) Assert.True(after[0].X < to.X - 0.5, "прыгнула к концу");
        AssertAt(to, Seen(s.Bar));
        Assert.False(s.Bar.IsMoving);
        s.Window.Close();
    }

    [AvaloniaFact]
    public async Task Same_tab_wider_or_moved_stands_in_place_at_once()
    {
        var s = Show();
        s.Tabs[2].IsChecked = true;
        await Frames(s.Bar, 400);

        // сменился язык или число на вкладке: вкладки шире — полоска сразу на новом месте, без езды
        s.Tabs[0].Content = "4 установлено модов";
        s.Tabs[2].Content = "0 проблем с зависимостями";
        var frames = await Frames(s.Bar, 150);
        Assert.False(s.Bar.IsMoving);
        Assert.All(frames, f => AssertAt(Under(s.Tabs[2], s.Host), f));
        s.Window.Close();
    }

    [AvaloniaFact]
    public async Task Without_motion_bar_jumps()
    {
        var s = Show(still: true);
        s.Tabs[3].IsChecked = true;
        var frames = await Frames(s.Bar, 150);
        Assert.False(s.Bar.IsMoving);
        Assert.All(frames, f => AssertAt(Under(s.Tabs[3], s.Host), f));
        s.Window.Close();
    }

    [AvaloniaFact]
    public async Task Switch_in_a_hidden_row_shows_in_place()
    {
        var s = Show();
        s.Host.IsVisible = false;
        s.Tabs[3].IsChecked = true;
        await Frames(s.Bar, 50);
        s.Host.IsVisible = true;
        var frames = await Frames(s.Bar, 150);
        Assert.False(s.Bar.IsMoving);
        Assert.All(frames, f => AssertAt(Under(s.Tabs[3], s.Host), f));
        s.Window.Close();
    }

    [AvaloniaFact]
    public async Task No_checked_tab_hides_the_bar()
    {
        var s = Show();
        foreach (var tab in s.Tabs) tab.IsChecked = false;
        await Frames(s.Bar, 50);
        Assert.False(s.Bar.IsVisible);
        Assert.Null(s.Bar.Tab);

        // снова выбрали — полоска сразу под вкладкой: ехать неоткуда
        s.Tabs[2].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(s.Bar.IsVisible);
        Assert.False(s.Bar.IsMoving);
        AssertAt(Under(s.Tabs[2], s.Host), Seen(s.Bar));

        // выбранную спрятали — полоски нет
        s.Tabs[2].IsVisible = false;
        await Frames(s.Bar, 50);
        Assert.False(s.Bar.IsVisible);
        s.Window.Close();
    }

    [AvaloniaFact]
    public void Bar_takes_no_room_in_the_row()
    {
        var s = Show(false, "А", "Б");
        var row = s.Host.Children[0];
        Assert.True(row.Bounds.Width < TabIndicator.BaseWidth);
        Assert.Equal(row.Bounds.Width, s.Host.Bounds.Width, 3); // ряд из коротких вкладок не стал шире из-за полоски
        Assert.Equal(row.Bounds.Height, s.Host.Bounds.Height, 3);
        s.Window.Close();
    }
}
