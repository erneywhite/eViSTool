using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// «Назад» на странице (<see cref="BackGesture"/>): Esc и кнопка «Назад» мыши выполняют команду страницы, пока она
/// видна и ей есть что делать. Esc в поле поиска с текстом сначала очищает поле, в чужом поле — не трогает ничего;
/// Esc, который забрал выпадающий список, команду не выполняет.
/// </summary>
public sealed class BackGestureTests
{
    /// <summary>Команда «снять выбор»: доступна, пока <see cref="HasSelection"/>; выполнение снимает его.</summary>
    private sealed class ClearCommand : ICommand
    {
        public bool HasSelection { get; set; } = true;
        public int Runs { get; private set; }

        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => HasSelection;

        public void Execute(object? parameter)
        {
            Runs++;
            HasSelection = false;
        }
    }

    private sealed class Stand
    {
        public required Window Window { get; init; }
        public required UserControl Page { get; init; }
        public required TextBox Search { get; init; }
        public required ComboBox Combo { get; init; }
        public required Button Button { get; init; }
        public required TextBox Other { get; init; }
        public required ClearCommand Command { get; init; }

        /// <summary>Нажатия Esc, дошедшие до окна, и были ли они к тому времени обработаны.</summary>
        public List<bool> EscHandled { get; } = [];
    }

