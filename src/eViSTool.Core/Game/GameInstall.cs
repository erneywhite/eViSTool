using System.Diagnostics;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Game;

/// <summary>Сведения об установленной игре или сервере.</summary>
public static class GameInstall
{
    // сначала сервер: на виртуалке клиента может не быть
    private static readonly string[] VersionedFiles =
        ["VintagestoryServer.exe", "Vintagestory.exe", "VintagestoryServer.dll", "Vintagestory.dll"];

    /// <summary>Версия игры по FileVersion исполняемого файла (VintagestoryAPI.dll не годится — там 1.22.0 на всю ветку).</summary>
    public static ModVersion? DetectVersion(string gameDir)
    {
        foreach (var name in VersionedFiles)
        {
            var path = Path.Combine(gameDir, name);
            if (!File.Exists(path)) continue;
            var v = FileVersionInfo.GetVersionInfo(path);
            var text = v.ProductVersion ?? v.FileVersion;
            if (ModVersion.TryParse(text, out var version)) return version;
        }
        return null;
    }

    /// <summary>Папка данных клиента по умолчанию: %APPDATA%\VintagestoryData.</summary>
    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VintagestoryData");

    public static string DefaultModsDir => Path.Combine(DefaultDataDir, "Mods");

    /// <summary>Ищет игру в стандартных местах установки. null — не нашли, пусть укажут вручную.</summary>
    public static string? FindGameDir()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vintagestory"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vintagestory"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vintage Story"),
        ];
        return candidates.FirstOrDefault(d => DetectVersion(d) is not null);
    }
}
