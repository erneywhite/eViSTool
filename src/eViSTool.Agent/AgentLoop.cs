using eViSTool.Core.Localization;
using eViSTool.Core.Server;

/// <summary>
/// Главный цикл агента: раз в 5 секунд — шаги по расписанию (статистика, здоровье сервера, ошибки модов, объявления,
/// перезапуски, копии), подхват настроек удалённого входа и выход после простоя. Шаги идут строго по очереди.
/// </summary>
internal sealed class AgentLoop(
    AgentState state,
    AgentStats stats,
    HealthWatch health,
    ModErrorWatch modErrors,
    ServerSchedule schedule,
    RemoteEntry remote,
    KnownCommands commands)
{
    /// <summary>
    /// Крутиться, пока агента не попросили выйти (<see cref="AgentState.Shutdown"/>) или пока он не простоял без дела:
    /// сервер остановлен, перезапуск не ждём, и дольше <see cref="AgentOptions.IdleExit"/> ничего не происходит.
    /// </summary>
    public async Task RunAsync()
    {
        var host = state.Host;
        var shutdown = state.Shutdown;
        while (!shutdown.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), shutdown.Token).ContinueWith(_ => { });
            try
            {
                stats.Tick();
                health.CheckDisk();
                health.CheckModUpdates();
                modErrors.Tick();
                state.ReloadAutomation();
                await schedule.AnnounceAsync();
                await schedule.RestartAsync();
                await schedule.RestartAfterBackupAsync();
                await remote.ApplyAsync();
                schedule.NotePlayers();
                commands.SaveIfChanged();
                await schedule.BackupAsync();

                var idle = host.State == ServerState.Stopped && host.RestartScheduledAt is null
                           && DateTime.Now - state.LastActivity > state.Options.IdleExit;
                if (idle) return;
            }
            catch (Exception ex) when (!shutdown.IsCancellationRequested)
            {
                // каждый шаг ловит свои ошибки; неожиданная не должна завершать цикл — иначе агент упадёт и уронит сервер
                host.Console.Add(ConsoleLineKind.Error, Loc.T("agent.tickFailed", ex.GetType().Name, ex.Message));
            }
        }
    }
}
