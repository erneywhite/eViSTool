using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Core.Notifications;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Окно канала оповещений с настоящей моделью: фокус при открытии, поля под выбранный тип, поле «Чат» (редактируемый
/// список: найденный чат или свой ID) и закрытие по «Сохранить». Поиск чатов подменён — в сеть тесты не ходят;
/// токены и ID выдуманные.
/// </summary>
public sealed class NotifyChannelWindowTests : IDisposable
{
    private const string Token = "123456789:made-up-token-for-window-tests";

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
            var include = new ResourceInclude(new Uri("avares://eViSTool.Desktop.Tests")) { Source = new Uri($"avares://eViSTool.Desktop/Themes/{name}.axaml") };
            _theme.Add(include);
            app.Resources.MergedDictionaries.Add(include);
        }
        _themeStyles = new StyleInclude(new Uri("avares://eViSTool.Desktop.Tests")) { Source = new Uri("avares://eViSTool.Desktop/Themes/Forest.axaml") };
        app.Styles.Add(_themeStyles);
    }

    private (NotifyChannelWindow Window, NotifyChannelEditorViewModel Model) Open(NotifyChannel channel, bool isNew,
        Func<string, Task<IReadOnlyList<TelegramChat>>>? findChats = null)
    {
        AddTheme();
        var model = new NotifyChannelEditorViewModel(channel, isNew,
            findChats ?? (_ => throw new InvalidOperationException("поиск чатов в этом тесте не ожидался")),
            (_, _) => throw new InvalidOperationException("отправка в этом тесте не ожидалась"));
        var window = new NotifyChannelWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    private static T Named<T>(Window window, string automationId) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == automationId);

    private static NotifyChannel NewTelegram() => new() { Kind = NotifyKind.Telegram, Name = "Telegram" };

    [AvaloniaFact]
    public void Opens_with_name_focused_and_only_telegram_fields()
    {
        var (window, _) = Open(NewTelegram(), isNew: true);

        Assert.True(Named<TextBox>(window, "NotifyName").IsFocused);
        Assert.True(Named<Control>(window, "NotifyToken").IsEffectivelyVisible);
        Assert.IsType<SecretTextBox>(Named<Control>(window, "NotifyToken"));
        Assert.False(Named<Control>(window, "NotifyDiscordUrl").IsEffectivelyVisible);
        Assert.False(Named<Control>(window, "NotifyNtfyTopic").IsEffectivelyVisible);
        Assert.False(Named<Control>(window, "NotifyWebhookUrl").IsEffectivelyVisible);
        Assert.False(Named<Control>(window, "NotifyStatus").IsVisible);
        Assert.True(Named<ComboBox>(window, "NotifyKind").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void Switching_kind_shows_its_fields()
    {
        var (window, model) = Open(NewTelegram(), isNew: true);

        model.SelectedKind = model.Kinds.Single(k => k.Kind == NotifyKind.Webhook);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Named<Control>(window, "NotifyToken").IsEffectivelyVisible);
        Assert.True(Named<Control>(window, "NotifyWebhookUrl").IsEffectivelyVisible);
        Assert.True(Named<Control>(window, "NotifyWebhookTemplate").IsEffectivelyVisible);
        Assert.Equal(model.SelectedKind.Title, Named<TextBox>(window, "NotifyName").Text); // имя пошло за типом
        window.Close();
    }

    /// <summary>Сохранённый чат: в поле — его подпись, а выбор не сбрасывается привязками при открытии окна.</summary>
    [AvaloniaFact]
    public void Saved_chat_shows_its_title_and_stays_selected()
    {
        var channel = new NotifyChannel
        {
            Kind = NotifyKind.Telegram, Name = "Наш сервер", ChatId = "-1001234567890", ChatTitle = "Наш сервер › Анонсы", TopicId = 42,
        };
        var (window, model) = Open(channel, isNew: false);

        var chat = Named<ComboBox>(window, "NotifyChat");
        Assert.Equal("Наш сервер › Анонсы", chat.Text);
        Assert.NotNull(chat.SelectedItem);
        Assert.Equal("-1001234567890", model.SelectedChat?.Id);
        Assert.Equal("Наш сервер › Анонсы", model.ChatText);
        Assert.Equal("42", Named<TextBox>(window, "NotifyTopic").Text);
        Assert.False(Named<ComboBox>(window, "NotifyKind").IsEnabled); // тип сохранённого канала не меняется
        window.Close();
    }

    [AvaloniaFact]
    public async Task Found_chats_fill_the_list_select_first_and_open_it()
    {
        IReadOnlyList<TelegramChat> found = [new("111", "@erney"), new("-1002", "Наш сервер › Анонсы", 7)];
        var (window, model) = Open(NewTelegram(), isNew: true, _ => Task.FromResult(found));
        model.TelegramToken = Token;

        await model.FindChatsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var chat = Named<ComboBox>(window, "NotifyChat");
        Assert.Same(found[0], chat.SelectedItem);
        Assert.Equal("@erney", chat.Text);
        Assert.True(chat.IsDropDownOpen);

        chat.SelectedIndex = 1; // выбрали тему форума — подставился её ID
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Наш сервер › Анонсы", model.ChatText);
        Assert.Equal("7", Named<TextBox>(window, "NotifyTopic").Text);
        window.Close();
    }

    /// <summary>Свой ID чата, набранный в поле со списком, доходит до модели и уходит в канал как ID.</summary>
    [AvaloniaFact]
    public void Typed_chat_id_is_saved_and_save_closes_window()
    {
        var (window, model) = Open(NewTelegram(), isNew: true);
        model.TelegramToken = Token;
        var chat = Named<ComboBox>(window, "NotifyChat");
        chat.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput("-100777");
        Assert.Equal("-100777", model.ChatText);

        var closed = false;
        window.Closed += (_, _) => closed = true;
        model.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.Equal("-100777", model.Result?.ChatId);
        window.Close();
    }

    /// <summary>Поиск не нашёл чатов: выбранный раньше чат пропадает из поля (его подпись не должна уйти как ID).</summary>
    [AvaloniaFact]
    public async Task No_chats_found_clears_previously_selected_chat()
    {
        var channel = new NotifyChannel { Kind = NotifyKind.Telegram, Name = "Наш сервер", ChatId = "-1001", ChatTitle = "Наш сервер" };
        var (window, model) = Open(channel, isNew: false, _ => Task.FromResult<IReadOnlyList<TelegramChat>>([]));
        model.TelegramToken = Token;

        await model.FindChatsCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var chat = Named<ComboBox>(window, "NotifyChat");
        Assert.Equal("", chat.Text ?? "");
        Assert.Null(chat.SelectedItem);
        Assert.Equal("", model.ChatText);
        Assert.True(Named<Control>(window, "NotifyStatus").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_in_template_adds_a_line_instead_of_saving()
    {
        var (window, model) = Open(new NotifyChannel { Kind = NotifyKind.Webhook, Name = "Хук" }, isNew: true);
        var template = Named<TextBox>(window, "NotifyWebhookTemplate");
        template.Focus();
        template.CaretIndex = template.Text!.Length;
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible); // окно не закрылось и не сохраняло
        Assert.Null(model.Result);
        Assert.EndsWith("\n", model.Template.Replace("\r", ""));
        window.Close();
    }
}
