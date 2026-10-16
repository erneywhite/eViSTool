using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;
using eViSTool.Core.Server;

/// <summary>
/// Кто зашёл и вышел — по разнице составов: в статистику, в чат Discord и в оповещения. Сервер останавливается —
/// это не «все вышли»: пока он не работает, разница только запоминается.
/// </summary>
internal sealed class PlayerEvents(AgentState state, AgentStats stats, AgentNotifications notify)
{
    private HashSet<string> _known = new(StringComparer.Ordinal);

    public void OnPlayersChanged()
    {
        var now = state.Players.Players.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (state.Host.State == ServerState.Running)
        {
            foreach (var name in now.Except(_known))
                stats.Stat(new StatsEntry(DateTime.Now, StatsKind.Join, Name: name));
            foreach (var name in _known.Except(now))
                stats.Stat(new StatsEntry(DateTime.Now, StatsKind.Leave, Name: name));
            foreach (var name in now.Except(_known))
                notify.ChatJoinLeave(Loc.T("chat.joined", name, now.Count));
            foreach (var name in _known.Except(now))
                notify.ChatJoinLeave(Loc.T("chat.left", name, now.Count));
            foreach (var name in now.Except(_known))
                notify.Notify(NotifyEvent.PlayerJoined, Loc.T("notify.ev.joined", name),
                    AgentNotifications.Line("◦", "notify.lbl.online", now.Count.ToString()));
            foreach (var name in _known.Except(now))
                notify.Notify(NotifyEvent.PlayerLeft, Loc.T("notify.ev.left", name),
                    AgentNotifications.Line("◦", "notify.lbl.online", now.Count.ToString()));
        }
        _known = now;
    }
}
