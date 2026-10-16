using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;
using eViSTool.Core.Server;

/// <summary>
/// Оповещения (вкладка «Оповещения») и чат игры в Discord. Настройки читаются при каждом событии, отправка — в фоне,
/// сбой — строкой в консоль сервера.
/// </summary>
internal sealed class AgentNotifications : IDisposable
{
    private readonly AgentOptions _opts;
    private readonly ServerHost _host;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ServerNotifier _notifier;
    private readonly ChatRelay _chatRelay;

    public AgentNotifications(AgentOptions opts, ServerHost host)
    {
        _opts = opts;
        _host = host;
        _notifier = new ServerNotifier(Settings, _http,
            (channel, error) => _host.Console.Add(ConsoleLineKind.System, Loc.T("notify.sendFailed", channel, error)));
        // чат игры → Discord: общий чат игроков, по желанию — входы и выходы; настройки читаются на каждое сообщение
        _chatRelay = new ChatRelay(
            () => ServerNotifySettings.Load(_opts.ProfileId, _opts.AgentsDir).ChatChannel, _http,
            error => _host.Console.Add(ConsoleLineKind.System, Loc.T("chat.consoleFailed", error)));
    }

    /// <summary>Строка оповещения: значок, подпись (ключ словаря) и текст.</summary>
    public static NotifyLine Line(string icon, string labelKey, string text) => new(icon, Loc.T(labelKey), text);

    // имя сервера в заголовках — то, что задало окно; не задало — без окна из agent.json или serverconfig.json
    private ServerNotifySettings Settings()
    {
        var s = ServerNotifySettings.Load(_opts.ProfileId, _opts.AgentsDir);
        return string.IsNullOrWhiteSpace(s.ServerName) && _opts.ServerName is { } name ? s with { ServerName = name } : s;
    }

    public void Notify(NotifyEvent e, string title, params NotifyLine[] details) => _notifier.Notify(e, title, details);

    /// <summary>Проверочное оповещение по кнопке окна — во все каналы, включённые хоть для одного события.</summary>
    public Task<IReadOnlyList<string>> TestAsync(string title, params NotifyLine[] details) => _notifier.TestAsync(title, details);

    /// <summary>Запуск и остановка сервера. Сам остановился — это падение, о нём скажет разбор вылета.</summary>
    public void OnServerState(ServerState state)
    {
        switch (state)
        {
            case ServerState.Running:
                Notify(NotifyEvent.ServerStarted, Loc.T("notify.ev.started"));
                break;
            case ServerState.Stopped when !_host.LastExitOnItsOwn:
                Notify(NotifyEvent.ServerStopped, Loc.T("notify.ev.stopped"));
                break;
        }
    }

    /// <summary>Строка консоли: сообщение общего чата игроков — в канал чата, если он задан.</summary>
    public void RelayChat(ConsoleLine line)
    {
        if (line.Kind != ConsoleLineKind.Output || ChatBridge.Parse(line.Text) is not { } post) return;
        if (ServerNotifySettings.Load(_opts.ProfileId, _opts.AgentsDir).ChatChannel is not null) _chatRelay.Post(post);
    }

    /// <summary>Вход или выход игрока — в канал чата, если там включены входы и выходы.</summary>
    public void ChatJoinLeave(string text)
    {
        var s = Settings();
        if (s.ChatChannel is not null && s.ChatJoins)
            _chatRelay.Post(new ChatPost(string.IsNullOrWhiteSpace(s.ServerName) ? "Vintage Story" : s.ServerName, text));
    }

    public void Dispose() => _http.Dispose();
}
