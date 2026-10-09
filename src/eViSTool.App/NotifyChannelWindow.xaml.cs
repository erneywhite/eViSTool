using System.Windows;
using System.Windows.Controls;
using eViSTool.App.ViewModels;
using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;

namespace eViSTool.App;

/// <summary>Тип канала в выпадающем списке окна.</summary>
public sealed record NotifyKindItem(NotifyKind Kind, string Title);

/// <summary>Готовый шаблон вебхука в выпадающем списке.</summary>
public sealed record WebhookPresetItem(string Title, string Template);

/// <summary>
/// Канал оповещений, как в Uptime Kuma: сверху тип, ниже — его поля. Секреты (токен бота, ссылки вебхуков) вводятся
/// в поля пароля и не показываются; у сохранённого канала поле пустое — пустое и оставить, чтобы не менять. Тема ntfy
/// видна: её же надо вписать в приложение на телефоне.
/// </summary>
public partial class NotifyChannelWindow : Window
{
    private readonly NotifyChannel _original;
    private readonly bool _isNew;

    /// <summary>Канал после «Сохранить».</summary>
    public NotifyChannel? Result { get; private set; }

    public NotifyChannelWindow(NotifyChannel channel, bool isNew)
    {
        _original = channel;
        _isNew = isNew;
        InitializeComponent();

        KindBox.ItemsSource = new[]
        {
            new NotifyKindItem(NotifyKind.Telegram, "Telegram"),
            new NotifyKindItem(NotifyKind.Discord, "Discord"),
            new NotifyKindItem(NotifyKind.Ntfy, "ntfy"),
            new NotifyKindItem(NotifyKind.Webhook, Loc.T("notify.whKind")),
        };
        // тип меняется только у нового канала: у сохранённого другие поля и секрет
        KindBox.IsEnabled = isNew;
        PresetBox.ItemsSource = new[]
        {
            new WebhookPresetItem(Loc.T("notify.whPresetJson"), WebhookTemplates.Json),
            new WebhookPresetItem("Slack", WebhookTemplates.Slack),
            new WebhookPresetItem("Home Assistant", WebhookTemplates.HomeAssistant),
        };

        NameBox.Text = channel.Name;
        // Telegram
        TopicBox.Text = channel.TopicId?.ToString() ?? "";
        if (channel.Kind == NotifyKind.Telegram && channel.ChatId is { Length: > 0 } id)
        {
            var chat = new TelegramChat(id, channel.ChatTitle is { Length: > 0 } t ? t : id, channel.TopicId);
            ChatBox.ItemsSource = new[] { chat };
            ChatBox.SelectedItem = chat;
        }
        // ntfy: тема — не скрываем, её вписывают в приложение
        NtfyServerBox.Text = channel.Kind == NotifyKind.Ntfy ? channel.Url ?? "" : "";
        NtfyTopicBox.Text = channel.Kind == NotifyKind.Ntfy ? channel.Secret ?? Notifier.NewNtfyTopic() : Notifier.NewNtfyTopic();
        // вебхук
        TemplateBox.Text = channel.Kind == NotifyKind.Webhook && channel.Template is { Length: > 0 } tpl ? tpl : WebhookTemplates.Json;
        // сохранённый секрет — подсказка «оставь пустым»
        if (channel.HasSecret)
        {
            TokenHint.Text = Loc.T("notify.tgTokenKept");
            DiscordHint.Text = Loc.T("notify.secretKept");
            WebhookHint.Text = Loc.T("notify.secretKept");
        }

        KindBox.SelectedItem = ((NotifyKindItem[])KindBox.ItemsSource).First(k => k.Kind == channel.Kind);
        Loaded += (_, _) => NameBox.Focus();
    }

