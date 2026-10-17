using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно «Дублировать роль» с настоящей разметкой и лесной темой: что остаётся на виде — фокус и выделение в поле кода,
/// ошибка под полем и «Создать копию» от того, что в поле, Enter в полях, «Отмена» и Escape закрывают без ответа.
/// Сама проверка кода и ответ — в модели (тесты в App.Tests).
/// </summary>
public sealed class DuplicateRoleWindowTests : IDisposable
{
    private static readonly string[] Taken = ["suvisitor", "suplayer", "sumod", "admin"];

    private readonly ResourceInclude _resources = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool/Themes/ForestResources.axaml"),
    };

    private readonly ResourceInclude _controls = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool/Themes/ForestControls.axaml"),
    };

    private readonly StyleInclude _styles = new(new Uri("avares://eViSTool.Desktop.Tests"))
    {
        Source = new Uri("avares://eViSTool/Themes/Forest.axaml"),
    };

    public DuplicateRoleWindowTests()
    {
        var app = Application.Current!;
        app.Resources.MergedDictionaries.Add(_resources);
        app.Resources.MergedDictionaries.Add(_controls);
        app.Styles.Add(_styles);
    }

    public void Dispose()
    {
        var app = Application.Current!;
        app.Styles.Remove(_styles);
        app.Resources.MergedDictionaries.Remove(_controls);
        app.Resources.MergedDictionaries.Remove(_resources);
    }

    private static (DuplicateRoleWindow Window, DuplicateRoleViewModel Model) Open()
    {
        var model = new DuplicateRoleViewModel("suplayer", "Survival Player", Taken);
        var window = new DuplicateRoleWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static TextBlock ErrorText(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("error"));

    private static void Press(Window window, Key key) =>
        window.KeyPress(key, RawInputModifiers.None, key == Key.Enter ? PhysicalKey.Enter : PhysicalKey.Escape, null);

    [AvaloniaFact]
    public void Code_field_gets_focus_with_text_selected_and_shows_suggestion()
    {
        var (window, _) = Open();
        var code = Named<TextBox>(window, "DuplicateCode");

        Assert.True(code.IsFocused);
        Assert.Equal("suplayer2", code.Text);
        Assert.Equal(0, code.SelectionStart);
        Assert.Equal(code.Text!.Length, code.SelectionEnd);
        Assert.False(ErrorText(window).IsVisible);
        Assert.True(Named<Button>(window, "DuplicateOk").IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void Taken_code_shows_error_and_disables_the_button_until_fixed()
    {
        var (window, model) = Open();
        var code = Named<TextBox>(window, "DuplicateCode");
        var ok = Named<Button>(window, "DuplicateOk");

        code.Text = "admin";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("admin", model.Code); // поле пишет в модель
        Assert.True(ErrorText(window).IsVisible);
        Assert.Equal(model.Error, ErrorText(window).Text);
        Assert.False(ok.IsEffectivelyEnabled);

        code.Text = "vip";
        Dispatcher.UIThread.RunJobs();

        Assert.False(ErrorText(window).IsVisible);
        Assert.True(ok.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_in_a_field_creates_the_copy_and_closes()
    {
        var (window, model) = Open();
        Named<TextBox>(window, "DuplicateCode").Text = "vip";
        Named<TextBox>(window, "DuplicateName").Text = "Важный гость";
        Dispatcher.UIThread.RunJobs();

        Press(window, Key.Enter);

        Assert.False(window.IsVisible);
        Assert.Equal(new RoleCopyChoice("vip", "Важный гость"), model.Result);
    }

    [AvaloniaFact]
    public void Enter_with_taken_code_does_nothing()
    {
        var (window, model) = Open();
        Named<TextBox>(window, "DuplicateCode").Text = "admin";
        Dispatcher.UIThread.RunJobs();

        Press(window, Key.Enter);

        Assert.True(window.IsVisible);
        Assert.Null(model.Result);
        window.Close();
    }

    [AvaloniaFact]
    public void Ok_button_click_creates_the_copy()
    {
        var (window, model) = Open();

        Mouse.Click(window, Named<Button>(window, "DuplicateOk"));

        Assert.False(window.IsVisible);
        Assert.Equal("suplayer2", model.Result?.Code);
    }

    [AvaloniaFact]
    public void Cancel_button_closes_without_answer()
    {
        var (window, model) = Open();

        Mouse.Click(window, Named<Button>(window, "DuplicateCancel"));

        Assert.False(window.IsVisible);
        Assert.Null(model.Result);
    }

    [AvaloniaFact]
    public void Escape_closes_without_answer()
    {
        var (window, model) = Open();

        Press(window, Key.Escape);

        Assert.False(window.IsVisible);
        Assert.Null(model.Result);
    }
}
