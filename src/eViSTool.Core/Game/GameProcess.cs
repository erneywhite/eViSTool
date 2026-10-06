using System.ComponentModel;
using System.Diagnostics;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Game;

/// <summary>Запущена ли игра/сервер этого профиля.</summary>
public static class GameProcess
{
    /// <summary>Процессы сервера VS из папки игры профиля (включая запущенные не нами).</summary>
    public static IReadOnlyList<int> FindServerPids(GameProfile profile)
    {
        var result = new List<int>();
        foreach (var p in Process.GetProcessesByName("VintagestoryServer"))
        {
            using (p)
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(profile.GameDir) || exe is null
                        || exe.StartsWith(Path.GetFullPath(profile.GameDir), StringComparison.OrdinalIgnoreCase))
                        result.Add(p.Id);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    result.Add(p.Id); // путь не узнать (другой пользователь) — лучше показать, чем пропустить
                }
            }
        }
        return result;
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
                if (ProcessCommandLine.Get(p.Id) is not { } cmd)
                {
                    result.Add(p.Id); // командную строку не узнать — лучше считать своим, чем пропустить
                    continue;
                }
                if (SameDataPath(ProcessCommandLine.Argument(cmd, "--dataPath"), wantData)) result.Add(p.Id);
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

    public static bool IsRunning(GameProfile profile)
    {
        if (profile.Kind == ProfileKind.Client) return FindClientPids(profile).Count > 0;
        var name = "VintagestoryServer";
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                if (string.IsNullOrWhiteSpace(profile.GameDir)) return true;
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (exe is null || exe.StartsWith(Path.GetFullPath(profile.GameDir), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // процесс другого пользователя/с правами админа — путь не узнать, считаем что наш
                    return true;
                }
            }
        }
        return false;
    }
}
