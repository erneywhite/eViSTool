using System.Net.Http;
using eViSTool.Core.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace eViSTool.Core.Notifications;

/// <summary>О чём сервер может оповещать.</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum NotifyEvent
{
    ServerCrashed,
    StartFailed,
    BackupFailed,
    ModsUpdated,
    ServerStarted,
    ServerStopped,
    RestartSoon,
    PlayerJoined,
    PlayerLeft,
}

/// <summary>
/// Оповещения одного сервера (вкладка «Оповещения»): какое событие в какие каналы. Лежит рядом с настройками расписания,
/// читает его агент — он и отправляет, окно можно закрыть. Каналы — копии из «Настроек» окна, только те, что включены
/// у этого сервера; их секреты зашифрованы на той машине, где лежит файл (у сервера в виртуалке — там).
/// </summary>
public sealed record ServerNotifySettings
{
    /// <summary>Имя сервера для заголовков: «Survival: сервер упал».</summary>
    public string? ServerName { get; init; }

    public List<NotifyChannel> Channels { get; init; } = [];

    /// <summary>Событие → ID каналов.</summary>
    public Dictionary<NotifyEvent, List<string>> Routes { get; init; } = [];

    public IEnumerable<NotifyChannel> ChannelsFor(NotifyEvent e) =>
        Routes.TryGetValue(e, out var ids) ? Channels.Where(c => ids.Contains(c.Id)) : [];

    public bool IsOn(NotifyEvent e, string channelId) => Routes.TryGetValue(e, out var ids) && ids.Contains(channelId);

    public static string FileFor(string profileId, string? agentsDir = null) =>
        Path.Combine(agentsDir ?? AgentProtocol.DefaultAgentsDir, $"{profileId}.notify.json");

    public static ServerNotifySettings Load(string profileId, string? agentsDir = null)
    {
        try
        {
            var file = FileFor(profileId, agentsDir);
            return File.Exists(file) ? JsonConvert.DeserializeObject<ServerNotifySettings>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string profileId, string? agentsDir = null)
    {
        var file = FileFor(profileId, agentsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
        File.Move(tmp, file, overwrite: true);
    }

    public static DateTime? ChangedAt(string profileId, string? agentsDir = null) =>
        File.Exists(FileFor(profileId, agentsDir)) ? File.GetLastWriteTimeUtc(FileFor(profileId, agentsDir)) : null;

    /// <summary>Без секретов — так настройки отдаются окну на другой машине.</summary>
    public ServerNotifySettings WithoutSecrets() => this with { Channels = [.. Channels.Select(c => c with { SecretProtected = null })] };

    /// <summary>
    /// Для агента на другой машине: секреты расшифрованы здесь и поедут по защищённому соединению, там их зашифруют
    /// заново (<see cref="ServerNotifyUpload.ToSettings"/>).
    /// </summary>
    public ServerNotifyUpload ToUpload() => new()
    {
        ServerName = ServerName,
        Routes = Routes,
        Channels = [.. Channels.Select(c => new NotifyChannelUpload(c with { SecretProtected = null }, c.Secret))],
    };
}

/// <summary>Канал для агента на другой машине: секрет открытым текстом (только внутри TLS-соединения агента).</summary>
public sealed record NotifyChannelUpload(NotifyChannel Channel, string? Secret);

/// <summary>Настройки оповещений сервера по дороге к агенту.</summary>
public sealed record ServerNotifyUpload
{
    public string? ServerName { get; init; }
    public Dictionary<NotifyEvent, List<string>> Routes { get; init; } = [];
    public List<NotifyChannelUpload> Channels { get; init; } = [];

    /// <summary>На машине агента: секреты шифруются для её пользователя.</summary>
    public ServerNotifySettings ToSettings() => new()
    {
        ServerName = ServerName,
        Routes = Routes,
        Channels = [.. Channels.Select(c => c.Channel with { SecretProtected = c.Secret is { Length: > 0 } s ? NotifySecret.Protect(s) : null })],
    };
}

/// <summary>
/// Отправка оповещений сервера: по настройкам (читаются при каждом событии — правки из окна действуют сразу), в фоне,
/// ошибки — в <paramref name="report"/> (агент пишет их в консоль сервера).
/// </summary>
public sealed class ServerNotifier(Func<ServerNotifySettings> settings, HttpClient http, Action<string, string> report)
{
    /// <summary>Цвет события: проблемы — красные, «скоро перезапуск» — жёлтый, моды — синие, запуск — зелёный.</summary>
    public static NotifySeverity SeverityOf(NotifyEvent e) => e switch
    {
        NotifyEvent.ServerCrashed or NotifyEvent.StartFailed or NotifyEvent.BackupFailed => NotifySeverity.Problem,
        NotifyEvent.RestartSoon => NotifySeverity.Warning,
        NotifyEvent.ModsUpdated => NotifySeverity.Info,
        NotifyEvent.ServerStarted => NotifySeverity.Good,
        _ => NotifySeverity.Neutral,
    };

    /// <summary>Сообщить о событии; ничего не ждёт.</summary>
    public void Notify(NotifyEvent e, string title, params NotifyLine[] details) =>
        _ = SendAsync(settings().ChannelsFor(e).ToList(), SeverityOf(e), title, details);

    /// <summary>Проверочное во все каналы, включённые хоть для одного события.</summary>
    public Task<IReadOnlyList<string>> TestAsync(string title, params NotifyLine[] details)
    {
        var s = settings();
        var ids = s.Routes.Values.SelectMany(v => v).ToHashSet();
        return SendAsync([.. s.Channels.Where(c => ids.Contains(c.Id))], NotifySeverity.Info, title, details);
    }

    /// <summary>Возвращает ошибки по каналам («Telegram: …»); пусто — всё ушло.</summary>
    private async Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<NotifyChannel> channels, NotifySeverity severity, string title,
        IReadOnlyList<NotifyLine> details)
    {
        var message = new NotifyMessage(severity, settings().ServerName, title, details);
        var errors = new List<string>();
        foreach (var channel in channels)
        {
            try { await new Notifier(http).SendAsync(channel, message).ConfigureAwait(false); }
            catch (NotifyException ex)
            {
                errors.Add($"{channel.Name}: {ex.Message}");
                report(channel.Name, ex.Message);
            }
        }
        return errors;
    }
}
