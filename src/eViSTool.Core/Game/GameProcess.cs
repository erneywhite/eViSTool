using System.ComponentModel;
using System.Diagnostics;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Game;

/// <summary>Процесс сервера VS: его файл (null — не узнать) и папка данных из --dataPath (null — параметра нет).</summary>
public sealed record ServerProcess(int Pid, string? ServerFile, string? DataPath);

/// <summary>Запущена ли игра/сервер этого профиля.</summary>
public static class GameProcess
{
    /// <summary>Процессы сервера VS из папки игры профиля (включая запущенные не нами).</summary>
    public static IReadOnlyList<int> FindServerPids(GameProfile profile) =>
        FindServers(profile.GameDir).Select(s => s.Pid).ToList();

    /// <summary>
    /// Серверы VS из этой папки игры (null — из любой), запущенные кем угодно. Сервер, чей файл не узнать (процесс другого
    /// пользователя), тоже попадает в список: лучше показать, чем пропустить. На Windows сервер — процесс
    /// VintagestoryServer; на Linux — dotnet с VintagestoryServer.dll в аргументах (так запускают server.sh и мы)
    /// или запускатель VintagestoryServer.
    /// </summary>
    public static IReadOnlyList<ServerProcess> FindServers(string? gameDir)
    {
        var result = new List<ServerProcess>();
        var dir = string.IsNullOrWhiteSpace(gameDir) ? null : Path.GetFullPath(gameDir);
        string[] names = OperatingSystem.IsWindows() ? [ServerExecutable.ProcessName] : ["dotnet", ServerExecutable.ProcessName];
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (Inspect(p, name) is not { } server) continue;
                    if (dir is null || server.ServerFile is null
                        || server.ServerFile.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                        result.Add(server);
                }
            }
        }
        return result;
    }

    /// <summary>Сервер ли это и откуда; null — процесс dotnet, но с другим приложением.</summary>
    private static ServerProcess? Inspect(Process p, string name)
    {
        var args = ProcessCommandLine.GetArgs(p.Id);
        var cwd = ProcessCommandLine.WorkingDirectory(p.Id);
        string? file;
        IReadOnlyList<string> serverArgs;
        if (name == "dotnet")
        {
            if (args is null) return null; // не узнать, что запущено в dotnet, — это может быть что угодно
            if (DotnetServer(args, cwd) is not { } parsed) return null;
            (file, serverArgs) = parsed;
        }
        else
        {
            try { file = p.MainModule?.FileName; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
            {
                file = null; // другой пользователь/с правами админа — путь не узнать
            }
            serverArgs = args is { Count: > 0 } ? args.Skip(1).ToList() : [];
        }
        return new ServerProcess(p.Id, file, DataPathFrom(serverArgs, cwd));
    }

    // параметры самого dotnet со значением: «dotnet exec --runtimeconfig x.json app.dll» и т. п.
    private static readonly HashSet<string> DotnetOptionsWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "--additionalprobingpath", "--additional-deps", "--depsfile", "--runtimeconfig", "--fx-version", "--roll-forward",
        "--roll-forward-on-no-candidate-fx",
    };

    /// <summary>
    /// Командная строка dotnet: «dotnet [exec] [параметры dotnet] VintagestoryServer.dll [аргументы сервера]». Сервер —
    /// если первое после параметров dotnet — VintagestoryServer.dll. Относительный путь (server.sh запускает из папки игры)
    /// считается от рабочей папки процесса; её не узнать — файл неизвестен (null). Не сервер — null.
    /// </summary>
    public static (string? ServerFile, IReadOnlyList<string> Args)? DotnetServer(IReadOnlyList<string> argv, string? cwd)
    {
        for (var i = 1; i < argv.Count; i++)
        {
            var a = argv[i];
            if (a == "exec") continue;
            if (DotnetOptionsWithValue.Contains(a)) { i++; continue; }
            if (a.StartsWith('-')) continue;
            if (!string.Equals(Path.GetFileName(a), ServerExecutable.DllName, StringComparison.OrdinalIgnoreCase)) return null;
            string? file = Path.IsPathRooted(a) ? Path.GetFullPath(a) : cwd is null ? null : Path.GetFullPath(a, cwd);
            return (file, argv.Skip(i + 1).ToList());
        }
        return null;
    }

    /// <summary>--dataPath из аргументов сервера; относительный — от рабочей папки процесса, если она известна.</summary>
    private static string? DataPathFrom(IReadOnlyList<string> serverArgs, string? cwd)
    {
        var data = ProcessCommandLine.Argument(serverArgs, "--dataPath");
        if (string.IsNullOrEmpty(data) || Path.IsPathRooted(data) || cwd is null) return data;
        return Path.GetFullPath(data, cwd);
    }

    /// <summary>
    /// Процессы игры (клиента) этого профиля: из его папки игры и с его папкой данных. Копии игры с другими папками
    /// данных (другие профили, запущенные из той же папки игры) сюда не попадают.
    /// </summary>
    public static IReadOnlyList<int> FindClientPids(GameProfile profile)
    {
        var result = new List<int>();
        var wantData = GameLauncher.DataPathFor(profile);
        foreach (var p in Process.GetProcessesByName("Vintagestory"))
        {
            using (p)
            {
                string? exe;
                try { exe = p.MainModule?.FileName; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { exe = null; }
                if (exe is not null && !string.IsNullOrWhiteSpace(profile.GameDir)
                    && !exe.StartsWith(Path.GetFullPath(profile.GameDir), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ProcessCommandLine.GetArgs(p.Id) is not { } args)
                {
                    result.Add(p.Id); // командную строку не узнать — лучше считать своим, чем пропустить
                    continue;
                }
                if (SameDataPath(ProcessCommandLine.Argument(args, "--dataPath"), wantData)) result.Add(p.Id);
            }
        }
        return result;
    }

    /// <summary>Папка данных из --dataPath совпадает с папкой профиля (null — стандартная папка, параметра нет).</summary>
    public static bool SameDataPath(string? arg, string? profileDataPath)
    {
        static string? Norm(string? d) => string.IsNullOrWhiteSpace(d) ? null : Path.GetFullPath(d).TrimEnd('\\', '/');
        var a = Norm(arg);
        if (a is not null && string.Equals(a, Norm(GameInstall.DefaultDataDir), StringComparison.OrdinalIgnoreCase)) a = null;
        return string.Equals(a, Norm(profileDataPath), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRunning(GameProfile profile) =>
        profile.Kind == ProfileKind.Client ? FindClientPids(profile).Count > 0 : FindServerPids(profile).Count > 0;
}
