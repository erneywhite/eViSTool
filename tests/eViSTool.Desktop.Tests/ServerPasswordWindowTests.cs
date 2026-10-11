using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Localization;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Dialogs;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно пароля сервера: вид (заголовок с именем сервера, скрытое поле с фокусом) и ответ, который получает тот, кто
/// спрашивает: введённая строка по «Готово» и Enter (при пустом поле — пустая строка, а не null), null — по «Отмена»,
/// Escape и крестику. Окно открывается тем же <see cref="DialogWindow.ShowModal{TResult}"/>, что и в программе.
/// </summary>
public sealed class ServerPasswordWindowTests
{
    private const string Server = "Деревня у реки";

    private static ServerPasswordWindow NewWindow() => new() { Server = Server };

    /// <summary>
    /// Открыть окно модально, как программа, и пока оно ждёт ответа — выполнить действие над ним. Действие идёт из
    /// очереди диспетчера: вложенный цикл окна крутит её, пока окно не закроют.
    /// </summary>
    private static string? Ask(ServerPasswordWindow window, Action<ServerPasswordWindow> act)
    {
        Dispatcher.UIThread.Post(() =>
        {
            window.UpdateLayout();
            act(window);
        }, DispatcherPriority.Background);
        return window.ShowModal<string>();
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static Button ButtonWith(Window window, string key) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Loc.T(key)));

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    [AvaloniaFact]
    public void Opens_with_server_in_heading_and_focused_hidden_field()
    {
        var window = NewWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Loc.T("browse.passwordFor", Server), window.Heading.Text);
        Assert.Equal(Loc.T("browse.passwordTitle"), window.Title);
        var box = Named<SecretTextBox>(window, "ServerPassword");
        Assert.Equal('●', box.PasswordChar);
        Assert.True(box.IsFocused);
        Assert.True(string.IsNullOrEmpty(box.Text));
        window.Close();
    }

    [AvaloniaFact]
    public void Done_answers_with_typed_password()
    {
        var answer = Ask(NewWindow(), window =>
        {
            Named<SecretTextBox>(window, "ServerPassword").Text = "сезам-2026";
            Click(window, ButtonWith(window, "common.done"));
        });

        Assert.Equal("сезам-2026", answer);
    }

    [AvaloniaFact]
    public void Done_with_empty_field_answers_empty_string_not_null()
    {
        var answer = Ask(NewWindow(), window => Click(window, ButtonWith(window, "common.done")));

        Assert.Equal("", answer);
    }

    [AvaloniaFact]
    public void Enter_in_field_confirms_like_done()
    {
        var answer = Ask(NewWindow(), window =>
        {
            var box = Named<SecretTextBox>(window, "ServerPassword");
            box.Text = "qwerty";
            box.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        });

        Assert.Equal("qwerty", answer);
    }

    [AvaloniaFact]
    public void Cancel_answers_nothing_even_with_text_typed()
    {
        var answer = Ask(NewWindow(), window =>
        {
            Named<SecretTextBox>(window, "ServerPassword").Text = "не отдавать";
            Click(window, ButtonWith(window, "common.cancel"));
        });

        Assert.Null(answer);
    }

    [AvaloniaFact]
    public void Escape_answers_nothing()
    {
        var answer = Ask(NewWindow(), window =>
        {
            Named<SecretTextBox>(window, "ServerPassword").Text = "не отдавать";
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        });

        Assert.Null(answer);
    }

    [AvaloniaFact]
    public void Closing_window_answers_nothing()
    {
        var answer = Ask(NewWindow(), window =>
        {
            Named<SecretTextBox>(window, "ServerPassword").Text = "не отдавать";
            window.Close();
        });

        Assert.Null(answer);
    }
}
