using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Тип канала в выпадающем списке окна канала оповещений.</summary>
public sealed record NotifyKindItem(NotifyKind Kind, string Title);

/// <summary>Готовый шаблон вебхука в выпадающем списке.</summary>
public sealed record WebhookPresetItem(string Title, string Template);

/// <summary>
/// Окно канала оповещений, как в Uptime Kuma: сверху тип, ниже — его поля. Секреты (токен бота, ссылки вебхуков)
/// вводятся в скрытые поля и не показываются; у сохранённого канала поле пустое — пустое и оставить, чтобы не менять.
/// Тема ntfy видна: её же надо вписать в приложение на телефоне. Ответ окна — <see cref="Result"/> после «Сохранить»
/// (и событие <see cref="Finished"/>). Поиск чатов Telegram и проверочное сообщение идут через делегаты из
/// конструктора: тесты их подменяют и в сеть не ходят.
/// </summary>
public sealed partial class NotifyChannelEditorViewModel : ObservableObject
{
    private readonly NotifyChannel _original;
    private readonly bool _isNew;
    private readonly Func<string, Task<IReadOnlyList<TelegramChat>>> _findChats;
    private readonly Func<NotifyChannel, NotifyMessage, Task> _send;

    /// <param name="channel">Канал, который правим; у нового — тип и имя по умолчанию.</param>
    /// <param name="isNew">Новый канал: тип можно менять, имя подстраивается под тип, пока его не правили.</param>
    /// <param name="findChats">Чаты, где боту писали (по токену); по умолчанию — настоящий Telegram.</param>
    /// <param name="send">Отправить сообщение в канал; по умолчанию — настоящая отправка.</param>
    public NotifyChannelEditorViewModel(NotifyChannel channel, bool isNew,
        Func<string, Task<IReadOnlyList<TelegramChat>>>? findChats = null,
        Func<NotifyChannel, NotifyMessage, Task>? send = null)
    {
        _original = channel;
        _isNew = isNew;
        _findChats = findChats ?? (token => new Notifier(NotifyChannelsViewModel.Http).FindTelegramChatsAsync(token));
        _send = send ?? ((c, m) => new Notifier(NotifyChannelsViewModel.Http).SendAsync(c, m));

        Kinds =
        [
            new NotifyKindItem(NotifyKind.Telegram, "Telegram"),
            new NotifyKindItem(NotifyKind.Discord, "Discord"),
            new NotifyKindItem(NotifyKind.Ntfy, "ntfy"),
            new NotifyKindItem(NotifyKind.Webhook, Loc.T("notify.whKind")),
        ];
        // тип меняется только у нового канала: у сохранённого другие поля и секрет
        IsKindEditable = isNew;
        Presets =
        [
            new WebhookPresetItem(Loc.T("notify.whPresetJson"), WebhookTemplates.Json),
            new WebhookPresetItem("Slack", WebhookTemplates.Slack),
            new WebhookPresetItem("Home Assistant", WebhookTemplates.HomeAssistant),
        ];

        _name = channel.Name;
        // Telegram
        _topicText = channel.TopicId?.ToString() ?? "";
        if (channel.Kind == NotifyKind.Telegram && channel.ChatId is { Length: > 0 } id)
        {
            var chat = new TelegramChat(id, channel.ChatTitle is { Length: > 0 } t ? t : id, channel.TopicId);
            Chats = [chat];
            SelectChat(chat);
        }
        // ntfy: тема — не скрываем, её вписывают в приложение
        _ntfyServer = channel.Kind == NotifyKind.Ntfy ? channel.Url ?? "" : "";
        _ntfyTopic = channel.Kind == NotifyKind.Ntfy ? channel.Secret ?? Notifier.NewNtfyTopic() : Notifier.NewNtfyTopic();
        // вебхук
        _template = channel.Kind == NotifyKind.Webhook && channel.Template is { Length: > 0 } tpl ? tpl : WebhookTemplates.Json;
        // сохранённый секрет — подсказка «оставь пустым»
        TokenHint = Loc.T(channel.HasSecret ? "notify.tgTokenKept" : "notify.tgTokenHint");
        DiscordHint = Loc.T(channel.HasSecret ? "notify.secretKept" : "notify.dcUrlHint");
        WebhookHint = Loc.T(channel.HasSecret ? "notify.secretKept" : "notify.whUrlHint");

        SelectedKind = Kinds.First(k => k.Kind == channel.Kind);
    }

