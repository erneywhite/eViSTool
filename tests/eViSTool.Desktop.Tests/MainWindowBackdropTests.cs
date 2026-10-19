using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Views;
using GlyphPath = Avalonia.Controls.Shapes.Path;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Боковая панель главного окна и фон из игры (docs/design.md, раздел 5) — настоящая разметка окна и лесная тема:
/// без картинки — цвет Side и «горизонтали», с картинкой (класс backdrop у Root) — вуаль над ней, вуалевые выбранный
/// и наведённый пункт меню (их задаёт тема, Forest.NavItem) и карточка профиля. Окно не показывается и живёт без
/// модели: только разметка и стили.
/// </summary>
public sealed class MainWindowBackdropTests : IDisposable
{
    private static readonly Uri Base = new("avares://eViSTool.Desktop.Tests");

    private readonly ResourceInclude[] _resources =
    [
        new(Base) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") },
        new(Base) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") },
        new(Base) { Source = new Uri("avares://eViSTool/Themes/ForestWindowChrome.axaml") },
    ];

    private readonly StyleInclude _styles = new(Base) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") };

    public MainWindowBackdropTests()
    {
        var app = Application.Current!;
        foreach (var include in _resources) app.Resources.MergedDictionaries.Add(include);
        app.Styles.Add(_styles);
    }

    public void Dispose()
    {
        var app = Application.Current!;
        app.Styles.Remove(_styles);
        foreach (var include in _resources) app.Resources.MergedDictionaries.Remove(include);
    }

