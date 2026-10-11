using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FocusOn = eViSTool.Desktop.Controls.Focus;

namespace eViSTool.Desktop.Tests;

public class FocusTests
{
    private static (Window Window, TextBox Other, TextBox Name) Build(bool selectAll)
    {
        var other = new TextBox { Text = "first" };
        var name = new TextBox { Text = "Мой профиль" };
        FocusOn.SetOnOpen(name, true);
        FocusOn.SetSelectAll(name, selectAll);
        var window = new Window { Content = new StackPanel { Children = { other, name } } };
        return (window, other, name);
    }

    [AvaloniaFact]
    public void Field_gets_focus_when_window_opens()
    {
        var (window, other, name) = Build(selectAll: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(name.IsFocused);
        Assert.False(other.IsFocused);
        Assert.Equal(name.SelectionStart, name.SelectionEnd); // без SelectAll текст не выделен
        window.Close();
    }

    [AvaloniaFact]
    public void SelectAll_selects_whole_text()
    {
        var (window, _, name) = Build(selectAll: true);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(name.IsFocused);
        Assert.Equal(0, name.SelectionStart);
        Assert.Equal(name.Text!.Length, name.SelectionEnd);
        window.Close();
    }

    /// <summary>
    /// Окно собрали заранее и очередь успела поработать, а показали позже. Loaded приходит только с показом — на этом
    /// держится Focus.OnOpen; поменяется в Avalonia — тест упадёт.
    /// </summary>
    [AvaloniaFact]
    public void Focus_waits_for_window_shown_later()
    {
        var (window, _, name) = Build(selectAll: false);
        Dispatcher.UIThread.RunJobs();
        Assert.False(name.IsLoaded);
        Assert.False(name.IsFocused);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(name.IsFocused);
        window.Close();
    }
}