    // ---------- тип и имя

    /// <summary>Типы каналов для выпадающего списка: Telegram, Discord, ntfy, вебхук.</summary>
    public IReadOnlyList<NotifyKindItem> Kinds { get; }

    /// <summary>Тип можно менять только у нового канала.</summary>
    public bool IsKindEditable { get; }

    /// <summary>Выбранный тип: от него зависят видимые поля, пояснение и имя по умолчанию.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind), nameof(IsTelegram), nameof(IsDiscord), nameof(IsNtfy), nameof(IsWebhook), nameof(IntroText))]
    private NotifyKindItem? _selectedKind;

    /// <summary>Тип канала (ничего не выбрано — Telegram, как в окне на WPF).</summary>
    public NotifyKind Kind => CurrentKind.Kind;

    public bool IsTelegram => Kind == NotifyKind.Telegram;
    public bool IsDiscord => Kind == NotifyKind.Discord;
    public bool IsNtfy => Kind == NotifyKind.Ntfy;
    public bool IsWebhook => Kind == NotifyKind.Webhook;

    /// <summary>Пояснение под типом: откуда взять токен или ссылку.</summary>
    public string IntroText => Loc.T(Kind switch
    {
        NotifyKind.Telegram => "notify.tgIntro",
        NotifyKind.Discord => "notify.dcIntro",
        NotifyKind.Ntfy => "notify.ntfyIntro",
        _ => "notify.whIntro",
    });

    /// <summary>Название канала (как ввели; пустое при сохранении становится названием типа).</summary>
    [ObservableProperty] private string _name;

    private NotifyKindItem CurrentKind => SelectedKind ?? Kinds[0];

    partial void OnSelectedKindChanged(NotifyKindItem? value)
    {
        // имя по умолчанию — по типу, пока его не меняли
        if (_isNew && value is not null && ((Name ?? "").Length == 0 || Kinds.Any(k => k.Title == Name)))
            Name = value.Title;
        ClearStatus();
    }

    // ---------- секреты: введённый, а пустое поле — оставить сохранённый

    /// <summary>Telegram: токен бота (скрытое поле).</summary>
    [ObservableProperty] private string _telegramToken = "";

    /// <summary>Discord: ссылка вебхука (скрытое поле).</summary>
    [ObservableProperty] private string _discordUrl = "";

    /// <summary>Свой вебхук: адрес (скрытое поле, в таких ссылках часто ключ).</summary>
    [ObservableProperty] private string _webhookUrl = "";

    /// <summary>Подсказка под токеном: как он выглядит, а у сохранённого канала — «оставь пустым, чтобы не менять».</summary>
    public string TokenHint { get; }
    public string DiscordHint { get; }
    public string WebhookHint { get; }

    partial void OnTelegramTokenChanged(string value) => ClearStatus();
    partial void OnDiscordUrlChanged(string value) => ClearStatus();
    partial void OnWebhookUrlChanged(string value) => ClearStatus();

    /// <summary>Введённый секрет, а если поле пустое — сохранённый (того же типа).</summary>
    private string? Typed(string? field) =>
        Clean(field) is { Length: > 0 } typed ? typed : _original.Kind == Kind ? _original.Secret : null;

    // секрет шифруем заново, только если ввели новый
    private string? Protect(string? field, string secret) =>
        Clean(field).Length > 0 || _original.Kind != Kind ? NotifySecret.Protect(secret) : _original.SecretProtected;

    private static string Clean(string? text) => text?.Trim() ?? "";

    // ---------- Telegram: чат и тема

    /// <summary>Найденные чаты (у сохранённого канала — его чат) для выпадающего списка поля «Чат».</summary>
    [ObservableProperty] private IReadOnlyList<TelegramChat> _chats = [];

