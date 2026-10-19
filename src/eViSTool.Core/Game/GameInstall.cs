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

    /// <summary>Папка данных клиента по умолчанию: %APPDATA%\VintagestoryData, на Linux — ~/.config/VintagestoryData.</summary>
    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VintagestoryData");

    public static string DefaultModsDir => Path.Combine(DefaultDataDir, "Mods");

    /// <summary>Игра из Flatpak (Linux): тот же архив игры внутри, но запуск — через flatpak run, данные — в его папке.</summary>
    public const string FlatpakAppId = "at.vintagestory.VintageStory";

    // путь «current/active» не меняется при обновлениях Flatpak, в отличие от папки с хэшем версии, куда он ведёт
    private static readonly string FlatpakGameSubdir = Path.Combine("app", FlatpakAppId, "current", "active", "files", "extra", "vintagestory");

    /// <summary>Где лежит игра из Flatpak: установка для пользователя (--user) и для всей системы.</summary>
    public static IEnumerable<string> FlatpakGameDirs()
    {
        if (OperatingSystem.IsWindows()) yield break;
        if (Platform.XdgDirs.DataHome() is { } home) yield return Path.Combine(home, "flatpak", FlatpakGameSubdir);
        yield return Path.Combine("/var/lib/flatpak", FlatpakGameSubdir);
    }

    /// <summary>Папка данных игры из Flatpak: ~/.var/app/&lt;id&gt;/config/VintagestoryData.</summary>
    public static string FlatpakDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".var", "app", FlatpakAppId, "config", "VintagestoryData");

    /// <summary>Папка игры — из Flatpak (её запускают через flatpak run, а не напрямую).</summary>
    public static bool IsFlatpak(string? gameDir) =>
        !OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(gameDir)
        && Path.GetFullPath(gameDir).Contains($"/flatpak/app/{FlatpakAppId}/", StringComparison.Ordinal);

    /// <summary>Стандартная папка данных для этой папки игры: у Flatpak она своя.</summary>
    public static string DefaultDataDirFor(string? gameDir) => IsFlatpak(gameDir) ? FlatpakDataDir : DefaultDataDir;

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
        // на Linux Program Files нет (пустая строка) — без проверки вышла бы папка относительно текущей;
        // Flatpak — последним: архив, поставленный рядом, человек скорее всего и выбрал основным
        return candidates.Where(c => c.Root.Length > 0).Select(c => Path.Combine(c.Root, c.Name))
            .Concat(RegistryInstallDirs())
            .Concat(LinuxArchiveDirs())
            .Concat(FlatpakGameDirs())
            .FirstOrDefault(d => DetectVersion(d) is not null);
    }

    /// <summary>
    /// Где искать игру из архива на Linux. Первым — папка из ярлыка, который пишет install.sh
    /// (~/.local/share/applications/Vintagestory.desktop, строка Path=): так найдётся, куда бы её ни поставили.
    /// Дальше — куда ставит сам install.sh и куда архив обычно распаковывают руками.
    /// </summary>
    public static IEnumerable<string> LinuxArchiveDirs()
    {
        if (OperatingSystem.IsWindows()) yield break;
        var data = Platform.XdgDirs.DataHome();
        if (data is not null)
        {
            var shortcut = Path.Combine(data, "applications", "Vintagestory.desktop");
            string? text = null;
            try { if (File.Exists(shortcut)) text = File.ReadAllText(shortcut); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (text is not null && GameDirFromShortcut(text) is { } fromShortcut) yield return fromShortcut;
            yield return Path.Combine(data, "vintagestory");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0)
        {
            yield return Path.Combine(home, "Games", "vintagestory");
            yield return Path.Combine(home, "Games", "Vintagestory");
            yield return Path.Combine(home, "vintagestory");
        }
        yield return "/opt/vintagestory";
    }

    /// <summary>
    /// Где игра по записи установщика в реестре Windows (список «Установка и удаление программ»): установщик Vintage Story
    /// пишет туда InstallLocation, а ставят игру куда угодно — на другой диск, в свою папку. Записи смотрим у пользователя
    /// и у всей системы (64- и 32-битная ветка); на других системах — пусто. Реестр читается, только когда до этих папок
    /// дошла очередь: игра нашлась в стандартном месте — FindGameDir его не трогает (сотни записей, в том числе в потоке окна).
    /// </summary>
    public static IEnumerable<string> RegistryInstallDirs()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var dir in GameDirsFromUninstallEntries(ReadUninstallEntries())) yield return dir;
    }

    /// <summary>
    /// Папки игры из записей «Установка и удаление программ»: только «Vintage Story…»; папка — InstallLocation, а без
    /// неё — папка значка (DisplayIcon вида «"S:\Games\Vintagestory\VintageStory.exe",0»). Без повторов.
    /// </summary>
    public static IEnumerable<string> GameDirsFromUninstallEntries(IEnumerable<UninstallEntry> entries) => entries
        .Where(e => e.Name?.TrimStart().StartsWith("Vintage Story", StringComparison.OrdinalIgnoreCase) == true)
        .Select(e => !string.IsNullOrWhiteSpace(e.Location) ? e.Location.Trim().Trim('"') : DirOfIcon(e.Icon))
        .Where(d => !string.IsNullOrWhiteSpace(d))
        .Select(d => d!.TrimEnd('\\', '/'))
        .Where(d => d.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string? DirOfIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var path = icon.Trim();
        if (path.StartsWith('"') && path.IndexOf('"', 1) is var end and > 0) path = path[1..end];
        else if (path.LastIndexOf(',') is var comma and > 0) path = path[..comma]; // «…\VintageStory.exe,0»
        // путь из реестра Windows — разделитель «\»; Path.GetDirectoryName на других системах его не видит (а разбор
        // проверяется тестами и там), поэтому папка — всё до последнего разделителя
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut > 0 ? path[..cut] : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<UninstallEntry> ReadUninstallEntries()
    {
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        var entries = new List<UninstallEntry>();
        (Microsoft.Win32.RegistryHive Hive, Microsoft.Win32.RegistryView View)[] roots =
        [
            (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default),
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
        ];
        foreach (var (hive, view) in roots)
        {
            try
            {
                using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                using var list = root.OpenSubKey(uninstall);
                if (list is null) continue;
                foreach (var name in list.GetSubKeyNames())
                {
                    // запись со своим запретом доступа (бывает у антивирусов, корпоративных агентов) — без неё, а не без
                    // всех, что идут после: Vintage Story по алфавиту ближе к концу
                    try
                    {
                        using var app = list.OpenSubKey(name);
                        if (app?.GetValue("DisplayName") is not string display) continue;
                        entries.Add(new UninstallEntry(display, app.GetValue("InstallLocation") as string, app.GetValue("DisplayIcon") as string));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            // реестр недоступен или ветки нет (32-битная на чистой системе) — просто без неё
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return entries;
    }

    /// <summary>Папка игры из ярлыка install.sh: строка Path= в разделе [Desktop Entry]; нет её — null.</summary>
    public static string? GameDirFromShortcut(string desktopEntry)
    {
        var inEntry = false;
        foreach (var raw in desktopEntry.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inEntry = line == "[Desktop Entry]"; continue; }
            if (inEntry && line.StartsWith("Path=", StringComparison.Ordinal) && line.Length > 5)
                return line[5..].Trim();
        }
        return null;
    }
}

/// <summary>Запись из списка «Установка и удаление программ» Windows: имя, папка установки, значок.</summary>
public sealed record UninstallEntry(string? Name, string? Location, string? Icon);
