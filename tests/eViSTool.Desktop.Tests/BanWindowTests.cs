using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Localization;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно бана с настоящей моделью: фокус в поле причины, пять чипов срока на одном свойстве модели (щелчок и правка из
/// модели), Enter на чипе и в поле причины — «Забанить», «Отмена» и Escape закрывают без ответа.
/// </summary>
public sealed class BanWindowTests : IDisposable
{
    private readonly List<ResourceInclude> _theme = [];
    private StyleInclude? _themeStyles;

    public void Dispose()
    {
        if (Application.Current is not { } app) return;
        foreach (var include in _theme) app.Resources.MergedDictionaries.Remove(include);
        if (_themeStyles is not null) app.Styles.Remove(_themeStyles);
    }

    /// <summary>Лесная тема окна (в тестовом приложении только Fluent): чипы, кнопки и подписи.</summary>
    private void AddTheme()
    {
        var app = Application.Current!;
        foreach (var name in new[] { "ForestResources", "ForestControls" })
        {
            var include = new ResourceInclude(new Uri("avares://eViSTool.Desktop.Tests")) { Source = new Uri($"avares://eViSTool/Themes/{name}.axaml") };
            _theme.Add(include);
            app.Resources.MergedDictionaries.Add(include);
        }
        _themeStyles = new StyleInclude(new Uri("avares://eViSTool.Desktop.Tests")) { Source = new Uri("avares://eViSTool/Themes/Forest.axaml") };
        app.Styles.Add(_themeStyles);
    }

    private (BanWindow Window, BanViewModel Model) Open(string player = "Вася")
    {
        AddTheme();
        var model = new BanViewModel(player);
        var window = new BanWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static readonly string[] ChipIds = ["BanHour", "BanDay", "BanWeek", "BanMonth", "BanForever"];

    private static RadioButton[] Chips(Window window) => ChipIds.Select(id => Named<RadioButton>(window, id)).ToArray();

    /// <summary>Закрылось ли окно и с чем: Closed приходит, ответ — в модели (null — без ответа).</summary>
    private static Func<bool> WatchClosed(Window window)
    {
        var closed = false;
        window.Closed += (_, _) => closed = true;
        return () => closed;
    }

    [AvaloniaFact]
    public void Opens_with_reason_focused_and_day_chip_checked()
    {
        var (window, model) = Open();

        Assert.True(Named<TextBox>(window, "BanReason").IsFocused);
        Assert.Equal([false, true, false, false, false], Chips(window).Select(c => c.IsChecked == true));
        Assert.Equal(Loc.T("players.banTitle"), window.Title);
        Assert.Equal(200, Named<TextBox>(window, "BanReason").MaxLength);
        Assert.Equal(BanTerm.Day, model.Term);
        window.Close();
    }

    [AvaloniaFact]
    public void Chips_drive_the_term_and_follow_it()
    {
        var (window, model) = Open();
        var chips = Chips(window);

        BanTerm[] terms = [BanTerm.Hour, BanTerm.Day, BanTerm.Week, BanTerm.Month, BanTerm.Forever];
        for (var i = 0; i < chips.Length; i++)
        {
            Mouse.Click(window, chips[i]);
            Assert.Equal(terms[i], model.Term);
            Assert.Equal(chips.Select((_, j) => j == i), chips.Select(c => c.IsChecked == true));
        }

        // и обратно: после щелчков чипы по-прежнему следуют за моделью
        model.Term = BanTerm.Week;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([false, false, true, false, false], chips.Select(c => c.IsChecked == true));
        window.Close();
    }

    [AvaloniaFact]
    public void Typed_reason_reaches_the_model()
    {
        var (window, model) = Open();

        window.KeyTextInput(" грифер ");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(" грифер ", model.Reason);
        window.Close();
    }

    [AvaloniaFact]
    public void Ok_button_closes_window_with_the_choice()
    {
        var (window, model) = Open("Вася");
        var closed = WatchClosed(window);
        window.KeyTextInput("  спам ");
        Mouse.Click(window, Chips(window)[3]); // щелчок уводит фокус на чип, так что причину вводим раньше

        Mouse.Click(window, Named<Button>(window, "BanOk"));

        Assert.True(closed());
        Assert.Equal(new BanChoice(30, "day", "спам"), model.Result);
    }

    [AvaloniaFact]
    public void Enter_in_reason_bans()
    {
        var (window, model) = Open();
        var closed = WatchClosed(window);
        window.KeyTextInput("читы");

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed());
        Assert.Equal(new BanChoice(1, "day", "читы"), model.Result);
    }

    [AvaloniaFact]
    public void Enter_on_a_chip_bans_without_switching_it()
    {
        var (window, model) = Open();
        var closed = WatchClosed(window);
        var chips = Chips(window);
        chips[4].Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed());
        Assert.Equal(new BanChoice(1, "day", ""), model.Result); // Enter чип не переключил: срок остался «день»
        Assert.True(chips[1].IsChecked);
    }

    [AvaloniaFact]
    public void Cancel_and_escape_close_without_answer()
    {
        var (window, model) = Open();
        var closed = WatchClosed(window);
        Mouse.Click(window, Named<Button>(window, "BanCancel"));
        Assert.True(closed());
        Assert.Null(model.Result);

        (window, model) = Open();
        closed = WatchClosed(window);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.True(closed());
        Assert.Null(model.Result);
    }

    [AvaloniaFact]
    public void Long_player_name_wraps_the_heading_instead_of_widening_the_window()
    {
        var (window, _) = Open("Очень_Длинный_Ник_Игрока_Который_Не_Помещается_В_Одну_Строку_Заголовка");

        Assert.Equal(444, window.Width);
        var heading = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text?.StartsWith(Loc.T("players.banHeading", "")) == true);
        Assert.True(heading.Bounds.Width <= 396 + 0.5, $"заголовок вылез: {heading.Bounds.Width}");
        Assert.True(heading.Bounds.Height > 30, "заголовок не перенёсся на вторую строку");
        window.Close();
    }
}