    /// <summary>Выбранный из найденных чат; выбрали — тема подставляется его.</summary>
    [ObservableProperty] private TelegramChat? _selectedChat;

    /// <summary>Текст поля «Чат»: подпись выбранного чата или ID, вписанный руками.</summary>
    [ObservableProperty] private string _chatText = "";

    /// <summary>Нашлось несколько чатов — окно раскрывает их список, чтобы выбрали, куда слать.</summary>
    public event Action? ChatListRequested;

    /// <summary>ID темы форума (пусто — общий чат).</summary>
    [ObservableProperty] private string _topicText;

    partial void OnSelectedChatChanged(TelegramChat? value)
    {
        if (value is not null) TopicText = value.TopicId?.ToString() ?? "";
    }

    /// <summary>Выбрать чат из списка: в поле — его подпись (так делает и само поле со списком).</summary>
    private void SelectChat(TelegramChat chat)
    {
        SelectedChat = chat;
        ChatText = chat.Title;
    }

    /// <summary>
    /// Куда слать: выбранный из найденных чат (в поле его подпись) или то, что вписали руками, — тогда это и ID, и подпись.
    /// Подпись найденного чата, набранная целиком, — тот же чат: поле со списком само выбирает его по тексту.
    /// </summary>
    private (string Id, string Title)? Chat
    {
        get
        {
            var text = ChatText ?? "";
            if (SelectedChat is { } selected && text == selected.Title) return (selected.Id, selected.Title);
            if (Chats.FirstOrDefault(c => c.Title == text) is { } listed) return (listed.Id, listed.Title);
            return Clean(text) is { Length: > 0 } typed ? (typed, typed) : null;
        }
    }

    /// <summary>
    /// «Найти чат»: в каких чатах боту писали. Один — выбран сразу; несколько — выбран первый и список раскрыт.
    /// Пока ищем, кнопка выключена.
    /// </summary>
    [RelayCommand]
    private async Task FindChatsAsync()
    {
        if (Typed(TelegramToken) is not { } token)
        {
            Show(Loc.T("notify.tgNeedToken"), error: true);
            return;
        }
        try
        {
            var chats = await _findChats(token);
            var old = SelectedChat;
            Chats = chats;
            if (chats.Count == 0)
            {
                // выбранного раньше чата в списке больше нет — поле очищается, как в окне на WPF (иначе подпись чата
                // ушла бы в сохранение как ID); вписанный руками ID остаётся
                if (old is not null)
                {
                    SelectedChat = null;
                    ChatText = "";
                }
                Show(Loc.T("notify.tgNoChats"), error: true);
                return;
            }
            SelectChat(chats[0]);
            Show(chats.Count == 1 ? Loc.T("notify.tgFound", chats[0].Title) : Loc.T("notify.tgFoundMany", chats.Count), error: false);
            if (chats.Count > 1) ChatListRequested?.Invoke();
        }
        catch (NotifyException ex)
        {
            Show(ex.Message, error: true);
        }
    }

    // ---------- ntfy и вебхук

    /// <summary>ntfy: тема (видна — её вписывают в приложение на телефоне).</summary>
    [ObservableProperty] private string _ntfyTopic;

    /// <summary>ntfy: свой сервер (пусто — ntfy.sh).</summary>
    [ObservableProperty] private string _ntfyServer;

    /// <summary>«Новая тема»: длинная случайная.</summary>
    [RelayCommand]
    private void NewTopic() => NtfyTopic = Notifier.NewNtfyTopic();

    /// <summary>Готовые шаблоны тела вебхука.</summary>
    public IReadOnlyList<WebhookPresetItem> Presets { get; }

    /// <summary>Выбранный шаблон: выбрали — тело запроса заменяется им.</summary>
    [ObservableProperty] private WebhookPresetItem? _selectedPreset;

    /// <summary>Тело запроса вебхука с подстановками.</summary>
    [ObservableProperty] private string _template;

    partial void OnSelectedPresetChanged(WebhookPresetItem? value)
    {
        if (value is not null) Template = value.Template;
    }

    // ---------- состояние

