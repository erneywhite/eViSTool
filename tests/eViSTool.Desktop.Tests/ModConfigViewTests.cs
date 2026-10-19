using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Localization;
using eViSTool.Desktop.Views;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// «Настройки модов» без модели — раскладка. Колонка списка в узком окне ужимается, и карточка редактора не уезжает за
/// край; окно снова шире — список прежней ширины, в том числе выбранной сплиттером.
/// </summary>
public sealed class ModConfigViewTests
{
    /// <summary>Поля вида — 10 слева и справа: колонкам достаётся ширина окна без 20.</summary>
    private const double ViewMargins = 20;

    private static Window Show(Control content, double width)
    {
        var baseUri = new Uri("avares://eViSTool.Desktop.Tests");
        var window = new Window { Width = width, Height = 700, RequestedThemeVariant = ThemeVariant.Dark, Content = content };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") });
        window.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") });
        window.Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Resize(Window window, double width)
    {
        window.Width = width;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Narrow_window_squeezes_the_list_and_keeps_the_editor_inside()
    {
        var view = new ModConfigView();
        // 598 у колонок — окно 860 (наименьшее) без боковой панели 210, полей страниц 32 и полей вида
        var window = Show(view, 598 + ViewMargins);
        var columns = view.FindControl<Grid>("Columns")!;
        var list = columns.ColumnDefinitions[0];
        var editor = columns.Children.OfType<Border>().Single(b => Grid.GetColumn(b) == 2);
        void AssertEditorInside()
        {
            Assert.True(editor.Bounds.Right <= columns.Bounds.Width + 0.5, $"карточка до {editor.Bounds.Right}, колонкам {columns.Bounds.Width}");
            Assert.True(editor.Bounds.Width >= columns.ColumnDefinitions[2].MinWidth - 0.5, $"карточка {editor.Bounds.Width}");
            Assert.True(list.ActualWidth >= list.MinWidth - 0.5, $"список {list.ActualWidth}");
        }

        Assert.Equal(598, columns.Bounds.Width, 1);
        AssertEditorInside();

        // широкое окно — список своей ширины
        Resize(window, 1400);
        Assert.Equal(340, list.ActualWidth, 1);

        // ширина от сплиттера переживает узкое окно
        list.Width = new GridLength(500);
        Resize(window, 598 + ViewMargins);
        AssertEditorInside();
        Resize(window, 1400);
        Assert.Equal(500, list.ActualWidth, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void Refresh_button_with_icon_is_named_by_its_text()
    {
        var view = new ModConfigView();
        var window = Show(view, 1200);
        var refresh = view.GetVisualDescendants().OfType<Button>().First(b => b.Content is StackPanel);
        Assert.Equal(Loc.T("mcfg.refresh"), ControlAutomationPeer.CreatePeerForElement(refresh).GetName());
        window.Close();
    }
}
