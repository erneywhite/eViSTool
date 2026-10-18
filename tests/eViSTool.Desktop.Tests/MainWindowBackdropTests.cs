using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using eViSTool.Desktop.Views;
using GlyphPath = Avalonia.Controls.Shapes.Path;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Боковая панель главного окна и фон из игры (docs/design.md, раздел 5) — настоящая разметка окна и лесная тема:
/// без картинки — цвет Side и «горизонтали», с картинкой (класс backdrop у Root) — вуаль над ней, вуалевые выбранный
/// и наведённый пункт меню и карточка профиля. Вуаль пункта меню задаётся через часть его шаблона (Border#Bd): поменяется
/// шаблон в теме — тест упадёт, а не вуаль тихо пропадёт. Окно не показывается и живёт без модели: только разметка и стили.
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
        Assert.True(window.FindControl<GlyphPath>("Contours")!.IsVisible);
        Assert.Same(window.FindResource("Forest.Surface"), window.FindControl<Border>("ProfileCard")!.Background);
        Assert.Null(window.FindControl<Image>("Backdrop")!.Source);
    }

    [AvaloniaFact]
    public void With_a_picture_veil_and_veil_brushes_in_the_menu()
    {
        var window = Window();
        var nav = window.FindControl<ListBox>("Nav")!;
        nav.SelectedIndex = 0;
        window.FindControl<Grid>("Root")!.Classes.Add("backdrop");

        Assert.Same(window.FindResource("Forest.SideVeil"), window.FindControl<Border>("SideUnderlay")!.Background);
        Assert.False(window.FindControl<GlyphPath>("Contours")!.IsVisible);
        Assert.Same(window.FindResource("Forest.SideCardVeil"), window.FindControl<Border>("ProfileCard")!.Background);

        // невидимое окно разворачивает в меню только первый пункт (остальные — без темы пункта меню), на нём и проверяем
        var item = Assert.IsType<ListBoxItem>(nav.ContainerFromIndex(0));
        Assert.Same(window.FindResource("Forest.NavSelectedVeil"), Bd(item).Background);

        // наведение — псевдоклассом напрямую: мышь окну без показа не нужна
        nav.SelectedIndex = -1;
        ((IPseudoClasses)item.Classes).Set(":pointerover", true);
        Assert.Same(window.FindResource("Forest.NavHoverVeil"), Bd(item).Background);
    }
}
