using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Views;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Шапка главного окна: верхняя полоса (список профилей и «Играть» не наезжают друг на друга при любой ширине окна),
/// перетаскивание окна за полосу на Linux (нажатие в меню «Играть» окно не тащит), выбор рамки на Linux и имена кнопок
/// окна для экранного диктора.
/// </summary>
public sealed class MainWindowChromeTests : IDisposable
{
    private static readonly Uri Base = new("avares://eViSTool.Desktop.Tests");

    private readonly ResourceInclude[] _resources =
    [
        new(Base) { Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml") },
        new(Base) { Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml") },
        new(Base) { Source = new Uri("avares://eViSTool/Themes/ForestWindowChrome.axaml") },
    ];

    private readonly StyleInclude _styles = new(Base) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") };
    private readonly string _language = Loc.Instance.Language;

    public MainWindowChromeTests()
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
        Loc.Instance.SetLanguage(_language);
    }

    private static Control Named(Visual root, string automationId) =>
        root.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    // ---------- верхняя полоса

    /// <summary>
    /// Худший случай: значок ошибок модов, длинное имя клиентского профиля и «Играть · &lt;длинный сервер&gt;». При любой
    /// ширине окна список профилей кончается левее «Играть» и подпись — левее списка; список ужимается до 160, но не
    /// шире 340, а с <see cref="MainWindow.ProfileComboRoomyFrom"/> — не уже 240. Раньше полоса была прижата вправо без
    /// растягивания и при ширине 1100–1190 список ложился поверх «Играть».
    /// </summary>
    [AvaloniaTheory]
    [InlineData(860)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1099)]
    [InlineData(1100)]
    [InlineData(1150)]
    [InlineData(1199)]
    [InlineData(1200)]
    [InlineData(1400)]
    [InlineData(2560)]
    public void Profile_list_never_covers_the_play_button(double width)
    {
        var window = new MainWindow();
        // без показа и без модели: Opened окна ждёт главную модель, а полосе хватает разметки, стилей и раскладки
        window.Measure(window.ClientSize);
        window.Arrange(new Rect(window.ClientSize));

        var badge = Named(window, "ErrorBadge");
        var play = Named(window, "PlayButton");
        badge.IsVisible = play.IsVisible = Named(window, "PlayMenuButton").IsVisible = true;
        window.FindControl<TextBlock>("PlayLabel")!.Text = "Играть · TOPS — The Old Professional Server (PvE, моды, 24/7)";
        var combo = window.FindControl<ComboBox>("ProfileCombo")!;
        combo.ItemsSource = new[]
        {
            new ProfileViewModel(new GameProfile { Name = "Основной с модами для сервера друзей и семьи", Kind = ProfileKind.Client }, _ => { }),
        };
        combo.SelectedIndex = 0;
        var label = window.FindControl<TextBlock>("ActiveProfileLabel")!;

        window.FitTopBar(width);
        // полоса — во всю правую часть окна (без боковой колонки 210). Окно не показано, очереди раскладки у него нет:
        // заново и целиком вручную (при той же ширине, что в первом проходе, полоса иначе не перемерилась бы)
        var bar = window.FindControl<Border>("TopBar")!;
        foreach (var control in bar.GetVisualDescendants().OfType<Layoutable>()) control.InvalidateMeasure();
        bar.InvalidateMeasure();
        bar.Measure(new Size(width - 210, 60));
        bar.Arrange(new Rect(0, 0, width - 210, 60));

        double Left(Visual v) => v.TranslatePoint(default, bar)!.Value.X;
        double Right(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width, 0), bar)!.Value.X;

