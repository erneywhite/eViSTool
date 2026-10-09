using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Platform;
using eViSTool.Core.Server;
using Newtonsoft.Json;

/// <summary>
/// Команда «service»: служба systemd — install, uninstall, status (только Linux). Ставить и удалять — от root. Агент в
/// службе работает от владельца данных сервера (обычно vintagestory) и запускает сервер вместе с собой. Текст юнита
/// собирает <see cref="SystemdUnit"/>; здесь — проверки до установки, systemctl и понятные сообщения.
/// </summary>
internal static class ServiceCommand
{
    // общие ключи агента (их разбирает AgentArgs): у этих есть значение — его пропускаем
    private static readonly HashSet<string> ValueKeys =
        ["--profile", "--exe", "--data", "--game", "--arg", "--idle-exit", "--agents-dir", "--lang", "--backup-name"];
    private static readonly HashSet<string> FlagKeys = ["--start", "--after-update", "--help", "-h"];

    // сколько ждать агента после запуска службы: обрезанный агент поднимается за секунду, медленный диск — дольше
    private static readonly TimeSpan AgentWait = TimeSpan.FromSeconds(30);

    public static int Run(AgentArgs cli, string[] args)
    {
        var sub = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;
        string? user = null, name = null;
        for (var i = sub is null ? 0 : 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--user" when sub == "install": user = i + 1 < args.Length ? args[++i] : ""; break;
                case "--name": name = i + 1 < args.Length ? args[++i] : ""; break;
                case var key when ValueKeys.Contains(key): i++; break;
                case var key when FlagKeys.Contains(key): break;
                default:
                    Console.Error.WriteLine(Loc.T("service.unknownKey", args[i]));
                    return Usage(Commands.BadUsage);
            }
        }
        if (cli.Help) return Usage(Commands.Ok);
        if (sub is not ("install" or "uninstall" or "status"))
        {
            if (sub is not null) Console.Error.WriteLine(Loc.T("agent.unknownCommand", "service " + sub));
            return Usage(Commands.BadUsage);
        }
        if (user is "")
        {
            Console.Error.WriteLine(Loc.T("service.noUserName"));
            return Commands.BadUsage;
        }
        if (SystemdUnit.NormalizeName(name) is not { } unit)
        {
            Console.Error.WriteLine(Loc.T("service.badName", name ?? ""));
            return Commands.BadUsage;
        }

        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine(Loc.T("service.onlyLinux"));
            return Commands.Failed;
        }
        if (!Directory.Exists("/run/systemd/system"))
        {
            Console.Error.WriteLine(Loc.T("service.noSystemd"));
            return Commands.Failed;
        }
        if (sub == "status") return Status(unit);
        if (!Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine(Loc.T("service.needRoot", string.Join(' ', ["service", .. args])));
            return Commands.Failed;
        }
        return sub == "install" ? Install(cli, unit, user) : Uninstall(unit);
    }

    private static int Usage(int code)
    {
        (code == Commands.Ok ? Console.Out : Console.Error).WriteLine(Loc.T("service.usage"));
        return code;
    }

    // ---- install

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static int Install(AgentArgs cli, string unit, string? explicitUser)
    {
        var appDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var agentExe = Path.Combine(appDir, AgentProtocol.ExeName);
        if (!File.Exists(agentExe)) agentExe = Environment.ProcessPath ?? agentExe;
        var appData = Path.Combine(appDir, "data");
        var appDataExisted = Directory.Exists(appData);

        // сервер — так же, как найдёт его сам агент в службе (те же ключи, server.sh, соседние папки)
        var tried = new List<string>();
        if (AgentOptions.From(cli, tried) is not { } opts)
        {
            Console.Error.WriteLine(Loc.T("agent.notFound", string.Join(", ", tried)));
            Console.Error.WriteLine(Loc.T("agent.notFoundHint"));
            return Commands.Failed;
        }
        var serverFile = Path.GetFullPath(opts.ExePath);
        var gameDir = opts.Located?.GameDir ?? Path.GetDirectoryName(serverFile)!;
        var dataDir = Path.GetFullPath(opts.DataPath);

        // от кого: названный ключом или владелец данных сервера / папки eViSTool — но не root без явной просьбы
        if (UnixAccounts.PickServiceUser(explicitUser, UnixAccounts.OwnerOf(dataDir), UnixAccounts.OwnerOf(appDir)) is not { } userName)
        {
            Console.Error.WriteLine(Loc.T("service.noUser", dataDir, appDir));
            return Commands.Failed;
        }
        if (UnixAccounts.Find(userName) is not { } user)
        {
            Console.Error.WriteLine(Loc.T("service.userMissing", userName));
            return Commands.Failed;
        }
        if (user.Uid == 0) Console.Error.WriteLine(Loc.T("service.rootUser"));

        // папку data могли только что создать мы сами (от root, пока искали сервер) — она должна быть пользователя службы
        if (!appDataExisted && Directory.Exists(appData)) UnixAccounts.Run("chown", "-R", $"{user.Name}:{user.Group}", appData);

        if (!CheckAccess(user, serverFile, dataDir, appDir, appData, agentExe)) return Commands.Failed;
        Warn(user, dataDir);

        var profile = string.IsNullOrWhiteSpace(cli.ProfileId) ? AgentArgs.DefaultProfile : cli.ProfileId;
        var agentsDir = cli.AgentsDir is { } dir ? Path.GetFullPath(dir) : Path.Combine(appData, "agents");
        var show = Show(unit);
        var mainPid = MainPid(show);
        var unitPath = SystemdUnit.PathFor(unit);
        var old = File.Exists(unitPath) ? File.ReadAllText(unitPath) : null;

        // имя занято чужой службой (своим файлом или системной из /usr/lib/systemd) — не трогаем
        var fragment = show.GetValueOrDefault("FragmentPath") ?? "";
        if (old is not null ? !SystemdUnit.IsOurs(old) : fragment.Length > 0 && fragment != unitPath)
        {
            Console.Error.WriteLine(Loc.T("service.notOurs", unit));
            return Commands.Failed;
        }
        // агент этого профиля уже работает не в службе — второй не запустится (профиль занят), сервер раздвоится
        if (RunningAgent(profile, agentsDir) is { } other && other != mainPid)
        {
            Console.Error.WriteLine(Loc.T("service.otherAgent", other));
            return Commands.Failed;
        }
        // сервер этого мира запущен не агентом (server.sh, руками) — служба запустила бы второй на тех же файлах
        if (ForeignServer(gameDir, dataDir, unit) is { } server)
        {
            Console.Error.WriteLine(Loc.T("service.serverRunning", server, dataDir));
            return Commands.Failed;
        }

        var environment = new Dictionary<string, string>();
        // без домашней папки агент (один файл с .NET внутри) не запустится: ему некуда распаковать свои библиотеки
        if (!HomeUsable(user))
        {
            var extract = Path.Combine(appData, ".net");
            Directory.CreateDirectory(extract);
            UnixAccounts.Run("chown", "-R", $"{user.Name}:{user.Group}", appData);
            environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extract;
            Console.Error.WriteLine(Loc.T("service.noHome", user.Name, user.Home, extract));
        }

        var text = new SystemdUnit
        {
            Name = unit,
            ExecPath = agentExe,
            Args = AgentArgsFor(cli),
            WorkingDirectory = appDir,
            User = user.Name,
            Group = user.Group,
            DataDir = dataDir,
            WritablePaths = [appDir, dataDir],
            Environment = environment,
        }.Render();

        var active = show.GetValueOrDefault("ActiveState") is "active" or "activating" or "reloading";
        // прошлые запуски упёрлись в предел перезапусков — без сброса systemd не даст запустить снова
        if (!active) UnixAccounts.Run("systemctl", "reset-failed", unit);
        if (text == old)
        {
            Console.WriteLine(Loc.T("service.unchanged", unit, unitPath));
            if (!Systemctl("enable", unit) || (!active && !Systemctl("start", unit))) return Commands.Failed;
        }
        else
        {
            var tmp = unitPath + ".tmp";
            File.WriteAllText(tmp, text);
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(tmp, unitPath, overwrite: true);
            Console.WriteLine(Loc.T("service.written", unitPath));
            if (!Systemctl("daemon-reload") || !Systemctl("enable", unit)) return Commands.Failed;
            if (active)
            {
                Console.WriteLine(Loc.T("service.restarting"));
                if (!Systemctl("restart", unit)) return Commands.Failed;
            }
            else if (!Systemctl("start", unit)) return Commands.Failed;
        }

        if (WaitForAgent(unit, profile, agentsDir) is not { } pid)
        {
            Console.Error.WriteLine(Loc.T("service.failedStart", unit));
            Console.Error.WriteLine(UnixAccounts.Run("journalctl", "-u", unit + ".service", "-n", "20", "--no-pager").Output.TrimEnd());
            return Commands.Failed;
        }
        Console.WriteLine(Loc.T("service.installed", unit, agentExe, user.Name, pid, gameDir, dataDir));
        Console.WriteLine(ServerLine(profile, agentsDir));
        Console.WriteLine(Loc.T("service.installedHint", unit, agentExe));
        return Commands.Ok;
    }

    /// <summary>Ключи агента в службе: --start (сервер поднимается вместе с ней) и то, что дали при установке, —
    /// пути абсолютными: рабочая папка у службы своя.</summary>
    private static List<string> AgentArgsFor(AgentArgs cli)
    {
        var args = new List<string> { "--start" };
        void AddPath(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) args.AddRange([key, Path.GetFullPath(value)]);
        }
        void AddValue(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) args.AddRange([key, value]);
        }
        AddPath("--game", cli.GameDir);
        AddPath("--exe", cli.ExePath);
        AddPath("--data", cli.DataPath);
        AddValue("--profile", cli.ProfileId);
        AddPath("--agents-dir", cli.AgentsDir);
        AddValue("--lang", cli.Language);
        AddValue("--backup-name", cli.BackupName);
        foreach (var extra in cli.ExtraArgs) args.AddRange(["--arg", extra]);
        return args;
    }

    /// <summary>Хватит ли пользователю прав: читать сервер, писать данные сервера и папку eViSTool, запускать агента.
    /// Не хватает — что именно и чем исправить.</summary>
    private static bool CheckAccess(UnixUser user, string serverFile, string dataDir, string appDir, string appData, string agentExe)
    {
        var problems = new List<string>();
        var fix = new List<string>(); // кому отдать папку (chown)
        void Need(char access, string path, string key, string owner)
        {
            if (UnixAccounts.CanAccess(user.Name, path, access) != false) return;
            problems.Add(Loc.T(key, path));
            if (!fix.Contains(owner)) fix.Add(owner);
        }
        Need('r', serverFile, "service.cantReadGame", Path.GetDirectoryName(serverFile)!);
        Need('w', dataDir, "service.cantWriteData", dataDir);
        Need('w', appDir, "service.cantWriteApp", appDir);
        Need('x', agentExe, "service.cantRunAgent", appDir);
        // data eViSTool, созданная запуском от root: агент службы не прочитает свои ключи и не запишет настройки
        if (UnixAccounts.ForeignEntry(appData, user.Name) is { } foreign)
        {
            problems.Add(Loc.T("service.foreignFile", foreign.Path, foreign.Owner));
            if (!fix.Contains(appDir)) fix.Add(appDir);
        }
        if (problems.Count == 0) return true;

        Console.Error.WriteLine(Loc.T("service.checkFailed", user.Name));
        foreach (var p in problems) Console.Error.WriteLine("  — " + p);
        foreach (var dir in fix) Console.Error.WriteLine(Loc.T("service.chownHint", $"{user.Name}:{user.Group}", Shell(dir)));
        return false;
    }

    /// <summary>Что не мешает установке, но может помешать серверу: чужие файлы в данных сервера, нет .NET для сервера.</summary>
    private static void Warn(UnixUser user, string dataDir)
    {
        if (UnixAccounts.ForeignEntry(dataDir, user.Name) is { } foreign)
        {
            Console.Error.WriteLine(Loc.T("service.warnDataFile", foreign.Path, foreign.Owner, user.Name));
            Console.Error.WriteLine(Loc.T("service.chownHint", $"{user.Name}:{user.Group}", Shell(dataDir)));
        }
        if (ServerExecutable.FindDotnet() is not { } dotnet)
            Console.Error.WriteLine(Loc.T("srv.dotnetMissing", 10));
        else if (UnixAccounts.CanAccess(user.Name, dotnet, 'x') == false)
            Console.Error.WriteLine(Loc.T("service.cantRunDotnet", user.Name, dotnet));
    }

    private static bool HomeUsable(UnixUser user) =>
        user.Home.StartsWith('/') && user.Home != "/" && Directory.Exists(user.Home) && UnixAccounts.CanAccess(user.Name, user.Home, 'w') != false;

    /// <summary>PID агента этого профиля, если он работает (по его файлу адреса); null — не работает.</summary>
    private static int? RunningAgent(string profile, string agentsDir)
    {
        try
        {
            var file = AgentProtocol.StateFile(profile, agentsDir);
            if (!File.Exists(file)) return null;
            var pid = JsonConvert.DeserializeObject<AgentEndpoint>(File.ReadAllText(file))?.Pid ?? 0;
            return Alive(pid) ? pid : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Сервер этих данных, запущенный не агентом этой службы; null — такого нет.</summary>
    private static int? ForeignServer(string gameDir, string dataDir, string unit)
    {
        foreach (var s in GameProcess.FindServers(gameDir))
        {
            if (s.DataPath is null || Path.GetFullPath(s.DataPath) != dataDir) continue;
            if (!ReadText($"/proc/{s.Pid}/cgroup").Contains($"/{unit}.service", StringComparison.Ordinal)) return s.Pid;
        }
        return null;
    }

    /// <summary>Дождаться, пока агент службы запишет свой адрес (значит, поднялся и слушает); null — служба упала.</summary>
    private static int? WaitForAgent(string unit, string profile, string agentsDir)
    {
        for (var until = DateTime.Now + AgentWait; DateTime.Now < until; Thread.Sleep(500))
        {
            var show = Show(unit);
            if (show.GetValueOrDefault("ActiveState") is "failed" or "inactive") return null;
            if (MainPid(show) is > 0 and var main && RunningAgent(profile, agentsDir) == main) return main;
        }
        return null;
    }

    /// <summary>Строка о сервере после установки: запускается ли он. Агент с --start берётся за сервер сразу, но не мгновенно.</summary>
    private static string ServerLine(string profile, string agentsDir)
    {
        try
        {
            using var client = AgentClient.TryConnect(profile, agentsDir);
            for (var until = DateTime.Now.AddSeconds(10); client is not null; Thread.Sleep(500))
            {
                var status = client.StatusAsync().GetAwaiter().GetResult();
                if (status.State == ServerState.Running) return Loc.T("service.serverUp", status.ServerPid ?? 0);
                if (status.State == ServerState.Starting) return Loc.T("service.serverStarting", status.ServerPid ?? 0);
                if (DateTime.Now >= until) break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or JsonException)
        {
            // не узнали — скажем, где смотреть
        }
        return Loc.T("service.serverNotStarted");
    }

    // ---- uninstall

    private static int Uninstall(string unit)
    {
        var unitPath = SystemdUnit.PathFor(unit);
        var show = Show(unit);
        if (!File.Exists(unitPath))
        {
            // в /etc её нет, но systemd её знает (системная, из /usr/lib/systemd) — это не наша служба
            if (show.GetValueOrDefault("FragmentPath") is { Length: > 0 })
            {
                Console.Error.WriteLine(Loc.T("service.notOurs", unit));
                return Commands.Failed;
            }
            Console.WriteLine(Loc.T("service.notInstalled", unit, Path.Combine(AppContext.BaseDirectory, AgentProtocol.ExeName)));
            return Commands.Ok;
        }
        if (!SystemdUnit.IsOurs(File.ReadAllText(unitPath)))
        {
            Console.Error.WriteLine(Loc.T("service.notOurs", unit));
            return Commands.Failed;
        }
        var dir = show.GetValueOrDefault("WorkingDirectory") is { Length: > 0 } wd ? Path.Combine(wd, "data") : unitPath;
        Console.WriteLine(Loc.T("service.stopping", unit));
        // остановка ждёт, пока сервер сохранит мир; не вышло — всё равно убираем юнит, но говорим
        var stopped = Systemctl("disable", "--now", unit);
        File.Delete(unitPath);
        Systemctl("daemon-reload");
        UnixAccounts.Run("systemctl", "reset-failed", unit);
        Console.WriteLine(Loc.T("service.removed", unit, dir));
        return stopped ? Commands.Ok : Commands.Failed;
    }

    // ---- status

    private static int Status(string unit)
    {
        var show = Show(unit);
        if (show.Count == 0) return Commands.Failed; // systemctl не ответил — причину он написал сам (см. Show)
        if (show.GetValueOrDefault("LoadState") is "not-found" or null or "")
        {
            Console.WriteLine(Loc.T("service.notInstalled", unit, Path.Combine(AppContext.BaseDirectory, AgentProtocol.ExeName)));
            return Commands.Ok;
        }
        var enabled = show.GetValueOrDefault("UnitFileState") == "enabled" ? Loc.T("service.stEnabled") : Loc.T("service.stDisabled");
        var state = show.GetValueOrDefault("ActiveState") switch
        {
            "active" => Loc.T("service.stActive", show.GetValueOrDefault("ActiveEnterTimestamp") ?? "", MainPid(show)),
            "activating" or "reloading" => Loc.T("service.stActivating", show.GetValueOrDefault("SubState") ?? ""),
            "deactivating" => Loc.T("service.stStopping"),
            "failed" => Loc.T("service.stFailed", show.GetValueOrDefault("Result") ?? ""),
            _ => Loc.T("service.stInactive"),
        };
        Console.WriteLine(Loc.T("service.stHead", unit, enabled, state));
        if (show.GetValueOrDefault("User") is { Length: > 0 } user) Console.WriteLine(Loc.T("service.stUser", user));
        if (show.GetValueOrDefault("WorkingDirectory") is { Length: > 0 } dir) Console.WriteLine(Loc.T("service.stDir", dir));
        Console.WriteLine(Loc.T("service.stFile", show.GetValueOrDefault("FragmentPath") ?? SystemdUnit.PathFor(unit)));
        if (int.TryParse(show.GetValueOrDefault("NRestarts"), out var restarts) && restarts > 0)
            Console.WriteLine(Loc.T("service.stRestarts", restarts));
        Console.WriteLine(Loc.T("service.stJournal", unit));
        return Commands.Ok;
    }

    // ---- общее

    /// <summary>Жив ли процесс: зомби (уже вышел, ждёт, пока его подберут) — не жив.</summary>
    private static bool Alive(int pid)
    {
        if (pid <= 0) return false;
        var stat = ReadText($"/proc/{pid}/stat");
        var close = stat.LastIndexOf(')'); // имя процесса в скобках может содержать что угодно
        return close > 0 && close + 2 < stat.Length && stat[close + 2] is not ('Z' or 'X');
    }

    private static int MainPid(IReadOnlyDictionary<string, string> show) =>
        int.TryParse(show.GetValueOrDefault("MainPID"), out var pid) ? pid : 0;

    /// <summary>Свойства службы из systemctl show; пусто — systemctl не ответил (его ошибка уже в stderr).</summary>
    private static Dictionary<string, string> Show(string unit)
    {
        var (code, output, error) = UnixAccounts.Run("systemctl", "show", unit + ".service", "--property=" +
            "LoadState,UnitFileState,ActiveState,SubState,Result,MainPID,User,FragmentPath,WorkingDirectory,NRestarts,ActiveEnterTimestamp");
        var result = new Dictionary<string, string>();
        if (code != 0)
        {
            Console.Error.WriteLine(Loc.T("service.systemctlFailed", "show " + unit, error.Trim()));
            return result;
        }
        foreach (var line in output.Split('\n'))
            if (line.IndexOf('=') is > 0 and var eq) result[line[..eq]] = line[(eq + 1)..].Trim();
        return result;
    }

    private static bool Systemctl(params string[] args)
    {
        var (code, _, error) = UnixAccounts.Run("systemctl", args);
        if (code == 0) return true;
        Console.Error.WriteLine(Loc.T("service.systemctlFailed", string.Join(' ', args), error.Trim()));
        return false;
    }

    /// <summary>Путь для команды, которую человек скопирует в терминал: с пробелами и прочим — в одинарных кавычках.</summary>
    private static string Shell(string path) =>
        path.All(c => char.IsAsciiLetterOrDigit(c) || "/._-+:,@".Contains(c)) ? path : "'" + path.Replace("'", @"'\''") + "'";

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