    private NotifyKind Kind => (KindBox.SelectedItem as NotifyKindItem)?.Kind ?? NotifyKind.Telegram;

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TelegramPanel.Visibility = Kind == NotifyKind.Telegram ? Visibility.Visible : Visibility.Collapsed;
        DiscordPanel.Visibility = Kind == NotifyKind.Discord ? Visibility.Visible : Visibility.Collapsed;
        NtfyPanel.Visibility = Kind == NotifyKind.Ntfy ? Visibility.Visible : Visibility.Collapsed;
        WebhookPanel.Visibility = Kind == NotifyKind.Webhook ? Visibility.Visible : Visibility.Collapsed;
        IntroText.Text = Kind switch
        {
            NotifyKind.Telegram => Loc.T("notify.tgIntro"),
            NotifyKind.Discord => Loc.T("notify.dcIntro"),
            NotifyKind.Ntfy => Loc.T("notify.ntfyIntro"),
            _ => Loc.T("notify.whIntro"),
        };
        // имя по умолчанию — по типу, пока его не меняли
        if (_isNew && (NameBox.Text.Length == 0 || ((NotifyKindItem[])KindBox.ItemsSource).Any(k => k.Title == NameBox.Text)))
            NameBox.Text = ((NotifyKindItem)KindBox.SelectedItem).Title;
        Show("", error: false);
    }

    private void Secret_Changed(object sender, RoutedEventArgs e) => Show("", error: false);

    private void NewTopic_Click(object sender, RoutedEventArgs e) => NtfyTopicBox.Text = Notifier.NewNtfyTopic();

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedItem is WebhookPresetItem preset) TemplateBox.Text = preset.Template;
    }

    // ---------- Telegram

    /// <summary>Введённый секрет, а если поле пустое — сохранённый (того же типа).</summary>
    private string? Typed(PasswordBox box) =>
        box.Password.Trim() is { Length: > 0 } typed ? typed : _original.Kind == Kind ? _original.Secret : null;

    private void ChatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChatBox.SelectedItem is TelegramChat c) TopicBox.Text = c.TopicId?.ToString() ?? "";
    }

    private (string Id, string Title)? Chat =>
        ChatBox.SelectedItem is TelegramChat c && ChatBox.Text == c.Title ? (c.Id, c.Title)
        : ChatBox.Text.Trim() is { Length: > 0 } typed ? (typed, typed)
        : null;

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (Typed(TokenBox) is not { } token)
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

    // ---------- проверка и сохранение

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

    /// <summary>Канал из полей выбранного типа; чего-то не хватает — сказать что и вернуть null.</summary>
    private NotifyChannel? Build()
    {
        var name = NameBox.Text.Trim() is { Length: > 0 } n ? n : ((NotifyKindItem)KindBox.SelectedItem).Title;
        var baseChannel = _original with { Kind = Kind, Name = name, ChatId = null, ChatTitle = null, TopicId = null, Url = null, Template = null };
        switch (Kind)
        {
            case NotifyKind.Telegram:
            {
                if (Typed(TokenBox) is not { } token) return Fail(Loc.T("notify.tgNeedToken"));
                if (Chat is not { } chat) return Fail(Loc.T("notify.tgNoChat"));
                long? topic = null;
                if (TopicBox.Text.Trim() is { Length: > 0 } topicText)
                {
                    if (!long.TryParse(topicText, out var t) || t <= 0) return Fail(Loc.T("notify.tgBadTopic"));
                    topic = t;
                }
                return baseChannel with
                {
                    SecretProtected = Protect(TokenBox, token), ChatId = chat.Id, ChatTitle = chat.Title, TopicId = topic,
                };
            }
            case NotifyKind.Discord:
            {
                if (Typed(DiscordBox) is not { } url) return Fail(Loc.T("notify.dcNeedUrl"));
                if (!Notifier.IsDiscordUrl(url)) return Fail(Loc.T("notify.dcBadUrl"));
                return baseChannel with { SecretProtected = Protect(DiscordBox, url) };
            }
            case NotifyKind.Ntfy:
            {
                var topic = NtfyTopicBox.Text.Trim();
                if (!Notifier.IsNtfyTopic(topic)) return Fail(Loc.T("notify.ntfyBadTopic"));
                var server = NtfyServerBox.Text.Trim();
                if (server.Length > 0 && (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
                    return Fail(Loc.T("notify.ntfyBadServer"));
                return baseChannel with
                {
                    SecretProtected = NotifySecret.Protect(topic), Url = server.Length > 0 ? server : null, ChatTitle = topic,
                };
            }
            default:
            {
                if (Typed(WebhookBox) is not { } url) return Fail(Loc.T("notify.whNeedUrl"));
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return Fail(Loc.T("notify.whBadUrl"));
                try { Notifier.WebhookBody(TemplateBox.Text, NotifyChannelsViewModel.TestMessage(), DateTime.Now); }
                catch (NotifyException ex) { return Fail(ex.Message); }
                return baseChannel with { SecretProtected = Protect(WebhookBox, url), Template = TemplateBox.Text, ChatTitle = uri.Host };
            }
        }
    }

    // секрет шифруем заново, только если ввели новый
    private string? Protect(PasswordBox box, string secret) =>
        box.Password.Trim().Length > 0 || _original.Kind != Kind ? NotifySecret.Protect(secret) : _original.SecretProtected;

    private NotifyChannel? Fail(string text)
    {
        Show(text, error: true);
        return null;
    }

    private void Show(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Forest.Danger" : "Forest.Accent");
        StatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
