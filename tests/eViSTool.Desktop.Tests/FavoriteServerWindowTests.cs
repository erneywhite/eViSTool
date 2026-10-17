using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно «Сервер в избранном игры» с настоящей моделью и лесной темой: куда встаёт фокус (в название у нового, в адрес у
/// правки), пароль в скрытом поле, Enter и «Сохранить» при плохом и хорошем адресе, «Отмена» и Escape. Диска и сети не
/// касается; пароль выдуманный.
/// </summary>
public sealed class FavoriteServerWindowTests : IDisposable
{
    private readonly List<ResourceInclude> _theme = [];
    private StyleInclude? _themeStyles;

    public void Dispose()
    {
        if (Application.Current is not { } app) return;
        foreach (var include in _theme) app.Resources.MergedDictionaries.Remove(include);
        if (_themeStyles is not null) app.Styles.Remove(_themeStyles);
    }

    /// <summary>Лесная тема окна (в тестовом приложении только Fluent): шаблоны кнопок и классы подписей.</summary>
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

    private static readonly PlayTarget Saved = new(
        "fav:Сервер друзей|play.example.org:42421", PlayTargetKind.Favorite, "Сервер друзей", "play.example.org:42421", "made-up-password");

    /// <summary>Окно, модель и ответы, с которыми модель закрывала окно.</summary>
    private (FavoriteServerWindow Window, FavoriteServerViewModel Model, List<FavoriteServerChoice> Answers) Open(PlayTarget? existing)
    {
        AddTheme();
        var model = new FavoriteServerViewModel(existing);
        var answers = new List<FavoriteServerChoice>();
        model.Finished += answers.Add;
        var window = new FavoriteServerWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, model, answers);
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    [AvaloniaFact]
    public void New_opens_empty_with_name_focused_and_password_hidden()
    {
        var (window, _, _) = Open(null);

        var name = Named<TextBox>(window, "FavName");
        var address = Named<TextBox>(window, "FavAddress");
        var password = Named<SecretTextBox>(window, "FavPassword");
        Assert.True(name.IsFocused);
        Assert.False(address.IsFocused);
        Assert.Equal("", name.Text ?? "");
        Assert.Equal("", address.Text ?? "");
        Assert.Equal('●', password.PasswordChar);
        Assert.Equal(80, name.MaxLength);
        Assert.Equal(255, address.MaxLength);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("error") && t.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Edit_opens_with_record_and_address_focused()
    {
        var (window, _, _) = Open(Saved);

        var address = Named<TextBox>(window, "FavAddress");
        Assert.True(address.IsFocused);
        Assert.False(Named<TextBox>(window, "FavName").IsFocused);
        Assert.Equal("Сервер друзей", Named<TextBox>(window, "FavName").Text);
        Assert.Equal("play.example.org:42421", address.Text);
        Assert.Equal("made-up-password", Named<SecretTextBox>(window, "FavPassword").Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_goes_to_the_model_and_save_closes_with_the_answer()
    {
        var (window, model, answers) = Open(null);

        window.KeyTextInput("Дом"); // фокус в названии
        var address = Named<TextBox>(window, "FavAddress");
        address.Focus();
        window.KeyTextInput("192.168.1.20");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Дом", model.Name);
        Assert.Equal("192.168.1.20", model.Address);

        Mouse.Click(window, Named<Button>(window, "FavSave"));

        Assert.Equal([new FavoriteServerChoice("Дом", "192.168.1.20", null)], answers);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Save_with_bad_address_shows_the_error_and_keeps_the_window()
    {
        var (window, model, answers) = Open(null);
        var address = Named<TextBox>(window, "FavAddress");
        address.Focus();
        window.KeyTextInput("play example.org");
        Dispatcher.UIThread.RunJobs();

        Mouse.Click(window, Named<Button>(window, "FavSave"));

        Assert.True(window.IsVisible);
        Assert.Empty(answers);
        Assert.Null(model.Result);
        var error = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("error"));
        Assert.True(error.IsEffectivelyVisible);
        Assert.Equal(Loc.T("browse.favBadAddress"), error.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_in_a_field_saves_like_the_default_button()
    {
        var (window, _, answers) = Open(Saved);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); // фокус в адресе
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([new FavoriteServerChoice("Сервер друзей", "play.example.org:42421", "made-up-password")], answers);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Enter_with_empty_address_shows_the_error_instead_of_closing()
    {
        var (window, model, answers) = Open(null);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.Empty(answers);
        Assert.True(model.HasError);
        window.Close();
    }

    [AvaloniaFact]
    public void Cancel_and_Escape_close_without_an_answer()
    {
        var (byCancel, _, cancelAnswers) = Open(Saved);
        Mouse.Click(byCancel, byCancel.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == Loc.T("common.cancel")));
        Assert.False(byCancel.IsVisible);
        Assert.Empty(cancelAnswers);

        var (byEscape, _, escapeAnswers) = Open(Saved);
        byEscape.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(byEscape.IsVisible);
        Assert.Empty(escapeAnswers);
    }
}