    /// <summary>Строка состояния под полями: что не так, нашлись ли чаты, ушло ли проверочное. Пусто — не видна.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Состояние — ошибка (красным), иначе песочным.</summary>
    [ObservableProperty] private bool _statusIsError;

    private void Show(string text, bool error)
    {
        StatusText = text;
        StatusIsError = error;
    }

    private void ClearStatus() => Show("", error: false);

    // ---------- проверка и сохранение

    /// <summary>«Отправить проверочное»: собрать канал из полей и отправить в него проверочное сообщение.</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        if (Build() is not { } channel) return;
        Show(Loc.T("notify.testing", channel.Name), error: false);
        try
        {
            await _send(channel, NotifyChannelsViewModel.TestMessage());
            Show(Loc.T("notify.testSent", channel.Name), error: false);
        }
        catch (NotifyException ex)
        {
            Show(ex.Message, error: true);
        }
    }

    /// <summary>Канал после «Сохранить»; null — ещё не сохранили.</summary>
    public NotifyChannel? Result { get; private set; }

    /// <summary>Сохранили — окно закрывается с этим каналом.</summary>
    public event Action<NotifyChannel>? Finished;

    /// <summary>«Сохранить»: чего-то не хватает — сказать что; иначе ответ окна.</summary>
    [RelayCommand]
    private void Save()
    {
        if (Build() is not { } channel) return;
        Result = channel;
        Finished?.Invoke(channel);
    }

    /// <summary>Канал из полей выбранного типа; чего-то не хватает — сказать что и вернуть null.</summary>
    private NotifyChannel? Build()
    {
        var name = Clean(Name) is { Length: > 0 } n ? n : CurrentKind.Title;
        var baseChannel = _original with { Kind = Kind, Name = name, ChatId = null, ChatTitle = null, TopicId = null, Url = null, Template = null };
        switch (Kind)
        {
            case NotifyKind.Telegram:
            {
                if (Typed(TelegramToken) is not { } token) return Fail(Loc.T("notify.tgNeedToken"));
                if (Chat is not { } chat) return Fail(Loc.T("notify.tgNoChat"));
                long? topic = null;
                if (Clean(TopicText) is { Length: > 0 } topicText)
                {
                    if (!long.TryParse(topicText, out var t) || t <= 0) return Fail(Loc.T("notify.tgBadTopic"));
                    topic = t;
                }
                return baseChannel with
                {
                    SecretProtected = Protect(TelegramToken, token), ChatId = chat.Id, ChatTitle = chat.Title, TopicId = topic,
                };
            }
            case NotifyKind.Discord:
            {
                if (Typed(DiscordUrl) is not { } url) return Fail(Loc.T("notify.dcNeedUrl"));
                if (!Notifier.IsDiscordUrl(url)) return Fail(Loc.T("notify.dcBadUrl"));
                return baseChannel with { SecretProtected = Protect(DiscordUrl, url) };
            }
            case NotifyKind.Ntfy:
            {
                var topic = Clean(NtfyTopic);
                if (!Notifier.IsNtfyTopic(topic)) return Fail(Loc.T("notify.ntfyBadTopic"));
                var server = Clean(NtfyServer);
                if (server.Length > 0 && (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
                    return Fail(Loc.T("notify.ntfyBadServer"));
                return baseChannel with
                {
                    SecretProtected = NotifySecret.Protect(topic), Url = server.Length > 0 ? server : null, ChatTitle = topic,
                };
            }
            default:
            {
                if (Typed(WebhookUrl) is not { } url) return Fail(Loc.T("notify.whNeedUrl"));
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return Fail(Loc.T("notify.whBadUrl"));
                var template = Template ?? "";
                try { Notifier.WebhookBody(template, NotifyChannelsViewModel.TestMessage(), DateTime.Now); }
                catch (NotifyException ex) { return Fail(ex.Message); }
                return baseChannel with { SecretProtected = Protect(WebhookUrl, url), Template = template, ChatTitle = uri.Host };
            }
        }
    }

    private NotifyChannel? Fail(string text)
    {
        Show(text, error: true);
        return null;
    }
}
