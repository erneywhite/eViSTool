using System.Diagnostics;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Platform;
using eViSTool.Desktop.Views;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Движение (docs/design.md, раздел 3): показанная страница или вкладка проявляется — прозрачность 0 → 1 и сдвиг на
/// 6 px снизу — и в конце стоит на месте без своих значений; при первом показе окна и в спрятанной панели проявления
/// нет. Системное «без анимаций»: настройку читает SystemMotion (здесь — из подмены), тогда проявление сразу, переходы
/// темы мгновенные, бегущая полоска стоит.
/// </summary>
public sealed class MotionTests
{
    private static readonly Uri Host = new("avares://eViSTool.Desktop.Tests");

    private static double Shift(Visual visual) => visual.RenderTransform?.Value.M32 ?? 0;

    private static Matrix MatrixOf(ITransform? transform) => transform?.Value ?? Matrix.Identity;

    private static T Part<T>(Control owner, string name) where T : Control =>
        owner.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Tick()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    // ---------- системная настройка

    /// <summary>Подмена системы: что ответить и сколько раз спросили.</summary>
    private sealed class FakeSystem(bool windows = false, bool linux = false, bool? spi = null, string? gsettings = null) : SystemMotion.ISource
    {
        public bool Throws { get; init; }
        public int Asked { get; private set; }
        public bool IsWindows => windows;
        public bool IsLinux => linux;

        public bool? WindowsAnimation()
        {
            Asked++;
            return Throws ? throw new InvalidOperationException("SPI") : spi;
        }

        public string? GnomeAnimation()
        {
            Asked++;
            return Throws ? throw new InvalidOperationException("gsettings") : gsettings;
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, true)] // не прочиталось — включены
    public void Windows_follows_show_animations_setting(bool? spi, bool expected) =>
        Assert.Equal(expected, SystemMotion.Read(new FakeSystem(windows: true, spi: spi)));

    [Theory]
    [InlineData("true\n", true)]
    [InlineData("false\n", false)]
    [InlineData("  false  ", false)]
    [InlineData(null, true)]   // gsettings нет или нет схемы GNOME
    [InlineData("", true)]
    [InlineData("@mb nothing", true)]
    public void Linux_follows_gnome_enable_animations(string? output, bool expected) =>
        Assert.Equal(expected, SystemMotion.Read(new FakeSystem(linux: true, gsettings: output)));

    [Fact]
    public void Other_systems_have_animations_without_asking()
    {
        var system = new FakeSystem(spi: false, gsettings: "false");
        Assert.True(SystemMotion.Read(system));
        Assert.Equal(0, system.Asked);
    }

    [Fact]
    public void Failed_read_leaves_animations_on()
    {
        Assert.True(SystemMotion.Read(new FakeSystem(windows: true) { Throws = true }));
        Assert.True(SystemMotion.Read(new FakeSystem(linux: true) { Throws = true }));
    }

    // ---------- проявление

    /// <summary>
    /// Что элемент прошёл по пути: самая низкая прозрачность и самый большой сдвиг вниз. Мгновенному значению после
    /// действия верить нельзя: окно без экрана, разбирая свою очередь (например, внутри щелчка), прогоняет и кадры, и
    /// проявление большой страницы успевает пройти целиком.
    /// </summary>
    internal sealed class RevealProbe : IDisposable
    {
        private readonly Visual _target;

        public RevealProbe(Visual target)
        {
            _target = target;
            MinOpacity = target.Opacity;
            MaxShift = Shift(target);
            target.PropertyChanged += OnChanged;
        }

        public double MinOpacity { get; private set; }
        public double MaxShift { get; private set; }

