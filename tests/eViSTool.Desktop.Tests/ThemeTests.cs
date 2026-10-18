using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Desktop.Platform;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Лесная тема по docs/design.md: токены совпадают с таблицей документа, в разметке нет цветов числом, наведение и
/// нажатие идут переходами (а не скачком) с кривой «торможение», выключенная кнопка — без фона, кольцо фокуса — только
/// от клавиатуры, ручка переключателя едет сдвигом, подчёркивание вкладки вырастает, неопределённая полоска бежит.
/// Время переходов — настоящее: окно без экрана двигает часы анимаций только по тику отрисовки, тики даём сами.
/// </summary>
public sealed class ThemeTests
{
    private static readonly Uri Host = new("avares://eViSTool.Desktop.Tests");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "eViSTool.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("eViSTool.slnx");
    }

    /// <summary>Окно с лесной темой. Тема — до содержимого: тема элемента по умолчанию выбирается, когда он входит в окно.</summary>
    private static Window Show(Control content)
    {
        var window = new Window { Width = 400, Height = 300 };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(Host) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(Host) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") });
        window.Styles.Add(new StyleInclude(Host) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") });
        window.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = { content } };
        window.Show();
        Mouse.Prepare(window);
        return window;
    }

    /// <summary>Дать часам анимаций пройти <paramref name="ms"/> миллисекунд: тик отрисовки каждые ~10 мс.</summary>
    private static async Task Run(int ms)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        } while (watch.ElapsedMilliseconds < ms);
    }

    private static Color Token(Window window, string key) =>
        window.TryFindResource(key, ThemeVariant.Dark, out var value) && value is ISolidColorBrush brush ? brush.Color
            : throw new KeyNotFoundException(key);

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static T Part<T>(Control owner, string name) where T : Control =>
        owner.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Matrix MatrixOf(ITransform? transform) => transform?.Value ?? Matrix.Identity;

    [AvaloniaFact]
    public void Tokens_MatchDesignDoc()
    {
        var doc = File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "docs", "design.md"));
        var rows = Regex.Matches(doc, @"^\| (Forest\.\w+) \| `(#[0-9A-Fa-f]{6,8})` \|", RegexOptions.Multiline);
        Assert.True(rows.Count >= 29, $"в таблицах раздела 1 найдено строк: {rows.Count}");

        var resources = new ResourceInclude(Host) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") };
        foreach (Match row in rows)
        {
            var key = row.Groups[1].Value;
            Assert.True(resources.TryGetResource(key, ThemeVariant.Dark, out var value), $"нет токена {key}");
            Assert.Equal((key, Color.Parse(row.Groups[2].Value)), (key, Assert.IsAssignableFrom<ISolidColorBrush>(value).Color));
        }
    }

    /// <summary>
    /// Правило раздела 1: в стилях и разметке цвета — только токенами. Исключения — сами токены, производные цвета
    /// палитры Fluent, системный красный кнопки «закрыть» и нейтрально-чёрный просмотр скриншотов.
    /// </summary>
    [Fact]
    public void Markup_HasNoNumericColors()
    {
        var src = System.IO.Path.Combine(RepoRoot(), "src", "eViSTool.Desktop");
        string[] allowed = ["App.axaml", "Themes/ForestResources.axaml", "Themes/ForestWindowChrome.axaml", "Dialogs/ScreenshotWindow.axaml"];
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(src, file).Replace('\\', '/');
            if (allowed.Contains(relative) || relative.StartsWith("bin/") || relative.StartsWith("obj/")) continue;
            var text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline); // в комментариях — можно
            found.AddRange(Regex.Matches(text, @"""#[0-9A-Fa-f]{3,8}""|#[0-9A-Fa-f]{6,8}\b").Select(m => $"{relative}: {m.Value}"));
        }
        Assert.Empty(found);
    }

    [AvaloniaFact]
    public async Task Button_HoverAndPress_AreTransitions()
    {
        var button = new Button { Content = "Проверить" };
        var window = Show(button);
        var presenter = Part<ContentPresenter>(button, "PART_ContentPresenter");
        var surface = Token(window, "Forest.Surface");
        var raised = Token(window, "Forest.Raised");
        await Run(300); // первое значение фона тоже идёт переходом (от пустого) — дождаться

        // кривая — «торможение» 0.2, 0.7, 0.2, 1, наведение — 180 мс
        var hover = Assert.Single(presenter.Transitions!.OfType<BrushTransition>(), t => t.Property == ContentPresenter.BackgroundProperty);
        Assert.Equal(TimeSpan.FromMilliseconds(180), hover.Duration);
        var easing = Assert.IsType<SplineEasing>(hover.Easing);
        Assert.Equal((0.2, 0.7, 0.2, 1.0), (easing.X1, easing.Y1, easing.X2, easing.Y2));
        Assert.Equal(surface, ColorOf(presenter.Background));
        Assert.NotEqual(0, presenter.BoxShadow.Count); // кромка

        // наведение: фон светлеет плавно — сразу после события ещё не приподнятый, через 300 мс — он
        var center = Mouse.Center(window, button);
        var watch = Stopwatch.StartNew();
        window.MouseMove(center);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        if (watch.ElapsedMilliseconds < 60) Assert.NotEqual(raised, ColorOf(presenter.Background));
        await Run(300);
        Assert.Equal(raised, ColorOf(presenter.Background));
        Assert.Equal(Token(window, "Forest.LineStrong"), ColorOf(presenter.BorderBrush));

        // нажатие: проседает за 60 мс (масштаб 0.985 и сдвиг на 1 px вниз), тень и кромка пропадают. Сразу после нажатия
        // сдвиг ещё не полный: если бы длительность сменилась позже сдвига, переход оборвался бы и кнопка прыгнула
        watch.Restart();
        window.MouseDown(center, MouseButton.Left);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        if (watch.ElapsedMilliseconds < 30) Assert.NotEqual(1, MatrixOf(presenter.RenderTransform).M32);
        Assert.Equal(TimeSpan.FromMilliseconds(60), presenter.Transitions!.OfType<TransformOperationsTransition>().Single().Duration);
        await Run(200);
        var pressed = MatrixOf(presenter.RenderTransform);
        Assert.Equal(0.985, pressed.M11, 3);
        Assert.Equal(1, pressed.M32, 3);
        Assert.Equal(0, presenter.BoxShadow.Count);

        // отпускание — 100 мс, обратно без сдвига, тоже переходом
        watch.Restart();
        window.MouseUp(center, MouseButton.Left);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        if (watch.ElapsedMilliseconds < 50) Assert.NotEqual(0, MatrixOf(presenter.RenderTransform).M32);
        Assert.Equal(TimeSpan.FromMilliseconds(100), presenter.Transitions!.OfType<TransformOperationsTransition>().Single().Duration);
        await Run(250);
        Assert.Equal(Matrix.Identity, MatrixOf(presenter.RenderTransform));
        Assert.NotEqual(0, presenter.BoxShadow.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Button_Disabled_HasNoBackgroundAndFaintText()
    {
        var plain = new Button { Content = "Обычная", IsEnabled = false };
        var primary = new Button { Content = "Главная", IsEnabled = false, Classes = { "primary" } };
        var window = Show(new StackPanel { Children = { plain, primary } });
        await Run(300);
        foreach (var button in new[] { plain, primary })
        {
            var presenter = Part<ContentPresenter>(button, "PART_ContentPresenter");
            Assert.Equal(Colors.Transparent, ColorOf(presenter.Background));
            Assert.Equal(Colors.Transparent, ColorOf(presenter.BorderBrush));
            Assert.Equal(Token(window, "Forest.Faint"), ColorOf(button.Foreground));
            Assert.Equal(1, presenter.Opacity); // не полупрозрачная копия обычной
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Button_Primary_IsConvex()
    {
        var button = new Button { Content = "Применить", Classes = { "primary" } };
        var window = Show(button);
        var presenter = Part<ContentPresenter>(button, "PART_ContentPresenter");
        var gradient = Assert.IsAssignableFrom<ILinearGradientBrush>(presenter.Background);
        Assert.Equal([Token(window, "Forest.AccentTop"), Token(window, "Forest.Accent")], gradient.GradientStops.Select(s => s.Color));
        Assert.Equal(2, presenter.BoxShadow.Count); // яркая кромка и тень в 1 px
        Assert.True(presenter.BoxShadow[0].IsInset);
        window.Close();
    }

    [AvaloniaFact]
    public void FocusRing_OnlyFromKeyboard()
    {
        var button = new Button { Content = "Кнопка" };
        var other = new Button { Content = "Другая" };
        var window = Show(new StackPanel { Children = { button, other } });
        var ring = Part<Border>(button, "FocusRing");
        Assert.False(ring.IsVisible);

        button.Focus(NavigationMethod.Pointer);
        Dispatcher.UIThread.RunJobs();
        Assert.False(ring.IsVisible);

        other.Focus(NavigationMethod.Pointer);
        button.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ring.IsVisible);
        Assert.Equal(new Thickness(-4), ring.Margin); // зазор 2 px и кольцо 2 px снаружи кнопки
        Assert.Equal(new Thickness(2), ring.BorderThickness);
        Assert.Null(ring.Transitions); // появляется сразу
        Assert.False(button.ClipToBounds); // кольцо за краем кнопки не обрезано
        Assert.Null(button.FocusAdorner); // и рамка фокуса Fluent поверх него не рисуется
        window.Close();
    }

    [AvaloniaFact]
    public async Task Switch_KnobSlidesByTransform()
    {
        var toggle = new CheckBox();
        var window = Show(toggle);
        toggle.Theme = (ControlTheme)window.FindResource("Forest.Switch")!;
        Mouse.Prepare(window);
        var knob = Part<Ellipse>(toggle, "Knob");
        Assert.Equal(HorizontalAlignment.Left, knob.HorizontalAlignment);
        Assert.Equal(0, MatrixOf(knob.RenderTransform).M31);

        var watch = Stopwatch.StartNew();
        toggle.IsChecked = true;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        if (watch.ElapsedMilliseconds < 60) Assert.True(MatrixOf(knob.RenderTransform).M31 < 13);
        await Run(350);
        Assert.Equal(13, MatrixOf(knob.RenderTransform).M31, 3);
        Assert.Equal(HorizontalAlignment.Left, knob.HorizontalAlignment); // выравнивание не переключается
        Assert.Equal(Token(window, "Forest.Accent"), ColorOf(Part<Border>(toggle, "Track").Background));
        window.Close();
    }

    [AvaloniaFact]
    public async Task TabHeader_UnderlineGrowsWhenChecked()
    {
        var first = new RadioButton { Content = "Общие", GroupName = "tabs", IsChecked = true };
        var second = new RadioButton { Content = "Мир", GroupName = "tabs" };
        var window = Show(new StackPanel { Orientation = Orientation.Horizontal, Children = { first, second } });
        var theme = (ControlTheme)window.FindResource("Forest.TabHeader")!;
        first.Theme = second.Theme = theme;
        await Run(300);
        var line = Part<Border>(second, "Underline");
        Assert.Equal(0, line.Opacity);
        Assert.Equal(0, MatrixOf(line.RenderTransform).M11);

        second.IsChecked = true;
        await Run(350);
        Assert.Equal(1, line.Opacity, 3);
        Assert.Equal(1, MatrixOf(line.RenderTransform).M11, 3);
        Assert.Equal(new RelativePoint(0.5, 0.5, RelativeUnit.Relative), line.RenderTransformOrigin); // от середины
        Assert.Equal(0, Part<Border>(first, "Underline").Opacity, 3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Progress_Indeterminate_RunsAThirdWideSegment()
    {
        var bar = new ProgressBar { Width = 200, IsIndeterminate = true };
        var window = Show(bar);
        bar.Theme = (ControlTheme)window.FindResource("Forest.Progress")!;
        await Run(100);
        var runner = Part<Border>(bar, "Runner");
        Assert.True(runner.IsVisible);
        Assert.False(Part<Border>(bar, "PART_Indicator").IsVisible);
        Assert.Equal(60, runner.Bounds.Width, 1);

        // отрезок едет: за полсекунды положение меняется и всегда в пределах «весь левее» … «весь правее»
        var seen = new HashSet<double>();
        for (var i = 0; i < 10; i++)
        {
            await Run(50);
            var x = MatrixOf(runner.RenderTransform).M31;
            Assert.InRange(x, -60.5, 200.5);
            seen.Add(Math.Round(x));
        }
        Assert.True(seen.Count > 3, $"положения отрезка: {string.Join(", ", seen)}");
        window.Close();
    }

    [Fact]
    public void CaptionColor_IsColorRef()
    {
        Assert.Equal(0x00131911, WindowCaption.ColorRef(Color.Parse("#111913")));
        Assert.Equal(0x00DEE9E7, WindowCaption.ColorRef(Color.Parse("#E7E9DE")));
    }
}
