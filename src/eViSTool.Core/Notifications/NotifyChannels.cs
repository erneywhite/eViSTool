using eViSTool.Core.Platform;
using Newtonsoft.Json;

namespace eViSTool.Core.Notifications;

/// <summary>Куда слать оповещения.</summary>
public enum NotifyKind { Telegram, Discord, Ntfy, Webhook }

/// <summary>
/// Канал оповещений (как в Uptime Kuma: настраивается один раз, потом включается у серверов). Секрет — токен бота,
/// ссылка вебхука, тема ntfy — хранится зашифрованным (<see cref="NotifySecret"/>) и на экран не выводится.
/// </summary>
public sealed record NotifyChannel
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public NotifyKind Kind { get; init; }

    /// <summary>Зашифрованный секрет: Telegram — токен бота, Discord и вебхук — адрес, ntfy — тема.</summary>
    public string? SecretProtected { get; init; }

    /// <summary>Telegram: ID чата (не секрет — без токена бота им не воспользоваться).</summary>
    public string? ChatId { get; init; }

    /// <summary>Подпись чата для людей: «@erney», «Наш сервер», «Наш сервер › Анонсы» — чтобы было видно, куда уходит.</summary>
    public string? ChatTitle { get; init; }

    /// <summary>Telegram: тема форума (супергруппа с темами); null — общий чат.</summary>
    public long? TopicId { get; init; }

    /// <summary>ntfy: свой сервер (пусто — ntfy.sh).</summary>
    public string? Url { get; init; }

    /// <summary>Вебхук: шаблон тела запроса с подстановками {server}, {title}, {details}, {text}, {severity}, {color}, {time}.</summary>
    public string? Template { get; init; }

    [JsonIgnore] public string? Secret => NotifySecret.Unprotect(SecretProtected);
    [JsonIgnore] public bool HasSecret => !string.IsNullOrEmpty(SecretProtected);
}

/// <summary>Насколько это важно — цвет оповещения: кружок в Telegram, полоса в Discord, приоритет в ntfy.</summary>
public enum NotifySeverity { Problem, Warning, Info, Good, Neutral }

/// <summary>
/// Строка подробностей: одноцветный символ из шрифта (⚑ ↻ ◷ ◦ ▣ — не эмодзи: Telegram их не подменяет), подпись
/// («Мод», «Сторож») и текст. Символ и подпись могут быть пустыми — так пишутся пункты списка.
/// </summary>
public sealed record NotifyLine(string Icon, string? Label, string Text)
{
    public NotifyLine(string text) : this("", null, text) { }
}

/// <summary>
/// Оповещение: цвет, сервер (если есть), что случилось и строки подробностей. Каждый канал показывает их по-своему;
/// в Telegram — «🔴 Survival» / «Сервер упал» жирным / подробности со значками.
/// </summary>
public sealed record NotifyMessage(NotifySeverity Severity, string? Server, string Title, IReadOnlyList<NotifyLine> Details)
{
    public string Dot => Severity switch
    {
        NotifySeverity.Problem => "🔴",
        NotifySeverity.Warning => "🟡",
        NotifySeverity.Info => "🔵",
        NotifySeverity.Good => "🟢",
        _ => "⚪",
    };

    /// <summary>Простым текстом — для каналов без разметки.</summary>
    public string ToPlainText() => string.Join("\n",
        new[] { string.IsNullOrWhiteSpace(Server) ? null : $"{Dot} {Server}", string.IsNullOrWhiteSpace(Server) ? $"{Dot} {Title}" : Title }
            .Where(s => s is not null)
            .Concat(Details.Select(d => string.Join(" ", new[] { d.Icon, d.Label is { Length: > 0 } l ? l + ":" : "", d.Text }
                .Where(s => s.Length > 0)))));
}

/// <summary>Каналы оповещений этого компьютера — файл в папке данных программы.</summary>
public static class NotifyChannels
{
    public static string FileIn(string root) => Path.Combine(root, "notify-channels.json");

    public static List<NotifyChannel> Load(string root)
    {
        try
        {
            var file = FileIn(root);
            return File.Exists(file) ? JsonConvert.DeserializeObject<List<NotifyChannel>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(string root, IEnumerable<NotifyChannel> channels)
    {
        var file = FileIn(root);
        Directory.CreateDirectory(root);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(channels.ToList(), Formatting.Indented));
        File.Move(tmp, file, overwrite: true);
    }
}

/// <summary>
/// Секреты оповещений зашифрованы для текущего пользователя (<see cref="SecretProtector"/>: DPAPI на Windows, ключ
/// пользователя на Linux): скопированная или утёкшая папка данных их не раскроет. На другом компьютере (например, у
/// агента сервера в виртуалке) секрет шифруется заново там.
/// </summary>
public static class NotifySecret
{
    private static readonly byte[] Entropy = "eViSTool notification secret"u8.ToArray();

    public static string Protect(string secret) => SecretProtector.Protect(secret, Entropy);

    /// <summary>null — не расшифровать (другой пользователь или компьютер) или пусто.</summary>
    public static string? Unprotect(string? stored) => SecretProtector.Unprotect(stored, Entropy);
}