    /// <summary>Окно: страница с полем поиска, выпадающим списком и кнопкой; вне страницы — ещё одно поле.</summary>
    private static Stand Show()
    {
        var search = new TextBox { Classes = { "search" } };
        var combo = new ComboBox { ItemsSource = new[] { "Сначала важное", "По имени" }, SelectedIndex = 0 };
        var button = new Button { Content = "Кнопка" };
        var command = new ClearCommand();
        var page = new UserControl { Content = new StackPanel { Children = { search, combo, button } } };
        BackGesture.SetCommand(page, command);
        var other = new TextBox { Text = "чужое поле" };
        var window = new Window { Width = 500, Height = 400, Content = new StackPanel { Children = { page, other } } };
        window.Show();
        Mouse.Prepare(window);
        var stand = new Stand { Window = window, Page = page, Search = search, Combo = combo, Button = button, Other = other, Command = command };
        // после всех: что стало с Esc, когда он дошёл до окна
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) stand.EscHandled.Add(e.Handled); },
            RoutingStrategies.Bubble, handledEventsToo: true);
        return stand;
    }

    private static void Esc(Window window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Back(Window window, Visual over)
    {
        var at = Mouse.Center(window, over);
        window.MouseDown(at, MouseButton.XButton1);
        window.MouseUp(at, MouseButton.XButton1);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Esc_runs_the_command_while_the_page_is_visible()
    {
        var s = Show();
        s.Button.Focus();
        Esc(s.Window);
        Assert.Equal(1, s.Command.Runs);
        Assert.Equal([true], s.EscHandled);

        // снимать больше нечего — Esc идёт дальше нетронутым
        Esc(s.Window);
        Assert.Equal(1, s.Command.Runs);
        Assert.Equal([true, false], s.EscHandled);
        s.Window.Close();
    }

    [AvaloniaFact]
    public void Esc_works_with_focus_outside_the_page_too()
    {
        var s = Show();
        s.Window.Focus();
        Esc(s.Window);
        Assert.Equal(1, s.Command.Runs);
        s.Window.Close();
    }

    [AvaloniaFact]
    public void Back_mouse_button_runs_the_command_over_any_place_of_the_window()
    {
        var s = Show();
        Back(s.Window, s.Button); // кнопка забирает нажатие себе — «Назад» всё равно срабатывает
        Assert.Equal(1, s.Command.Runs);

        s.Command.HasSelection = true;
        Back(s.Window, s.Other); // и над местом вне страницы
        Assert.Equal(2, s.Command.Runs);

        // другие кнопки мыши — нет
        s.Command.HasSelection = true;
        var at = Mouse.Center(s.Window, s.Other);
        foreach (var button in new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.XButton2 })
        {
            s.Window.MouseDown(at, button);
            s.Window.MouseUp(at, button);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, s.Command.Runs);
        s.Window.Close();
    }

    [AvaloniaFact]
    public void Browser_back_key_works_like_the_mouse_button_even_in_the_search_box()
    {
        var s = Show();
        s.Search.Focus();
        s.Window.KeyTextInput("alpha");
        s.Window.KeyPress(Key.BrowserBack, RawInputModifiers.None, PhysicalKey.BrowserBack, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, s.Command.Runs);
        Assert.Equal("alpha", s.Search.Text); // поиск не тронут: очищает только Esc
        s.Window.Close();
    }

    [AvaloniaFact]
    public void Hidden_page_ignores_esc_and_back()
    {
        var s = Show();
        s.Page.IsVisible = false;
        s.Window.Focus();
        Esc(s.Window);
        Back(s.Window, s.Other);
        Assert.Equal(0, s.Command.Runs);
        Assert.Equal([false], s.EscHandled);
        s.Window.Close();
    }

    [AvaloniaFact]
    public void Esc_in_the_search_box_clears_it_first_and_ctrl_z_brings_it_back()
    {
        var s = Show();
        s.Search.Focus();
        s.Window.KeyTextInput("alpha");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("alpha", s.Search.Text);

        // первый Esc — полю: текст очищен, выбор на месте
        Esc(s.Window);
        Assert.Equal("", s.Search.Text ?? "");
        Assert.Equal(0, s.Command.Runs);
        Assert.Equal([true], s.EscHandled);

        // очистка — обычная правка: Ctrl+Z возвращает введённое
        s.Window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        s.Window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("alpha", s.Search.Text);

        // поле пустое — Esc снимает выбор
        Esc(s.Window);
        Esc(s.Window);
        Assert.Equal("", s.Search.Text ?? "");
        Assert.Equal(1, s.Command.Runs);
        Assert.True(s.Search.IsFocused);
        s.Window.Close();
    }

    [AvaloniaFact]
    public void Esc_in_a_field_outside_the_page_is_left_to_it()
    {
        var s = Show();
        s.Other.Focus();
        Esc(s.Window);
        Assert.Equal("чужое поле", s.Other.Text);
        Assert.Equal(0, s.Command.Runs);
        s.Window.Close();
    }

    [AvaloniaFact]
    public void Esc_that_closes_a_dropdown_does_not_clear_the_selection()
    {
        var s = Show();
        s.Combo.Focus();
        s.Combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        Esc(s.Window);
        Assert.False(s.Combo.IsDropDownOpen);
        Assert.Equal(0, s.Command.Runs);
        Assert.Equal(0, s.Combo.SelectedIndex);

        // следующий Esc — уже странице
        Esc(s.Window);
        Assert.Equal(1, s.Command.Runs);
        s.Window.Close();
    }

    /// <summary>
    /// Меню-всплывашка, которое само на Esc не закрывается (как «Играть ▾» в шапке окна): пока оно открыто, страница Esc и
    /// «Назад» не трогает — выбор под меню не пропадает. Подсказка (всплывающее окно без закрытия щелчком мимо) не мешает.
    /// </summary>
    [AvaloniaFact]
    public void Open_menu_keeps_esc_and_back_away_from_the_page()
    {
        var s = Show();
        var menu = new Popup { PlacementTarget = s.Button, IsLightDismissEnabled = true, Child = new Border { Width = 100, Height = 50 } };
        var tip = new Popup { PlacementTarget = s.Combo, Child = new Border { Width = 60, Height = 20 } };
        var root = (StackPanel)s.Window.Content!;
        root.Children.Add(menu);
        root.Children.Add(tip);
        s.Button.Focus();
        menu.IsOpen = true;
        tip.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        Esc(s.Window);
        s.Window.KeyPress(Key.BrowserBack, RawInputModifiers.None, PhysicalKey.BrowserBack, null);
        Assert.True(menu.IsOpen);
        Assert.Equal(0, s.Command.Runs);

        menu.IsOpen = false;
        Dispatcher.UIThread.RunJobs();
        Esc(s.Window);
        Assert.Equal(1, s.Command.Runs); // открыта только подсказка — Esc снова странице
        tip.IsOpen = false;
        s.Window.Close();
    }

    [AvaloniaFact]
    public void Page_taken_out_of_the_window_stops_listening()
    {
        var s = Show();
        ((StackPanel)s.Window.Content!).Children.Remove(s.Page);
        s.Window.Focus();
        Esc(s.Window);
        Back(s.Window, s.Other);
        Assert.Equal(0, s.Command.Runs);
        s.Window.Close();
    }
}