    private static MainWindow Window()
    {
        var window = new MainWindow();
        // без показа: Opened и Closing окна ждут главную модель, а стилям и шаблонам хватает прохода раскладки
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));
        return window;
    }

    private static Border Bd(ListBoxItem item) => item.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Bd");

    [AvaloniaFact]
    public void Without_a_picture_side_colour_and_contours()
    {
        var window = Window();

        Assert.Same(window.FindResource("Forest.Side"), window.FindControl<Border>("SideUnderlay")!.Background);
        var contours = window.FindControl<GlyphPath>("Contours")!;
        Assert.True(contours.IsVisible);
        Assert.Same(window.FindResource("Forest.Surface"), window.FindControl<Border>("ProfileCard")!.Background);
        Assert.Null(window.FindControl<Image>("Backdrop")!.Source);

        // линии плывут через свой сдвиг (ContourDriftTests), в рамке, которая держит перерисовку в границах панели;
        // окно не показано — таймер стоит
        Assert.Same(ContourLines.Geometry, contours.Data);
        Assert.IsType<TranslateTransform>(contours.RenderTransform);
        Assert.True(window.FindControl<Border>("SideUnderlay")!.ClipToBounds);
        Assert.False(window.Drift.IsMoving);
    }

    [AvaloniaFact]
    public async Task With_a_picture_veil_and_veil_brushes_in_the_menu()
    {
        var window = Window();
        var nav = window.FindControl<ListBox>("Nav")!;
        nav.SelectedIndex = 0;
        window.FindControl<Grid>("Root")!.Classes.Add("backdrop");

        Assert.Same(window.FindResource("Forest.SideVeil"), window.FindControl<Border>("SideUnderlay")!.Background);
        Assert.False(window.FindControl<GlyphPath>("Contours")!.IsVisible);
        Assert.Same(window.FindResource("Forest.SideCardVeil"), window.FindControl<Border>("ProfileCard")!.Background);

        // невидимое окно разворачивает в меню только первый пункт (остальные — без темы пункта меню), на нём и проверяем.
        // Подложка пункта меняется переходом (180 мс) — проверяем, к чему он пришёл
        var item = Assert.IsType<ListBoxItem>(nav.ContainerFromIndex(0));
        await Wait.Until(() => ReferenceEquals(Bd(item).Background, window.FindResource("Forest.NavSelectedVeil")));

        // наведение — псевдоклассом напрямую: мышь окну без показа не нужна
        nav.SelectedIndex = -1;
        ((IPseudoClasses)item.Classes).Set(":pointerover", true);
        await Wait.Until(() => ReferenceEquals(Bd(item).Background, window.FindResource("Forest.NavHoverVeil")));
    }

    /// <summary>
    /// Карточка с логотипом в «О программе» — «окно» в подложку панели (Controls/BackdropWindow): берёт картинку и линии
    /// главного окна. С картинкой — тот же Bitmap, что у Image#Backdrop, со смещением, равным положению карточки в окне;
    /// без картинки (галочка выключена, заставки нет) — «горизонтали» панели с её же сдвигом плавания. Что пиксели в
    /// «окне» совпадают с картинкой под ним, проверяет BackdropWindowTests на показанном окне.
    /// </summary>
    [AvaloniaFact]
    public void About_card_is_a_window_into_the_side_panel_underlay()
    {
        var window = new MainWindow();
        var pages = window.FindControl<Panel>("Pages")!;
        for (var i = 0; i < pages.Children.Count; i++) pages.Children[i].IsVisible = pages.Children[i] is AboutView;
        Layout(window);
        var hero = window.GetVisualDescendants().OfType<BackdropWindow>().Single();
        var backdrop = window.FindControl<Image>("Backdrop")!;
        var contours = window.FindControl<GlyphPath>("Contours")!;
        Assert.Same(backdrop, BackdropWindow.GetPicture(hero));
        Assert.Same(contours, BackdropWindow.GetLines(hero));
        var heroAt = BackdropWindow.Origin(hero, window)!.Value;
        Assert.True(heroAt.X > 210 && heroAt.Y > 60, $"карточка в {heroAt}"); // на странице, справа от панели и ниже полосы
        // строки-ссылки (четыре ссылки и две благодарности) — со стрелкой-иконкой, а не знаком «↗»
        var about = pages.Children.OfType<AboutView>().Single();
        Assert.Equal(6, about.GetVisualDescendants().OfType<GlyphPath>().Count(p => p.IsVisible && ReferenceEquals(p.Data, window.FindResource("Icon.External"))));

        // без картинки — линии панели: то же начало узора в окне и тот же сдвиг плавания
        Assert.Null(hero.ShownPicture);
        Assert.True(hero.LinesShown);
        Assert.Same(contours.RenderTransform, hero.LinesCopy.RenderTransform);
        Assert.Same(contours.Stroke, hero.LinesCopy.Stroke);
        Vector origin = BackdropWindow.Origin(contours, window)!.Value - heroAt;
        Assert.Equal(origin, hero.LinesOrigin);

        // с картинкой — тот же Bitmap; где он лежит в окне, считаем как Image (UniformToFill: масштаб по большей стороне,
        // по центру), и в карточке он сдвинут ровно на её положение
        var picture = new WriteableBitmap(new PixelSize(480, 270), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        backdrop.Source = picture;
        window.FindControl<Grid>("Root")!.Classes.Add("backdrop");
        Layout(window);
        var shown = Assert.IsType<BackdropWindow.Shown>(hero.ShownPicture);
        Assert.Same(picture, shown.Source);
        Assert.False(hero.LinesShown);
        var size = backdrop.Bounds.Size;
        var scale = Math.Max(size.Width / 480, size.Height / 270);
        var drawn = new Rect(size).CenterRect(new Rect(0, 0, 480 * scale, 270 * scale)).Intersect(new Rect(size))
            .Translate(BackdropWindow.Origin(backdrop, window)!.Value);
        Assert.Equal(drawn.Translate(-(Vector)heroAt), shown.To);
        Assert.Equal(BitmapInterpolationMode.HighQuality, shown.Mode); // как у самой картинки фона

        // галочку выключили — картинки нет, снова линии
        backdrop.Source = null;
        window.FindControl<Grid>("Root")!.Classes.Remove("backdrop");
        Layout(window);
        Assert.Null(hero.ShownPicture);
        Assert.True(hero.LinesShown);
    }

    // окно не показано: очередь раскладки у него не работает, поэтому раскладка — вручную и заново целиком (картинка,
    // сменившая Source, иначе осталась бы неизмеренной). Пересчёт «окна», который в показанном окне идёт после прохода
    // раскладки, — напрямую
    private static void Layout(MainWindow window)
    {
        foreach (var control in window.GetVisualDescendants().OfType<Layoutable>()) control.InvalidateMeasure();
        window.InvalidateMeasure();
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));
        foreach (var hole in window.GetVisualDescendants().OfType<BackdropWindow>()) hole.Place();
    }
}