        Assert.True(Right(combo) <= Left(play) + 0.5, $"окно {width}: список до {Right(combo)}, «Играть» с {Left(play)}");
        Assert.True(Left(badge) >= 0 && Right(badge) <= Left(combo) + 0.5, $"окно {width}: значок ошибок {Left(badge)}–{Right(badge)}, список с {Left(combo)}");
        Assert.Equal(width >= MainWindow.TopBarWideFrom, label.IsVisible);
        if (label.IsVisible) Assert.True(Right(label) <= Left(combo) + 0.5, $"окно {width}: подпись до {Right(label)}, список с {Left(combo)}");
        Assert.InRange(combo.Bounds.Width, width >= MainWindow.ProfileComboRoomyFrom ? 240 : 160, 340);
        Assert.True(Right(play) <= bar.Bounds.Width, $"окно {width}: «Играть» до {Right(play)} из {bar.Bounds.Width}");
    }

    // ---------- перетаскивание за полосу (Linux)

    /// <summary>
    /// Нажатие в меню, которое лежит в полосе как Popup, всплывает к полосе через Popup, хотя на экране оно в своём окне
    /// попапа. Полоса его не считает своим: на Linux такое нажатие тащило окно, а двойное — разворачивало.
    /// </summary>
    [AvaloniaFact]
    public void Press_inside_a_popup_of_the_strip_is_not_a_press_on_the_strip()
    {
        var label = new TextBlock { Text = "Активный профиль" };
        var hint = new TextBlock { Text = "Булавка — сервер по умолчанию" };
        var menu = new Popup { Child = new Border { Padding = new Thickness(8), Child = hint } };
        var strip = new Border { Height = 60, Background = Avalonia.Media.Brushes.Transparent, Child = new DockPanel { Children = { menu, label } } };
        menu.PlacementTarget = label;
        var window = new Window { Width = 400, Height = 300, Content = new StackPanel { Children = { strip } } };
        var pressed = new List<bool>();
        strip.PointerPressed += (sender, e) => pressed.Add(MainWindow.PressedOn((Visual)sender!, e.Source));
        window.Show();
        menu.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        try
        {
            // где живёт меню, решает платформа: в своём окне попапа (Windows, X11) или в слое над окном (здесь, без
            // экрана) — в обоих случаях нажатие идёт к полосе через Popup, а по дереву на экране оно не в ней. Нажатие —
            // самим событием от подсказки: путь события тот же, что у мыши, а куда попала мышь, здесь не проверяем
            Assert.NotNull(TopLevel.GetTopLevel(hint));
            Assert.False(strip.IsVisualAncestorOf(hint));
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
            hint.RaiseEvent(new PointerPressedEventArgs(hint, pointer, window, default, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            Assert.Equal(new[] { false }, pressed); // дошло до полосы, но не её

            menu.IsOpen = false;
            Mouse.Click(window, label); // подпись на самой полосе — её
            Assert.Equal(new[] { false, true }, pressed);
        }
        finally
        {
            menu.IsOpen = false;
            window.Close();
        }
        Assert.True(MainWindow.PressedOn(strip, strip));
        Assert.True(MainWindow.PressedOn(strip, null));
    }

    // ---------- рамка на Linux

    /// <summary>
    /// Своя рамка на Linux — пока её не выключили в настройках и окно может быть прозрачным (работает композитор): без
    /// композитора тень своей рамки — сплошная полоса цвета окна.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false, "Transparent", true)]
    [InlineData(false, "None", false)]
    [InlineData(true, "Transparent", false)]
    [InlineData(true, "None", false)]
    public void Own_frame_on_linux_needs_the_setting_and_a_compositor(bool systemFrameSetting, string level, bool own)
    {
        var transparency = level == "None" ? WindowTransparencyLevel.None : WindowTransparencyLevel.Transparent;
        Assert.Equal(own, MainWindow.OwnFrameOnLinux(systemFrameSetting, transparency));
    }

    // ---------- кнопки окна

    /// <summary>
    /// Кнопки своей шапки: имена для экранного диктора и подсказки — на языке программы, средняя — «Развернуть» или
    /// «Восстановить» по состоянию окна, и меняются на ходу. Avalonia пишет кнопкам свои английские имена после сборки
    /// шаблона — тема их перебивает (Controls/CaptionButtonName).
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Forest.WindowChromeWin", false)]
    [InlineData("Forest.WindowChromeLinux", true)]
    public void Caption_buttons_are_named_in_the_app_language_and_by_window_state(string themeKey, bool maximizeTip)
    {
        Loc.Instance.SetLanguage("ru");
        var window = new Window { Width = 400, Height = 300 };
        window.Show();
        try
        {
            var buttons = Decorate(window, (ControlTheme)window.FindResource(themeKey)!);
            Button Part(string name) => buttons.Single(b => b.Name == name);
            void AssertNamed(string part, string text, bool tip = true)
            {
                Assert.Equal(text, AutomationProperties.GetName(Part(part)));
                Assert.Equal(tip ? text : null, ToolTip.GetTip(Part(part)));
            }

            AssertNamed("PART_MinimizeButton", "Свернуть");
            AssertNamed("PART_MaximizeButton", "Развернуть", maximizeTip);
            AssertNamed("PART_CloseButton", "Закрыть");

            window.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            AssertNamed("PART_MaximizeButton", "Восстановить", maximizeTip);

            Loc.Instance.SetLanguage("en");
            AssertNamed("PART_MaximizeButton", "Restore down", maximizeTip);
            AssertNamed("PART_CloseButton", "Close");
            window.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            AssertNamed("PART_MaximizeButton", "Maximize", maximizeTip);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Своя шапка в окне без экрана. У Avalonia.Headless рисованной шапки нет (NeedsManagedDecorations — false), поэтому
    /// строим её тем же внутренним вызовом, которым это делает окно на Windows и Linux (TopLevelHost.UpdateDrawnDecorations):
    /// сборка шаблона, английские имена от Avalonia, подключение слоёв к окну — всё по-настоящему. Поменяется этот вызов в
    /// новой Avalonia — тест упадёт здесь, и проверить надо будет и сами имена.
    /// </summary>
    private static List<Button> Decorate(Window window, ControlTheme theme)
    {
        var host = typeof(TopLevel).GetProperty("TopLevelHost", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        var update = host.GetType().GetMethod("UpdateDrawnDecorations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var parts = Enum.Parse(Nullable.GetUnderlyingType(update.GetParameters()[0].ParameterType)!, "TitleBar");
        update.Invoke(host, [parts, window.WindowState, theme]);
        Dispatcher.UIThread.RunJobs();
        ((Layoutable)host).UpdateLayout();
        var buttons = ((Visual)host).GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith("PART_", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(3, buttons.Count);
        return buttons;
    }
}