        private void OnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.OpacityProperty) MinOpacity = Math.Min(MinOpacity, _target.Opacity);
            else if (e.Property == Visual.RenderTransformProperty) MaxShift = Math.Max(MaxShift, Shift(_target));
        }

        /// <summary>Проявился: начал почти с прозрачного и снизу, не ниже, чем на 6 px.</summary>
        public void AssertRevealed()
        {
            Assert.InRange(MinOpacity, 0, 0.25);
            Assert.InRange(MaxShift, Motion.RevealShift * 0.7, Motion.RevealShift + 0.01);
        }

        /// <summary>Показался сразу целиком, без движения.</summary>
        public void AssertStill()
        {
            Assert.Equal(1, MinOpacity);
            Assert.Equal(0, MaxShift);
        }

        public void Dispose() => _target.PropertyChanged -= OnChanged;
    }

    /// <summary>Окно-стенд: панель с Motion.RevealChildren, из двух «страниц» видна первая.</summary>
    private static (Window Window, Panel Panel, Border First, Border Second) Pages(bool still = false)
    {
        var first = new Border { Height = 50, Background = Brushes.Gray };
        var second = new Border { Height = 50, Background = Brushes.Gray, IsVisible = false };
        var panel = new Panel { Children = { first, second } };
        Motion.SetRevealChildren(panel, true);
        var window = new Window { Width = 300, Height = 200, Content = panel };
        if (still) Motion.SetEnabled(window, false);
        window.Show();
        Tick();
        return (window, panel, first, second);
    }

    private static void Switch(Visual from, Visual to)
    {
        to.IsVisible = true;
        from.IsVisible = false;
    }

    [AvaloniaFact]
    public async Task Shown_child_fades_in_from_below_and_ends_in_place()
    {
        var (window, _, first, second) = Pages();
        // показ окна — не смена: первая страница сразу на месте
        Assert.Equal(1, first.Opacity);
        Assert.Null(first.RenderTransform);

        using var seen = new RevealProbe(second);
        var watch = Stopwatch.StartNew();
        Switch(first, second);
        Tick();
        Assert.False(first.IsVisible); // прежняя прячется сразу
        if (watch.ElapsedMilliseconds < 100) Assert.InRange(second.Opacity, 0, 0.99); // и на первом кадре ещё идёт

        await Wait.Frames(400);
        seen.AssertRevealed();
        Assert.Equal(1, second.Opacity);
        Assert.Equal(0, Shift(second));
        // своих значений не оставляет: прозрачность и сдвиг снова дают разметка и стили
        Assert.False(second.IsSet(Visual.OpacityProperty));
        Assert.False(second.IsSet(Visual.RenderTransformProperty));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quick_switching_back_and_forth_ends_in_place()
    {
        var (window, _, first, second) = Pages();
        for (var i = 0; i < 3; i++)
        {
            Switch(first, second);
            Tick();
            Switch(second, first);
            Tick();
        }
        Switch(first, second);
        await Wait.Frames(400);
        foreach (var page in new[] { first, second })
        {
            Assert.Equal(1, page.Opacity);
            Assert.Equal(0, Shift(page));
        }
        Assert.True(second.IsVisible);
        Assert.False(first.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Child_of_hidden_panel_shows_without_reveal()
    {
        // вкладку сменили, пока раздел спрятан (другая страница окна): когда раздел покажут, вкладка уже на месте
        var (window, panel, first, second) = Pages();
        using var seen = new RevealProbe(second);
        panel.IsVisible = false;
        Switch(first, second);
        panel.IsVisible = true;
        await Wait.Frames(300);
        seen.AssertStill();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Window_not_shown_yet_shows_pages_without_reveal()
    {
        var first = new Border();
        var second = new Border { IsVisible = false };
        var panel = new Panel { Children = { first, second } };
        Motion.SetRevealChildren(panel, true);
        var window = new Window { Content = panel };
        using var seen = new RevealProbe(second);
        Switch(first, second); // как ShowPage при запуске: до показа окна
        window.Show();
        await Wait.Frames(300);
        seen.AssertStill();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Without_motion_page_shows_at_once()
    {
        var (window, _, first, second) = Pages(still: true);
        using var seen = new RevealProbe(second);
        Switch(first, second);
        await Wait.Frames(300);
        seen.AssertStill();
        Assert.True(second.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Panel_without_reveal_is_untouched()
    {
        var (window, panel, first, second) = Pages();
        Motion.SetRevealChildren(panel, false);
        using var seen = new RevealProbe(second);
        Switch(first, second);
        await Wait.Frames(300);
        seen.AssertStill();
        window.Close();
    }

    /// <summary>
    /// Вкладки «Сервера» (свой сервер и у хостера) — один общий механизм: каждая вкладка лежит в панели-обёртке внутри
    /// панели вкладок, а у той — Motion.RevealChildren. Консоль (карточка) — ребёнок той же панели.
    /// </summary>
    [AvaloniaFact]
    public void Server_tabs_reveal_through_their_container()
    {
        var app = Application.Current!;
        ResourceInclude[] theme =
        [
            new(Host) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") },
            new(Host) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") },
        ];
        foreach (var include in theme) app.Resources.MergedDictionaries.Add(include);
        try
        {
            var view = new ServerView();
            Type[] tabs =
            [
                typeof(ServerHostedView), typeof(ServerNotifyTab), typeof(ServerHostedBackupsView), typeof(ServerConfigView),
                typeof(ServerPlayersTab), typeof(ServerJournalTab), typeof(ServerStatsTab), typeof(ServerAnnouncementsTab),
                typeof(ServerRemoteView), typeof(ServerScheduleView),
            ];
            var found = view.GetLogicalDescendants().OfType<UserControl>().Where(c => tabs.Contains(c.GetType())).ToList();
            Assert.Equal(tabs.Length + 1, found.Count); // оповещения — и у своего сервера, и у хостера
            foreach (var tab in found)
            {
                var container = Assert.IsAssignableFrom<Panel>(tab.Parent?.Parent);
                Assert.True(Motion.GetRevealChildren(container), tab.GetType().Name);
            }
            // панелей вкладок две: у своего сервера и у хостера
            Assert.Equal(2, found.Select(tab => tab.Parent!.Parent).Distinct().Count());
        }
        finally
        {
            foreach (var include in theme) app.Resources.MergedDictionaries.Remove(include);
        }
    }

    /// <summary>
    /// В панели с Motion.RevealChildren — только то, что переключается: страницы или вкладки, у каждой своя видимость.
    /// Без IsVisible может быть одна — видная по умолчанию (её прячет код, как «Мои моды» в главном окне). Заголовок,
    /// полоса вкладок и прочие постоянные строки — снаружи: иначе плашка с условной видимостью, добавленная к ним,
    /// начнёт проявляться при каждом переключении.
    /// </summary>
    [Fact]
    public void Markup_reveal_panels_hold_only_switched_children()
    {
        var src = System.IO.Path.Combine(ThemeTests.RepoRoot(), "src", "eViSTool.Desktop");
        var reveal = XName.Get("Motion.RevealChildren", "using:eViSTool.Desktop.Controls");
        var panels = 0;
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.StartsWith("bin/") || relative.StartsWith("obj/")) continue;
            foreach (var panel in XDocument.Load(file).Descendants().Where(e => (string?)e.Attribute(reveal) == "True"))
            {
                panels++;
                // свойства-элементы (<Panel.Styles> и т. п.) — не дети; видимость бывает и свойством-элементом
                var children = panel.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
                var steady = children
                    .Where(e => e.Attribute("IsVisible") is null && e.Element(e.Name + ".IsVisible") is null)
                    .Select(e => e.Name.LocalName).ToList();
                if (children.Count < 2 || steady.Count > 1)
                    found.Add($"{relative}: {panel.Name.LocalName}, детей {children.Count}, без IsVisible: {string.Join(", ", steady)}");
            }
        }
        Assert.NotEqual(0, panels); // пространство имён ctl совпало: проверка что-то видит
        Assert.Empty(found);
    }

    // ---------- «без анимаций» в теме

    /// <summary>Окно с лесной темой и стилями «без анимаций» (Themes/ForestStill.axaml), как при выключенных анимациях.</summary>
    private static Window StillWindow(Control content)
    {
        var window = new Window { Width = 400, Height = 300 };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(Host) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(Host) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") });
        window.Styles.Add(new StyleInclude(Host) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") });
        window.Styles.Add(new StyleInclude(Host) { Source = new Uri("avares://eViSTool/Themes/ForestStill.axaml") });
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { content } };
        window.Show();
        Mouse.Prepare(window);
        return window;
    }

    private static Color Token(Window window, string key) =>
        window.TryFindResource(key, ThemeVariant.Dark, out var value) && value is ISolidColorBrush brush ? brush.Color
            : throw new KeyNotFoundException(key);

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [AvaloniaFact]
    public void Still_theme_window_turns_motion_off_for_everything_inside()
    {
        var inner = new Border();
        var window = StillWindow(inner);
        Assert.False(Motion.GetEnabled(window));
        Assert.False(Motion.GetEnabled(inner)); // наследуется
        window.Close();
    }

    [AvaloniaFact]
    public void Still_button_hover_and_press_change_at_once()
    {
        var button = new Button { Content = "Проверить" };
        var window = StillWindow(button);
        var presenter = Part<ContentPresenter>(button, "PART_ContentPresenter");
        Assert.Null(presenter.Transitions);
        Assert.Equal(Token(window, "Forest.Surface"), ColorOf(presenter.Background)); // первое значение — тоже сразу

        var center = Mouse.Center(window, button);
        window.MouseMove(center);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Token(window, "Forest.Raised"), ColorOf(presenter.Background));

        // нажатие: тема меняет переходы на время нажатия (^:pressed) — «без анимаций» сильнее
        window.MouseDown(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(presenter.Transitions);
        var pressed = MatrixOf(presenter.RenderTransform);
        Assert.Equal(0.985, pressed.M11, 3);
        Assert.Equal(1, pressed.M32, 3);

        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Matrix.Identity, MatrixOf(presenter.RenderTransform));
        window.Close();
    }

    [AvaloniaFact]
    public void Still_switch_and_tab_underline_change_at_once()
    {
        var toggle = new CheckBox();
        var first = new RadioButton { Content = "Общие", GroupName = "stillTabs", IsChecked = true };
        var second = new RadioButton { Content = "Мир", GroupName = "stillTabs" };
        var window = StillWindow(new StackPanel { Children = { toggle, first, second } });
        toggle.Theme = (ControlTheme)window.FindResource("Forest.Switch")!;
        first.Theme = second.Theme = (ControlTheme)window.FindResource("Forest.TabHeader")!;
        Mouse.Prepare(window);

        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(13, MatrixOf(Part<Ellipse>(toggle, "Knob").RenderTransform).M31, 3);
        Assert.Equal(Token(window, "Forest.Accent"), ColorOf(Part<Border>(toggle, "Track").Background));

        second.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        var line = Part<Border>(second, "Underline");
        Assert.Equal(1, line.Opacity);
        Assert.Equal(1, MatrixOf(line.RenderTransform).M11, 3);
        Assert.Equal(0, Part<Border>(first, "Underline").Opacity);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Still_indeterminate_progress_stands_in_the_middle()
    {
        var bar = new ProgressBar { Width = 200, IsIndeterminate = true };
        var window = StillWindow(bar);
        bar.Theme = (ControlTheme)window.FindResource("Forest.Progress")!;
        await Wait.Frames(100);
        var runner = Part<Border>(bar, "Runner");
        Assert.True(runner.IsVisible);
        Assert.Equal(HorizontalAlignment.Center, runner.HorizontalAlignment);
        for (var i = 0; i < 5; i++)
        {
            await Wait.Frames(60);
            Assert.Equal(70, runner.Bounds.X, 1); // (200 − 60) / 2
            Assert.Equal(0, MatrixOf(runner.RenderTransform).M31);
        }
        window.Close();
    }

    /// <summary>
    /// Анимация по кругу (IterationCount="Infinite": дыхание точки, блик заглушек, бегущая полоска) — только в стиле с
    /// условием [(ctl|Motion.Enabled)=True]: иначе при выключенных в системе анимациях она так и крутится. Пакеты,
    /// писавшие такие стили параллельно, условие забывали — этот тест не даст забыть снова.
    /// </summary>
    [Fact]
    public void Endless_animations_run_only_while_motion_is_enabled()
    {
        var src = System.IO.Path.Combine(ThemeTests.RepoRoot(), "src", "eViSTool.Desktop");
        var endless = new List<string>();
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.StartsWith("bin/") || relative.StartsWith("obj/")) continue;
            foreach (var animation in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "Animation"
                         && (string?)e.Attribute("IterationCount") is { } count && count.Equals("Infinite", StringComparison.OrdinalIgnoreCase)))
            {
                var selector = (string?)animation.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Style")?.Attribute("Selector") ?? "";
                endless.Add($"{relative}: {selector}");
                if (!selector.Contains("(ctl|Motion.Enabled)=True]", StringComparison.Ordinal)) missing.Add($"{relative}: {selector}");
            }
        }
        Assert.NotEmpty(endless); // сами анимации нашлись — иначе тест ничего не проверяет
        Assert.Empty(missing);
    }
}
