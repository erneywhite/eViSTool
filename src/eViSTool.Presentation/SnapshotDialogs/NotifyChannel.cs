using eViSTool.Core.Notifications;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно канала оповещений: новый канал (dialog.notifychannel — как его открывает «Добавить канал» в настройках),
/// сохранённые Discord, вебхук, ntfy и Telegram (dialog.notifychannel.discord, .webhook, .ntfy, .telegram). Окно на снимке только
/// открывается: ничего не ищет, не отправляет и не сохраняет. Секрет сохранённых каналов — не шифровка настоящего
/// токена, а выдуманная строка: окну на открытии нужно только знать, что секрет есть (подсказка «оставь поле пустым»).
/// </summary>
internal static class NotifyChannelDialog
{
    private const string MadeUpSecret = "made-up-secret-for-snapshot";

    /// <summary>Новый канал: Telegram, поля пустые, тип можно менять.</summary>
    [SnapshotDialog("notifychannel")]
    private static void New(MainViewModel main) =>
        AppWindows.Current.EditNotifyChannel(new NotifyChannel { Kind = NotifyKind.Telegram, Name = nameof(NotifyKind.Telegram) }, isNew: true);

    /// <summary>Сохранённый Discord: ссылка вебхука сохранена, поле пустое, тип не меняется.</summary>
    [SnapshotDialog("notifychannel.discord")]
    private static void Discord(MainViewModel main) =>
        AppWindows.Current.EditNotifyChannel(new NotifyChannel
        {
            Kind = NotifyKind.Discord, Name = "Discord сервера", SecretProtected = MadeUpSecret,
        }, isNew: false);

    /// <summary>Сохранённый вебхук в Home Assistant: его шаблон тела запроса.</summary>
    [SnapshotDialog("notifychannel.webhook")]
    private static void Webhook(MainViewModel main) =>
        AppWindows.Current.EditNotifyChannel(new NotifyChannel
        {
            Kind = NotifyKind.Webhook, Name = "Умный дом", SecretProtected = MadeUpSecret, ChatTitle = "ha.example.org",
            Template = WebhookTemplates.HomeAssistant,
        }, isNew: false);

    /// <summary>
    /// Сохранённый ntfy со своим сервером. Тема на снимке — новая случайная (выдуманный секрет не расшифровать), поэтому она разная: сверять раскладку, а не текст темы.
    /// </summary>
    [SnapshotDialog("notifychannel.ntfy")]
    private static void Ntfy(MainViewModel main) =>
        AppWindows.Current.EditNotifyChannel(new NotifyChannel
        {
            Kind = NotifyKind.Ntfy, Name = "Телефон", SecretProtected = MadeUpSecret, Url = "https://ntfy.example.org",
        }, isNew: false);

    /// <summary>Сохранённый Telegram: чат группы с темой (подпись в поле чата, ID темы ниже).</summary>
    [SnapshotDialog("notifychannel.telegram")]
    private static void Telegram(MainViewModel main) =>
        AppWindows.Current.EditNotifyChannel(new NotifyChannel
        {
            Kind = NotifyKind.Telegram, Name = "Наш сервер", SecretProtected = MadeUpSecret, ChatId = "-1001234567890",
            ChatTitle = "Наш сервер › Анонсы", TopicId = 42,
        }, isNew: false);
}
