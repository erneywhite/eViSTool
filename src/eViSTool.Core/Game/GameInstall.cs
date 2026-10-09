using System.Diagnostics;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Game;

/// <summary>Сведения об установленной игре или сервере.</summary>
public static class GameInstall
{
    // сначала сервер: на виртуалке клиента может не быть. На Linux exe нет — версию даёт dll (FileVersionInfo читает
    // её из метаданных сборки)
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
            // на Linux у сборки без InformationalVersion ProductVersion — это AssemblyVersion («1.22.7.0»), а Windows
            // показывает «1.22.7» из FileVersion. Совпадают по значению — берём текст FileVersion, как на Windows
            if (!OperatingSystem.IsWindows() && ModVersion.TryParse(text, out var product)
                && ModVersion.TryParse(v.FileVersion, out var file) && product.Equals(file))
                text = v.FileVersion;
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
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        (string Root, string Name)[] candidates =
        [
            (appData, "Vintagestory"),
            (programFiles, "Vintagestory"),
            (programFiles, "Vintage Story"),
        ];
        // на Linux Program Files нет (пустая строка) — без проверки вышла бы папка относительно текущей
        return candidates.Where(c => c.Root.Length > 0).Select(c => Path.Combine(c.Root, c.Name))
            .FirstOrDefault(d => DetectVersion(d) is not null);
    }
}
