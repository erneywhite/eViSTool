using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.Platform;
using eViSTool.Core.Server;

/// <summary>
/// Команда «setup»: найти сервер (ключи, data/agent.json, поиск), показать, что нашлось и откуда, проверить права и всё,
/// без чего сервер не заработает, и запомнить его папки в data/agent.json. Повторный запуск обновляет файл: что
/// человек дописал в нём руками, остаётся.
///
///   eViSTool.Agent setup [--game &lt;папка&gt;] [--data &lt;папка&gt;] [--profile &lt;id&gt;] [--name &lt;имя&gt;] [--arg &lt;аргумент&gt;]…
///                        [--start | --no-start] [--force]
/// </summary>
internal static class SetupCommand
{
    // ключи setup: со значением и без; остальное — опечатка, о ней лучше сказать сразу
    private static readonly HashSet<string> WithValue = ["--game", "--data", "--profile", "--name", "--arg", "--lang", "--agents-dir"];
    private static readonly HashSet<string> Flags = ["--force", "--start", "--no-start", "--help", "-h"];

    public static int Run(AgentArgs cli, string[] args)
    {
        string? name = null;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (WithValue.Contains(args[i]))
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine(Loc.T("setup.needValue", args[i]));
                    return Commands.Usage(Commands.BadUsage);
                }
                if (args[i] == "--name") name = args[i + 1];
                i++;
            }
            else if (Flags.Contains(args[i]))
            {
                force |= args[i] == "--force";
            }
            else
            {
                Console.Error.WriteLine(Loc.T("setup.badOption", args[i]));
                return Commands.Usage(Commands.BadUsage);
            }
        }
        if (cli.Help) return Commands.Usage(Commands.Ok);
        if (cli.ConfigError is { } broken)
        {
            Console.Error.WriteLine(broken);
            return Commands.Failed;
        }

        // где сервер: ключ, иначе agent.json, иначе поиск — как у агента при запуске
        var config = cli.Config;
        var gameKey = AgentConfig.Pick(cli.GameDir, null);
        var dataKey = AgentConfig.Pick(cli.DataPath, null);
        var tried = new List<string>();
        if (ServerLocator.Locate(gameKey ?? config?.GameDir, dataKey ?? config?.DataDir, AppContext.BaseDirectory, tried) is not { } found)
        {
            Console.Error.WriteLine(Loc.T("agent.notFound", string.Join(", ", tried)));
            Console.Error.WriteLine(Loc.T("setup.notFoundHint"));
            return Commands.Failed;
        }
        var profile = cli.Profile;
        var user = Environment.UserName;
        Console.WriteLine(Loc.T("setup.game", found.GameDir,
            gameKey is not null ? Loc.T("setup.srcKey", "--game")
            : config?.GameDir is not null ? Loc.T("setup.srcConfig", AgentConfig.FileName)
            : Loc.T("setup.srcFound")));
        Console.WriteLine(Loc.T("setup.data", found.DataDir,
            dataKey is not null ? Loc.T("setup.srcKey", "--data")
            : config?.DataDir is not null ? Loc.T("setup.srcConfig", AgentConfig.FileName)
            : found.Script is { } script && !SameDir(found.DataDir, ServerLocator.DefaultDataDir) ? Loc.T("setup.srcScript", script)
            : Loc.T("setup.srcDefault")));
        Console.WriteLine(Loc.T("setup.profile", profile));
        Console.WriteLine(Loc.T("setup.user", user));
        Console.WriteLine();

        // от root — нельзя, если всё это принадлежит другому пользователю: его файлы служба потом не сможет менять.
        // Проверяем до всего, что пишет на диск (даже папку data eViSTool не создаём)
        if (!force && AgentSetup.OwnerToRunAs(found.DataDir, AppContext.BaseDirectory) is { } owner)
        {
            Console.Error.WriteLine(Loc.T("setup.asRoot", owner, $"sudo -u {owner} {Self()} {Shell.Join(["setup", .. args])}"));
            return Commands.Failed;
        }

        var checks = new List<SetupCheck> { AgentSetup.CheckGame(found.GameDir) };
        if (AgentSetup.CheckDotnet(found.GameDir) is { } dotnet) checks.Add(dotnet);
        checks.Add(AgentSetup.CheckData(found.DataDir));
        if (AgentSetup.CheckDataOwners(found.DataDir) is { } owners) checks.Add(owners);
        // настройки — в data рядом с программой: если туда нельзя писать, eViSTool молча взял бы папку в профиле
        // пользователя, и служба с командами от разных пользователей видели бы разные настройки
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        var settingsDir = cli.AgentsDir is not null ? Path.GetDirectoryName(AgentConfig.FileFor(cli.AgentsDir))! : portable;
        checks.Add(cli.AgentsDir is null && !AppPaths.IsPortable
            ? new SetupCheck(SetupLevel.Problem, Loc.T("setup.notPortable", portable, AgentSetup.Fix(AppContext.BaseDirectory)))
            : AgentSetup.CheckSettingsDir(settingsDir));

        // уже работающий сервер: свой агент (подхватит настройки после перезапуска) или кто-то другой
        var own = OwnAgent(profile, cli.AgentsDir);
        var others = AgentSetup.OtherServers(found.GameDir, found.DataDir, own?.ServerPid);
        foreach (var pid in others) checks.Add(new SetupCheck(SetupLevel.Warning, Loc.T("setup.otherServer", pid)));
        if (own is not null) checks.Add(new SetupCheck(SetupLevel.Warning, Loc.T("setup.agentRunning", own.AgentPid)));
        else if (others.Count == 0) checks.Add(new SetupCheck(SetupLevel.Ok, Loc.T("setup.okNotRunning")));

        foreach (var check in checks)
            Console.WriteLine((check.Level switch { SetupLevel.Ok => "  [ok] ", SetupLevel.Warning => "  [!]  ", _ => "  [x]  " }) + check.Text);
        Console.WriteLine();
        if (!force && checks.Any(c => c.Level == SetupLevel.Problem))
        {
            Console.Error.WriteLine(Loc.T("setup.notSaved"));
            return Commands.Failed;
        }

        var file = AgentConfig.FileFor(cli.AgentsDir);
        try
        {
            AgentConfig.Save(file, new AgentConfigUpdate(found.GameDir, found.DataDir,
                Profile: AgentConfig.Pick(cli.ProfileId, null),
                StartServer: cli.StartServer ? true : cli.NoStart ? false : null,
                ServerName: name?.Trim(),
                ServerArgs: cli.ExtraArgs.Count > 0 ? cli.ExtraArgs : null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return Commands.Failed;
        }
        Console.WriteLine(Loc.T("setup.saved", file));
        Console.WriteLine();

        // что дальше: от того же пользователя (через sudo -u, если так и запускали), служба — от root
        var self = Self();
        var asUser = OperatingSystem.IsWindows() ? self
            : UnixAccount.IsRoot ? "sudo " + self
            : Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } ? $"sudo -u {Shell.Quote(user)} {self}"
            : self;
        Console.WriteLine(Loc.T("setup.next"));
        Console.WriteLine(Loc.T("setup.nextRemote", asUser));
        if (!OperatingSystem.IsWindows()) Console.WriteLine(Loc.T("setup.nextService", "sudo " + self));
        return Commands.Ok;
    }

    /// <summary>Агент этого профиля уже работает — его PID и PID его сервера (null — агента нет или он не ответил).</summary>
    private static AgentStatus? OwnAgent(string profile, string? agentsDir)
    {
        if (!File.Exists(AgentProtocol.StateFile(profile, agentsDir)) || AgentClient.TryConnect(profile, agentsDir) is not { } client)
            return null;
        using (client)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                return client.StatusAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                // работает, но не отвечает — считаем, что его нет: проверки выше покажут, если сервер занят
                return new AgentStatus { AgentPid = client.Endpoint.Pid };
            }
        }
    }

    /// <summary>Как запустить агента снова: «./eViSTool.Agent», если он в текущей папке, иначе полный путь.</summary>
    private static string Self()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AgentProtocol.ExeName);
        if (Path.GetDirectoryName(exe) is { } dir && SameDir(dir, Environment.CurrentDirectory))
            return (OperatingSystem.IsWindows() ? ".\\" : "./") + Path.GetFileName(exe);
        return OperatingSystem.IsWindows() ? exe : Shell.Quote(exe);
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
