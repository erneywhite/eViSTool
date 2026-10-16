using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;
using eViSTool.Core.Server;

/// <summary>
/// Сервер остановился сам (упал, выключился от ошибок) — разобрать его вывод за этот запуск: какой мод виноват.
/// Результат — в статусе (<see cref="LastCrash"/>): окна всех, кто следит за этим сервером, покажут оповещение.
/// </summary>
internal sealed class CrashWatch(AgentState state, AgentNotifications notify)
{
    public ServerCrashInfo? LastCrash { get; private set; }

    public void OnServerState(ServerState serverState)
    {
        var host = state.Host;
        if (serverState != ServerState.Stopped || !host.LastExitOnItsOwn) return;
        var (startSeq, crashed, started) = (host.SessionStartSeq, host.LastExitCrashed, state.ReachedRunning);
        _ = Task.Run(() =>
        {
            try
            {
                var lines = host.Console.GetSince(startSeq, 100_000)
                    .Where(l => l.Kind is ConsoleLineKind.Output or ConsoleLineKind.Error).Select(l => l.Text).ToList();
                string? report = null;
                if (CrashAnalyzer.CrashFilePath(lines) is { } path && File.Exists(path)) report = File.ReadAllText(path);
                var finding = CrashAnalyzer.AnalyzeServer(GameLog.Parse(lines), report, crashed, ModFingerprints.Build(state.Mods.Locals()));
                var cause = finding?.Culprit is { } c
                    ? Loc.T("srv.crashCulprit", c.Mod?.Name ?? c.ModId, c.Version ?? "")
                    : Loc.T("srv.crashNoCulprit");
                // в оповещении — без ссылок на «строки выше»: консоли там не видно. Сторож поднимет сервер сам — так и скажем
                var why = finding?.Culprit is { } culprit
                    ? Loc.T("notify.ev.culprit", culprit.Mod?.Name ?? culprit.ModId, culprit.Version ?? "")
                    : Loc.T("notify.ev.noCulprit");
                var details = new List<NotifyLine> { AgentNotifications.Line("⚑", "notify.lbl.mod", why) };
                if (host.RestartScheduledAt is { } at)
                    details.Add(AgentNotifications.Line("↻", "notify.lbl.watchdog",
                        Loc.T("notify.ev.restartIn", Math.Max(1, (int)(at - DateTime.Now).TotalSeconds))));
                notify.Notify(started ? NotifyEvent.ServerCrashed : NotifyEvent.StartFailed,
                    started ? Loc.T("notify.ev.crashed") : Loc.T("notify.ev.startFailed"),
                    [.. details]);
                if (finding is null) return;
                LastCrash = ServerCrashInfo.From(finding, DateTime.Now);
                host.Console.Add(ConsoleLineKind.System, cause);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        });
    }
}
