using eViSTool.Core.Localization;
using eViSTool.Core.Server;

/// <summary>
/// Команды «status», «start», «stop», «restart», «command»: работают с уже запущенным агентом этого профиля через его
/// локальный HTTP API (адрес и ключ — в data/agents/&lt;профиль&gt;.json и .key), как окно на этом компьютере.
/// Кнопки окна отвечают сразу, а окно следит за статусом; здесь следить некому — команда ждёт итога сама (с пределом
/// по времени) и говорит его словами: запустился, не запустился и почему, остановлен.
/// </summary>
internal static class ControlCommand
{
    private const string TimeoutOption = "--timeout";
    private const string WaitOption = "--wait";

    // большой мир с модами поднимается и сохраняется минутами
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    // ответ на команду — строки консоли за это время; пришли и стихли на секунду — ответ кончился
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    public static int Run(string command, AgentArgs cli, string[] args)
    {
        if (cli.Help) return Commands.Usage(Commands.Ok);
        var w = command switch
        {
            "start" or "stop" or "restart" => CliWords.Parse(args, TimeoutOption),
            "command" => CliWords.Parse(args, WaitOption),
            _ => CliWords.Parse(args),
        };
        if (w.Error is not null || (command != "command" && w.TooMany(0))) return w.Bad(Loc.T("agent.usage"));
        var timeout = w.Seconds(TimeoutOption, DefaultTimeout);
        var wait = w.Seconds(WaitOption, DefaultWait);
        if (timeout is null || wait is null) return w.Bad(Loc.T("agent.usage"));
        // «command /time set day» — без кавычек тоже: слова — одна команда; «time» → «/time», как в консоли окна
        var text = string.Join(" ", w.Words).Trim();
        if (command == "command" && text.Length == 0)
        {
            Console.Error.WriteLine(Loc.T("ctl.noCommand"));
            return Commands.BadUsage;
        }
        if (text.Length > 0 && !text.StartsWith('/')) text = "/" + text;

        if (AgentUser.AgentsDir(cli, write: false) is not { } dir) return Commands.Failed;
        var profile = cli.Profile; // ключ --profile, иначе agent.json, иначе «server» — как у самого агента
        AgentClient? client;
        try
        {
            client = AgentClient.TryConnect(profile, dir);
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Commands.Failed;
        }
        if (client is null)
        {
            Console.Error.WriteLine(Loc.T("ctl.notRunning", profile));
            Console.Error.WriteLine(Loc.T("ctl.startAgentHint"));
            return Commands.Failed;
        }

        using (client)
        {
            try
            {
                return (command switch
                {
                    "status" => Status(client, profile),
                    "start" => Start(client, timeout.Value),
                    "stop" => Stop(client, timeout.Value),
                    "restart" => Restart(client, timeout.Value),
                    _ => Send(client, text, wait.Value),
                }).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
            {
                // отказ агента (409 с текстом: сервер не запущен, мир занят, нет файла сервера) — как есть
                Console.Error.WriteLine(ex.Message);
                return Commands.Failed;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine(Loc.T("ctl.noConnection", ex.Message));
                return Commands.Failed;
            }
        }
    }

    // ---- status

    private static async Task<int> Status(AgentClient client, string profile)
    {
        var s = await client.StatusAsync();
        Console.WriteLine(Loc.T("ctl.agent", s.AgentPid, s.AgentVersion, profile));
        if (AgentProtocol.IsOutdated(s)) Console.WriteLine(Loc.T("ctl.otherVersion", s.AgentVersion, AgentProtocol.AppVersion));
        Console.WriteLine(Loc.T("ctl.server", StateText(s)));
        if (s.RestartScheduledAt is { } retry) Console.WriteLine(Loc.T("ctl.watchdog", Time(retry)));
        Console.WriteLine(Loc.T("ctl.game", s.GameVersion ?? "?", s.GamePort?.ToString() ?? "?"));
        if (s.State == ServerState.Running)
            Console.WriteLine(s.Players.Count == 0 ? Loc.T("ctl.noPlayers")
                : Loc.T("ctl.players", s.Players.Count, string.Join(", ", s.Players.Select(p => p.Name))));
        Console.WriteLine(s.RemotePort is { } port ? Loc.T("ctl.remoteOn", port)
            : s.RemoteError is { Length: > 0 } error ? Loc.T("ctl.remoteError", error)
            : Loc.T("ctl.remoteOff"));
        if (s.NextRestartAt is { } restart) Console.WriteLine(Loc.T("ctl.nextRestart", Time(restart)));
        if (s.NextBackupAt is { } backup) Console.WriteLine(Loc.T("ctl.nextBackup", Time(backup)));
        if (s.LastCrash is { } crash)
            Console.WriteLine(Loc.T("ctl.lastCrash", Time(crash.At), crash.ModName is { } mod
                ? Loc.T("srv.crashCulprit", mod, crash.ModVersion ?? "") : crash.Reason));
        return Commands.Ok;
    }

    private static string StateText(AgentStatus s) => s.State switch
    {
        ServerState.Running => Loc.T("ctl.srvRunning", Uptime(DateTime.Now - (s.StartedAt ?? DateTime.Now)), s.ServerPid?.ToString() ?? "?",
            s.MemoryMb is { } mb ? SizeText.Format(mb * 1024 * 1024) : "?"),
        ServerState.Starting => Loc.T("ctl.srvStarting"),
        ServerState.Stopping => Loc.T("ctl.srvStopping"),
        _ => s.LastExitCode is { } code ? Loc.T("ctl.srvStoppedCode", code) : Loc.T("ctl.srvStopped"),
    };

    private static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? Loc.T("ctl.uptimeDays", (int)t.TotalDays, t.Hours)
        : t.TotalHours >= 1 ? Loc.T("ctl.uptimeHours", (int)t.TotalHours, t.Minutes)
        : Loc.T("ctl.uptimeMinutes", Math.Max(0, (int)t.TotalMinutes));

    private static string Time(DateTime at) => at.ToString(at.Date == DateTime.Today ? "T" : "g", Loc.Culture);

    // ---- start, stop, restart

    private static async Task<int> Start(AgentClient client, TimeSpan timeout)
    {
        var s = await client.StatusAsync();
        switch (s.State)
        {
            case ServerState.Running:
                Console.WriteLine(Loc.T("ctl.alreadyRunning", s.ServerPid?.ToString() ?? "?"));
                return Commands.Ok;
            case ServerState.Stopping:
                Console.Error.WriteLine(Loc.T("ctl.stillStopping"));
                return Commands.Failed;
        }
        Console.WriteLine(Loc.T("ctl.starting"));
        var since = s.LastSeq;
        if (s.State == ServerState.Stopped) await client.StartAsync();
        // запускается: дождаться «работает»; остановился — не запустился
        return await WaitStarted(client, since, timeout, st => st.State == ServerState.Stopped, withCode: true);
    }

    private static async Task<int> Restart(AgentClient client, TimeSpan timeout)
    {
        var s = await client.StatusAsync();
        if (s.State == ServerState.Stopping)
        {
            Console.Error.WriteLine(Loc.T("ctl.stillStopping"));
            return Commands.Failed;
        }
        Console.WriteLine(Loc.T("ctl.restarting"));
        var (since, oldPid) = (s.LastSeq, s.State == ServerState.Stopped ? null : s.ServerPid);
        await client.RestartAsync();
        // между остановкой и новым запуском «остановлен» мелькает на миг; держится два опроса подряд — не запустился
        var stoppedBefore = false;
        // (код выхода тут — скорее прежнего процесса: новый мог и не начаться, например, мир занят — не показываем)
        return await WaitStarted(client, since, timeout, st =>
        {
            var stopped = st.State == ServerState.Stopped;
            var failed = stopped && stoppedBefore;
            stoppedBefore = stopped;
            return failed;
        }, withCode: false, oldPid);
    }

    /// <summary>Ждать, пока сервер заработает (новый процесс, если был oldPid). failed — признак, что запуск не вышел.</summary>
    private static async Task<int> WaitStarted(AgentClient client, long since, TimeSpan timeout,
        Func<AgentStatus, bool> failed, bool withCode, int? oldPid = null)
    {
        var started = DateTime.Now;
        while (true)
        {
            await Task.Delay(Poll);
            var st = await client.StatusAsync();
            if (st.State == ServerState.Running && (oldPid is null || st.ServerPid != oldPid))
            {
                Console.WriteLine(Loc.T("ctl.started", st.ServerPid?.ToString() ?? "?"));
                return Commands.Ok;
            }
            if (failed(st))
            {
                Console.Error.WriteLine(withCode && st.LastExitCode is { } code ? Loc.T("ctl.startFailedCode", code) : Loc.T("ctl.startFailed"));
                await PrintTail(client, since);
                if (st.RestartScheduledAt is { } retry) Console.Error.WriteLine(Loc.T("ctl.watchdog", Time(retry)));
                return Commands.Failed;
            }
            if (DateTime.Now - started > timeout) return TimedOut(st, timeout);
        }
    }

    private static async Task<int> Stop(AgentClient client, TimeSpan timeout)
    {
        var s = await client.StatusAsync();
        // остановлен, но сторож собрался поднять его снова — остановка отменит и это
        if (s.State == ServerState.Stopped && s.RestartScheduledAt is null)
        {
            Console.WriteLine(Loc.T("ctl.alreadyStopped"));
            return Commands.Ok;
        }
        Console.WriteLine(Loc.T("ctl.stopping"));
        await client.StopAsync();
        var started = DateTime.Now;
        while (true)
        {
            var st = await client.StatusAsync();
            if (st.State == ServerState.Stopped && st.RestartScheduledAt is null)
            {
                Console.WriteLine(st.LastExitCode is { } code ? Loc.T("ctl.stoppedCode", code) : Loc.T("ctl.stopped"));
                return Commands.Ok;
            }
            if (DateTime.Now - started > timeout) return TimedOut(st, timeout);
            await Task.Delay(Poll);
        }
    }

    private static int TimedOut(AgentStatus st, TimeSpan timeout)
    {
        Console.Error.WriteLine(Loc.T("ctl.timeout", (int)timeout.TotalSeconds, StateText(st)));
        return Commands.Failed;
    }

    /// <summary>Не запустился — последние строки консоли этого запуска: причина обычно там.</summary>
    private static async Task PrintTail(AgentClient client, long since)
    {
        var lines = await client.ConsoleAsync(since, 0);
        foreach (var l in lines.Where(l => l.Kind != ConsoleLineKind.Input).TakeLast(15))
            Console.Error.WriteLine("  " + ConsoleMarkup.ToPlain(l.Text));
    }

    // ---- command

    /// <summary>
    /// Команда серверу и его ответ: строки консоли, пришедшие после неё (как их показывает консоль окна, тем же
    /// долгим опросом). Пришли и стихли на секунду — ответ кончился; дольше wait не ждём, сервер может и молчать.
    /// </summary>
    private static async Task<int> Send(AgentClient client, string text, TimeSpan wait)
    {
        var seq = (await client.StatusAsync()).LastSeq;
        await client.CommandAsync(text);
        var until = DateTime.Now + wait;
        var any = false;
        var lastLine = DateTime.Now;
        while (DateTime.Now < until && !(any && DateTime.Now - lastLine >= Quiet))
        {
            var lines = await client.ConsoleAsync(seq, 1);
            if (lines.Count == 0) continue;
            seq = lines[^1].Seq;
            // свою же команду (и чужие из окна) не повторяем: в ответе — только то, что написали сервер и агент
            foreach (var l in lines.Where(l => l.Kind != ConsoleLineKind.Input))
            {
                Console.WriteLine(ConsoleMarkup.ToPlain(l.Text));
                any = true;
                lastLine = DateTime.Now;
            }
        }
        if (!any) Console.Error.WriteLine(Loc.T("ctl.noAnswer", (int)wait.TotalSeconds));
        return Commands.Ok;
    }
}
