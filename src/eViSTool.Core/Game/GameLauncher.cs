using System.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Game;

/// <summary>
/// Запуск клиента игры для профиля. У профиля со своей папкой данных игра получает <c>--dataPath</c> — свои моды,
/// настройки и миры; у профиля со стандартной папкой (%APPDATA%\VintagestoryData) запускается как обычно, без параметров.
/// </summary>
public static class GameLauncher
{
    public const string ClientExeName = "Vintagestory.exe";

    public static string ClientExe(GameProfile profile) => Path.Combine(profile.GameDir ?? "", ClientExeName);

    /// <summary>С чем запускать игру для профиля. Бросает, если профиль не клиентский или игры в его папке нет.</summary>
    public static ProcessStartInfo StartInfo(GameProfile profile)
    {
        if (profile.Kind != ProfileKind.Client) throw new InvalidOperationException(Loc.T("play.notClient"));
        var exe = ClientExe(profile);
        if (string.IsNullOrWhiteSpace(profile.GameDir) || !File.Exists(exe))
            throw new FileNotFoundException(Loc.T("play.noExe", ClientExeName, profile.GameDir ?? "—"), exe);

        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!, UseShellExecute = false };
        if (DataPathFor(profile) is { } dataPath)
        {
            psi.ArgumentList.Add("--dataPath");
            psi.ArgumentList.Add(dataPath);
        }
        return psi;
    }

    /// <summary>Папка для <c>--dataPath</c>; null — папка стандартная (или не задана), параметр не нужен.</summary>
    public static string? DataPathFor(GameProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.DataDir)) return null;
        var dir = Normalize(profile.DataDir);
        return string.Equals(dir, Normalize(GameInstall.DefaultDataDir), StringComparison.OrdinalIgnoreCase) ? null : dir;
    }

    /// <summary>Запустить игру. Возвращает PID; игра живёт сама по себе — закрытие eViSTool её не трогает.</summary>
    public static int Launch(GameProfile profile)
    {
        using var process = Process.Start(StartInfo(profile)) ?? throw new InvalidOperationException(Loc.T("play.startFailed"));
        return process.Id;
    }

    private static string Normalize(string dir)
    {
        var full = Path.GetFullPath(dir);
        return Path.GetPathRoot(full) == full ? full : full.TrimEnd('\\', '/');
    }
}
