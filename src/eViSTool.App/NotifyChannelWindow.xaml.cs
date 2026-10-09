using System.Windows;
using eViSTool.App.ViewModels;
using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;

namespace eViSTool.App;

/// <summary>
/// Канал Telegram: имя, токен бота, чат. Токен вводится в поле пароля и не показывается; у сохранённого канала поле
/// пустое — пустое и оставить, чтобы не менять. Чат находится сам: человек пишет боту — «Найти чат» его подставляет.
/// </summary>
public partial class NotifyChannelWindow : Window
{
    private readonly NotifyChannel _original;

    /// <summary>Канал после «Сохранить».</summary>
    public NotifyChannel? Result { get; private set; }

    public NotifyChannelWindow(NotifyChannel channel)
    {
        _original = channel;
        InitializeComponent();
        NameBox.Text = channel.Name;
        if (channel.HasSecret) TokenHint.Text = Loc.T("notify.tgTokenKept");
        if (channel.ChatId is { Length: > 0 } id)
        {
            var chat = new TelegramChat(id, channel.ChatTitle is { Length: > 0 } t ? t : id);
            ChatBox.ItemsSource = new[] { chat };
            ChatBox.SelectedItem = chat;
        }
        Loaded += (_, _) => (channel.HasSecret ? (UIElement)NameBox : TokenBox).Focus();
    }

    /// <summary>Токен из поля, а если поле пустое — сохранённый.</summary>
    private string? Token => TokenBox.Password.Trim() is { Length: > 0 } typed ? typed : _original.Secret;

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e) => Show("", error: false);

    /// <summary>Выбранный в списке чат или ID, вписанный руками.</summary>
    private (string Id, string Title)? Chat =>
        ChatBox.SelectedItem is TelegramChat c && ChatBox.Text == c.Title ? (c.Id, c.Title)
        : ChatBox.Text.Trim() is { Length: > 0 } typed ? (typed, typed)
        : null;

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (Token is not { } token)
        {
            Show(Loc.T("notify.tgNeedToken"), error: true);
            return;
        }
        FindButton.IsEnabled = false;
        try
        {
            var chats = await new Notifier(NotifyChannelsViewModel.Http).FindTelegramChatsAsync(token);
            ChatBox.ItemsSource = chats;
            if (chats.Count == 0)
            {
                Show(Loc.T("notify.tgNoChats"), error: true);
                return;
            }
            ChatBox.SelectedItem = chats[0];
            Show(chats.Count == 1 ? Loc.T("notify.tgFound", chats[0].Title) : Loc.T("notify.tgFoundMany", chats.Count), error: false);
            if (chats.Count > 1) ChatBox.IsDropDownOpen = true;
        }
        catch (NotifyException ex)
        {
            Show(ex.Message, error: true);
        }
        finally { FindButton.IsEnabled = true; }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Build() is not { } channel) return;
        TestButton.IsEnabled = false;
        Show(Loc.T("notify.testing", channel.Name), error: false);
        try
        {
            await new Notifier(NotifyChannelsViewModel.Http).SendAsync(channel, NotifyChannelsViewModel.TestMessage());
            Show(Loc.T("notify.testSent", channel.Name), error: false);
        }
        catch (NotifyException ex)
        {
            Show(ex.Message, error: true);
        }
        finally { TestButton.IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Build() is not { } channel) return;
        Result = channel;
        DialogResult = true;
    }

    /// <summary>Канал из полей; чего-то не хватает — сказать что и вернуть null.</summary>
    private NotifyChannel? Build()
    {
        if (Token is not { } token)
        {
            Show(Loc.T("notify.tgNeedToken"), error: true);
            return null;
        }
        if (Chat is not { } chat)
        {
            Show(Loc.T("notify.tgNoChat"), error: true);
            return null;
        }
        return _original with
        {
            Name = NameBox.Text.Trim() is { Length: > 0 } name ? name : "Telegram",
            // секрет шифруем заново, только если ввели новый
            SecretProtected = TokenBox.Password.Trim().Length > 0 ? NotifySecret.Protect(token) : _original.SecretProtected,
            ChatId = chat.Id,
            ChatTitle = chat.Title,
        };
    }

    private void Show(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Forest.Danger" : "Forest.Accent");
        StatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
