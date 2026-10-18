using System.Diagnostics;
using eViSTool.Core.AppUpdate;
using eViSTool.Core.Localization;
using eViSTool.Core.Platform;
using eViSTool.Core.Server;
using eViSTool.Core.Versioning;

/// <summary>
/// Обновление eViSTool на этом компьютере по просьбе окна с другого: скачать архив, остановить сервер (мир
/// сохранится), поставить новые файлы и уступить место новой копии агента. Ход дел — в статусе (<see cref="Stage"/>).
/// </summary>
internal sealed class AgentSelfUpdate(AgentState state)
{
    /// <summary>Этап: download, stop, install, restart; failed — не вышло (<see cref="Error"/>); null — не обновлялись.</summary>
    public string? Stage { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Обновление уже идёт — второе не начинать.</summary>
    public bool Busy => Stage is not (null or "failed");

    /// <summary>Начать обновление до <paramref name="target"/> в фоне; статус сразу говорит «download».</summary>
    public void Begin(ModVersion target)
    {
        Stage = "download";
        Error = null;
        _ = Task.Run(() => RunAsync(target));
    }

    private async Task RunAsync(ModVersion target)
    {
        var host = state.Host;
        var opts = state.Options;
        var appDir = AppContext.BaseDirectory;
        var wasRunning = false; // сервер работал до обновления — при сбое его надо поднять обратно
        try
        {
            host.Console.Add(ConsoleLineKind.System, Loc.T("selfupd.started", state.Version, target));
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var updater = new AppUpdater(http);
            var release = await updater.FindReleaseAsync(target)
                          ?? throw new InvalidOperationException(Loc.T("selfupd.noRelease", target));
            // архив с проверкой отпечатка — тем же путём, что и самообновление окна
            var zip = await updater.DownloadAsync(release, Path.Combine(Path.GetTempPath(), "eViSTool-update"));

            // работающий сервер держит консоль агента — останавливаем (мир сохранится) и потом запускаем снова
            wasRunning = host.State != ServerState.Stopped;
            if (wasRunning)
            {
                Stage = "stop";
                try { await host.SendCommandAsync("/announce " + Loc.T("selfupd.announce")); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
                await Task.Delay(TimeSpan.FromSeconds(3));
                await host.StopAsync();
            }

            Stage = "install";
            AppUpdater.Install(zip, appDir);

            // под systemd новую версию поднимает служба: своя дочерняя копия погибла бы вместе с этой. Выходим с кодом 75
            // (в юните — «не сбой, перезапустить»), а что было с сервером, новая версия узнает из отметки
            if (Environment.GetEnvironmentVariable(SystemdUnit.ServiceVariable) is { Length: > 0 })
            {
                Stage = "restart";
                AgentProtocol.MarkAfterUpdate(opts.ProfileId, opts.AgentsDir, wasRunning);
                host.Console.Add(ConsoleLineKind.System, Loc.T("selfupd.restarting", target));
                await Task.Delay(TimeSpan.FromSeconds(1)); // окно успеет увидеть «перезапуск»
                state.ExitCode = SystemdUnit.SelfUpdateExitCode;
                state.Shutdown.Cancel();
                return;
            }

            // новая копия агента — с теми же параметрами; она дождётся, пока эта выйдет и освободит профиль (и порт удалённого доступа)
            Stage = "restart";
            var psi = new ProcessStartInfo(Path.Combine(appDir, AgentProtocol.ExeName))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = appDir,
            };
            foreach (var a in AppUpdater.RelaunchArgs(Environment.GetCommandLineArgs().Skip(1), wasRunning))
                psi.ArgumentList.Add(a);
            using (Process.Start(psi)) { }
            host.Console.Add(ConsoleLineKind.System, Loc.T("selfupd.restarting", target));
            await Task.Delay(TimeSpan.FromSeconds(1)); // окно успеет увидеть «перезапуск»
            state.Shutdown.Cancel();
        }
        catch (Exception ex)
        {
            // любой сбой (нет места, битый архив, нет прав…) — файлы AppUpdater.Install вернул на место сам, а сервер,
            // остановленный ради обновления, поднимаем: иначе на Linux-службе он стоял бы до ручного вмешательства
            Stage = "failed";
            Error = ex.Message;
            host.Console.Add(ConsoleLineKind.Error, Loc.T("selfupd.failed", ex.Message));
            if (wasRunning && host.State == ServerState.Stopped && !state.Shutdown.IsCancellationRequested)
            {
                try
                {
                    host.Console.Add(ConsoleLineKind.System, Loc.T("selfupd.restartServer"));
                    await host.StartAsync();
                }
                catch (Exception startEx)
                {
                    host.Console.Add(ConsoleLineKind.Error, startEx.Message);
                }
            }
        }
    }
}
