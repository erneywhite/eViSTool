using System.ComponentModel;
using System.Diagnostics;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Game;

/// <summary>Запущена ли игра/сервер этого профиля.</summary>
public static class GameProcess
{
    public static bool IsRunning(GameProfile profile)
    {
        var name = profile.Kind == ProfileKind.Server ? "VintagestoryServer" : "Vintagestory";
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
