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
using eViSTool.Desktop.Controls;
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

    internal static string RepoRoot()
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
    /// Правило раздела 1: в стилях и разметке цвета — только токенами. Целиком исключены файлы, где числа — их место:
    /// сами токены, производные цвета палитры Fluent и нейтрально-чёрный просмотр скриншотов. В остальных разрешены
    /// только перечисленные значения — системный красный кнопки «закрыть», — чтобы новый цвет числом там не прошёл молча.
    /// </summary>
    [Fact]
    public void Markup_HasNoNumericColors()
    {
        var src = System.IO.Path.Combine(RepoRoot(), "src", "eViSTool.Desktop");
        string[] allowedFiles = ["App.axaml", "Themes/ForestResources.axaml", "Dialogs/ScreenshotWindow.axaml"];
        (string File, string Color)[] allowedColors = [("Themes/ForestWindowChrome.axaml", "#C42B1C"), ("Themes/ForestWindowChrome.axaml", "#B0291C")];
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(src, file).Replace('\\', '/');
            if (allowedFiles.Contains(relative) || relative.StartsWith("bin/") || relative.StartsWith("obj/")) continue;
            var text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline); // в комментариях — можно
            found.AddRange(Regex.Matches(text, @"""#[0-9A-Fa-f]{3,8}""|#[0-9A-Fa-f]{6,8}\b")
                .Select(m => m.Value.Trim('"'))
                .Where(color => !allowedColors.Contains((relative, color.ToUpperInvariant())))
                .Select(color => $"{relative}: {color}"));
        }
        Assert.Empty(found);
    }

    /// <summary>
    /// Обрезка карточки (ClipToBounds) срезает и её собственную тень: карточка с таблицей или консолью выходила плоской
    /// рядом с приподнятыми. Обрезают внутренней рамкой Border.cardClip.
    /// </summary>
    [Fact]
    public void Markup_CardsAreNotClipped()
    {
        var src = System.IO.Path.Combine(RepoRoot(), "src", "eViSTool.Desktop");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.axaml", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.StartsWith("bin/") || relative.StartsWith("obj/")) continue;
            var text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline);
            found.AddRange(Regex.Matches(text, @"<Border\b[^>]*>")
                .Where(m => Regex.IsMatch(m.Value, @"Classes=""[^""]*\bcard\b") && m.Value.Contains("ClipToBounds=\"True\""))
                .Select(m => $"{relative}: {m.Value}"));
        }
        Assert.Empty(found);
    }

    /// <summary>Карточка с содержимым во всю площадь (как таблица): тень под ней есть, углы содержимого обрезаны по скруглению.</summary>
    [AvaloniaFact]
    public void Card_KeepsShadow_AndClipsContentByInnerBorder()
    {
        var card = new Border { Width = 200, Height = 100, Classes = { "card" } };
        var window = Show(card);
        var accent = new SolidColorBrush(Token(window, "Forest.Accent"));
        card.Child = new Border { Classes = { "cardClip" }, Child = new Border { Background = accent } };
        window.Background = new SolidColorBrush(Token(window, "Forest.Base"));
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("кадр не отрисовался");
        var at = card.TranslatePoint(default, window)!.Value;
        var (left, top, bottom) = ((int)at.X, (int)at.Y, (int)(at.Y + card.Bounds.Height));

        // под карточкой — тень: темнее фона страницы
        var basePixel = Pixel(frame, left + 100, bottom + 60);
        var under = Pixel(frame, left + 100, bottom);
        Assert.True(Lightness(under) < Lightness(basePixel) - 10, $"под карточкой {under}, фон {basePixel}");

        // посередине содержимое, а в углу (сразу за рамкой) — уже нет: обрезано по скруглению 9
        Assert.Equal(accent.Color, Pixel(frame, left + 100, top + 50));
        var corner = Pixel(frame, left + 1, top + 1);
        Assert.True(Math.Abs(corner.R - accent.Color.R) > 60, $"угол карточки {corner}");
        window.Close();
    }

    private static int Lightness(Color c) => c.R + c.G + c.B;

    private static Color Pixel(Avalonia.Media.Imaging.WriteableBitmap frame, int x, int y)
    {
        var bytes = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
        finally { handle.Free(); }
        return frame.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
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
        var hover = Assert.Single(presenter.Transitions!.OfType<PremulBrushTransition>(), t => t.Property == ContentPresenter.BackgroundProperty);
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

    /// <summary>
    /// Ссылка наружу (класс external) — стрелка Icon.External после текста, цвета текста и с ним же тускнеет; у обычной
    /// ссылки стрелки нет. Знака «↗» в текстах больше нет: его во вшитых шрифтах нет, и он приходил из системы эмодзи.
    /// </summary>
    [AvaloniaFact]
    public async Task LinkButton_External_HasArrowIconAfterText()
    {
        var external = new Button { Content = "Модбаза", Classes = { "external" } };
        var plain = new Button { Content = "Удалить" };
        var window = Show(new StackPanel { Children = { external, plain } });
        external.Theme = plain.Theme = (ControlTheme)window.FindResource("Forest.LinkButton")!;
        await Run(300);

        var arrow = Part<Avalonia.Controls.Shapes.Path>(external, "Ext");
        Assert.True(arrow.IsVisible);
        Assert.Same(window.FindResource("Icon.External"), arrow.Data);
        Assert.Equal((8, 8), (arrow.Bounds.Width, arrow.Bounds.Height)); // не 18, как у Path.icon
        var text = Part<TextBlock>(external, "T");
        Assert.True(arrow.Bounds.Left >= text.Bounds.Right, "стрелка после текста");
        Assert.Equal(Token(window, "Forest.Accent"), ColorOf(arrow.Stroke));
        Assert.False(Part<Avalonia.Controls.Shapes.Path>(plain, "Ext").IsVisible);

        external.IsEnabled = false;
        await Run(300);
        Assert.Equal(Token(window, "Forest.Faint"), ColorOf(arrow.Stroke));
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

    // ---------- вспышки: переход цвета не светлее обоих концов

    /// <summary>Цвет, который видит глаз: кисть поверх подложки. Смешение — в sRGB, как у Skia; кисти нет — подложка.</summary>
    private static Color Over(IBrush? brush, Color backdrop)
    {
        if (brush is not ISolidColorBrush solid) return backdrop;
        var a = solid.Color.A / 255.0 * solid.Opacity;
        byte Mix(byte top, byte under) => (byte)Math.Round(top * a + under * (1 - a));
        return Color.FromRgb(Mix(solid.Color.R, backdrop.R), Mix(solid.Color.G, backdrop.G), Mix(solid.Color.B, backdrop.B));
    }

    /// <summary>
    /// Снять кисть <paramref name="read"/> поверх <paramref name="backdrop"/> до перемены, на каждом кадре перехода и в
    /// конце. Кадр — тик отрисовки примерно раз в 5 мс: вспышка при кривой «торможение» приходится на первые десятки мс.
    /// </summary>
    private static async Task<List<Color>> Frames(Func<IBrush?> read, Color backdrop, Action change, int ms = 300)
    {
        var seen = new List<Color> { Over(read(), backdrop) };
        change();
        var watch = Stopwatch.StartNew();
        do
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            seen.Add(Over(read(), backdrop));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        } while (watch.ElapsedMilliseconds < ms);
        return seen;
    }

    /// <summary>
    /// Ни один кадр не светлее обоих концов перехода (запас 3 — округление каналов). У Avalonia Transparent — белый с
    /// нулевой альфой, и без предумножения переход от него к тёмному токену проходил через светлые полупрозрачные цвета.
    /// </summary>
    private static void AssertNoFlash(List<Color> frames, string what)
    {
        var limit = Math.Max(Lightness(frames[0]), Lightness(frames[^1])) + 3;
        var worst = frames.MaxBy(Lightness);
        Assert.True(Lightness(worst) <= limit,
            $"{what}: кадр {worst} светлее концов {frames[0]} → {frames[^1]} ({string.Join(", ", frames.Select(c => c.ToString()[3..]))})");
    }

    /// <summary>Пункт бокового меню при наведении: прозрачный фон → вуаль наведения — без вспышки белым.</summary>
    [AvaloniaFact]
    public async Task NavItem_Hover_DoesNotFlashWhite()
    {
        var nav = new ListBox { Items = { "Мои моды", "Каталог модов" }, BorderThickness = new Thickness(0), Width = 220 };
        var window = Show(nav);
        var side = Token(window, "Forest.Side");
        window.Background = new SolidColorBrush(side);
        nav.Background = Brushes.Transparent;
        nav.ItemContainerTheme = (ControlTheme)window.FindResource("Forest.NavItem")!;
        await Run(300);
        var item = nav.GetVisualDescendants().OfType<ListBoxItem>().Last();
        var bd = Part<Border>(item, "Bd");

        var hover = await Frames(() => bd.Background, side, () => window.MouseMove(Mouse.Center(window, item)));
        Assert.Equal(Over(window.FindResource("Forest.NavHoverVeil") as IBrush, side), hover[^1]);
        AssertNoFlash(hover, "наведение на пункт меню");

        // и обратно: увёл мышь — вуаль тает без вспышки
        var leave = await Frames(() => bd.Background, side, () => window.MouseMove(new Point(390, 290)));
        Assert.Equal(side, leave[^1]);
        AssertNoFlash(leave, "уход с пункта меню");
        window.Close();
    }

    /// <summary>
    /// Кнопка, которая включилась (выключенная — без фона и рамки): фон и рамка проявляются без вспышки белым — «Пропустить»
    /// в карточке мода при переходе на мод с обновлением. Подложка — карточка (Surface).
    /// </summary>
    [AvaloniaFact]
    public async Task Button_TurningEnabled_DoesNotFlashWhite()
    {
        var button = new Button { Content = "Пропустить", IsEnabled = false };
        var window = Show(new Border { Classes = { "card" }, Padding = new Thickness(14), Child = button });
        var surface = Token(window, "Forest.Surface");
        await Run(300);
        var presenter = Part<ContentPresenter>(button, "PART_ContentPresenter");

        var fill = await Frames(() => presenter.Background, surface, () => button.IsEnabled = true);
        AssertNoFlash(fill, "фон включившейся кнопки");
        button.IsEnabled = false;
        await Run(300);
        var border = await Frames(() => presenter.BorderBrush, surface, () => button.IsEnabled = true);
        Assert.Equal(Token(window, "Forest.Line"), border[^1]);
        AssertNoFlash(border, "рамка включившейся кнопки");
        window.Close();
    }

    /// <summary>Плоская кнопка («Дополнительно ▾») и чип-фильтр при наведении: от прозрачного фона — без вспышки белым.</summary>
    [AvaloniaFact]
    public async Task FlatButtonAndChip_Hover_DoNotFlashWhite()
    {
        var flat = new Button { Content = "Дополнительно", Classes = { "flat" } };
        var chip = new RadioButton { Content = "Все" };
        var window = Show(new StackPanel { Spacing = 20, Children = { flat, chip } });
        chip.Theme = (ControlTheme)window.FindResource("Forest.Chip")!;
        var page = Token(window, "Forest.Base");
        window.Background = new SolidColorBrush(page);
        await Run(300);

        var presenter = Part<ContentPresenter>(flat, "PART_ContentPresenter");
        AssertNoFlash(await Frames(() => presenter.Background, page, () => window.MouseMove(Mouse.Center(window, flat))),
            "наведение на плоскую кнопку");
        var bd = Part<Border>(chip, "Bd");
        AssertNoFlash(await Frames(() => bd.Background, page, () => window.MouseMove(Mouse.Center(window, chip))), "наведение на чип");
        window.Close();
    }

    /// <summary>
    /// Переходы цвета в разметке и коде окна — только ctl:PremulBrushTransition. BrushTransition из Avalonia смешивает без
    /// учёта прозрачности: конец Transparent (белый с нулевой альфой) или любая кисть с альфой 0 другого цвета даёт вспышку
    /// белым. Прозрачные фоны без переходов проверка не трогает: её предмет — сам переход.
    /// </summary>
    [Fact]
    public void Markup_ColorTransitionsArePremultiplied()
    {
        var src = System.IO.Path.Combine(RepoRoot(), "src", "eViSTool.Desktop");
        var found = new List<string>();
        var premultiplied = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.StartsWith("bin/") || relative.StartsWith("obj/")) continue;
            string text;
            if (file.EndsWith(".axaml")) text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline);
            else if (file.EndsWith(".cs")) text = Regex.Replace(File.ReadAllText(file), @"//.*?$|/\*.*?\*/", "", RegexOptions.Multiline | RegexOptions.Singleline);
            else continue;
            premultiplied += Regex.Matches(text, @"\bPremulBrushTransition\b").Count;
            found.AddRange(Regex.Matches(text, @"(?<![\w.:])BrushTransition\b|Avalonia\.Animation\.BrushTransition\b")
                .Select(m => $"{relative}: …{text.Substring(Math.Max(0, m.Index - 20), Math.Min(60, text.Length - Math.Max(0, m.Index - 20)))}…"));
        }
        Assert.Empty(found);
        Assert.True(premultiplied >= 20, $"переходов цвета в теме найдено: {premultiplied}"); // проверка видит тему, а не пустоту
    }

    /// <summary>
    /// Смешение кистей перехода: от прозрачного конца по пути меняется только прозрачность, цвет — другого конца (у
    /// Transparent своего цвета нет); два непрозрачных цвета — как у Avalonia, в линейном свете; концы — сами кисти.
    /// </summary>
    [Fact]
    public void BrushMix_FromTransparent_KeepsTheOtherColor()
    {
        var veil = Color.Parse("#99283128");
        var lastAlpha = -1;
        for (var t = 0.05; t < 1; t += 0.05)
        {
            var mixed = BrushMix.Mix(t, Colors.Transparent, veil);
            Assert.Equal((veil.R, veil.G, veil.B), (mixed.R, mixed.G, mixed.B));
            Assert.True(mixed.A > lastAlpha, $"прозрачность не растёт на {t}: {mixed}");
            lastAlpha = mixed.A;
            Assert.Equal((veil.R, veil.G, veil.B), BrushMix.Mix(t, veil, Colors.Transparent) is var back ? (back.R, back.G, back.B) : default);
        }

        // непрозрачные: середина — как у Avalonia (линейный свет: светлее простого среднего каналов)
        var surface = Color.Parse("#252E26");
        var raised = Color.Parse("#323C33");
        var avalonia = typeof(Animation).Assembly.GetType("Avalonia.Animation.Animators.ColorAnimator")
            ?.GetMethod("InterpolateCore", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.SkipWhen(avalonia is null, "у этой версии Avalonia смешение цветов устроено иначе — сравнить не с чем");
        foreach (var t in new[] { 0.1, 0.5, 0.9 })
            Assert.Equal(avalonia.Invoke(null, [t, surface, raised]), BrushMix.Mix(t, surface, raised));

        // кисти: концы — те же объекты, null — сразу новое значение, без перехода
        var from = new SolidColorBrush(surface);
        var to = new SolidColorBrush(raised);
        Assert.Same(from, BrushMix.Mix(0, from, to));
        Assert.Same(to, BrushMix.Mix(1, from, to));
        Assert.Same(to, BrushMix.Mix(0.2, null, to));
        Assert.Null(BrushMix.Mix(0.2, from, null));
    }

    /// <summary>
    /// Главная кнопка: градиент ↔ сплошной цвет смешиваются по остановкам; выключенная (без фона) → включённая проявляется
    /// цветом самого градиента, без белёсого пути.
    /// </summary>
    [Fact]
    public void BrushMix_GradientAndSolid_MixByStops()
    {
        var top = Color.Parse("#E9D6A3");
        var bottom = Color.Parse("#D2BC86");
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
        var hover = Color.Parse("#E0CC98");

        var half = Assert.IsAssignableFrom<ILinearGradientBrush>(BrushMix.Mix(0.5, gradient, new SolidColorBrush(hover)));
        Assert.Equal([0.0, 1.0], half.GradientStops.Select(s => s.Offset));
        Assert.Equal([BrushMix.Mix(0.5, top, hover), BrushMix.Mix(0.5, bottom, hover)], half.GradientStops.Select(s => s.Color));
        Assert.Equal(gradient.EndPoint, half.EndPoint);

        var appearing = Assert.IsAssignableFrom<ILinearGradientBrush>(BrushMix.Mix(0.3, Brushes.Transparent, gradient));
        Assert.Equal([(top.R, top.G, top.B), (bottom.R, bottom.G, bottom.B)], appearing.GradientStops.Select(s => (s.Color.R, s.Color.G, s.Color.B)));
        Assert.All(appearing.GradientStops, s => Assert.InRange(s.Color.A, (byte)1, (byte)254));
    }

    [Fact]
    public void CaptionColor_IsColorRef()
    {
        Assert.Equal(0x00131911, WindowCaption.ColorRef(Color.Parse("#111913")));
        Assert.Equal(0x00DEE9E7, WindowCaption.ColorRef(Color.Parse("#E7E9DE")));
    }
}
