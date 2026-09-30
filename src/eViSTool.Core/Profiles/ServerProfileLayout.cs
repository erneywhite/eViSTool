namespace eViSTool.Core.Profiles;

/// <summary>
/// Где живут папки данных серверных профилей. Чтобы не плодить папки рядом с VintagestoryData, профили лежат внутри неё:
/// <c>VintagestoryData\ServerProfiles\&lt;имя&gt;</c> — у каждого свои конфиг, мир, данные игроков, настройки модов, бэкапы и журналы,
/// а папка модов по умолчанию общая — <c>VintagestoryData\Mods</c> («дом»).
/// </summary>
public static class ServerProfileLayout
{
    public const string ContainerName = "ServerProfiles";
    private const string ModsName = "Mods";

    /// <summary>«Дом» профиля: для папки внутри ServerProfiles — папка над контейнером, иначе сама папка данных.</summary>
    public static string HomeOf(string dataDir)
    {
        var full = Full(dataDir);
        var parent = Path.GetDirectoryName(full);
        return parent is not null && IsContainer(parent) && Path.GetDirectoryName(parent) is { } home ? home : full;
    }

    /// <summary>Папка находится внутри контейнера ServerProfiles (у неё есть «дом» с общей папкой модов).</summary>
    public static bool IsInContainer(string dataDir) => !Same(HomeOf(dataDir), dataDir);

    /// <summary>Контейнер профилей для «дома» этой папки данных.</summary>
    public static string ContainerFor(string dataDir) => Path.Combine(HomeOf(dataDir), ContainerName);

    /// <summary>Общая папка модов «дома».</summary>
    public static string SharedModsDir(string dataDir) => Path.Combine(HomeOf(dataDir), ModsName);

    /// <summary>Свободная папка для нового профиля: «…\ServerProfiles\имя», при занятости — «имя-2» и т. д.</summary>
    public static string SuggestDir(string dataDir, string name)
    {
        var container = ContainerFor(dataDir);
        var slug = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c)).Trim('_', '.');
        if (slug.Length == 0) slug = "server";
        var candidate = Path.Combine(container, slug);
        for (var n = 2; Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any(); n++)
            candidate = Path.Combine(container, $"{slug}-{n}");
        return candidate;
    }

    /// <summary>Путь лежит внутри контейнера профилей папки данных (туда клонировать можно, хотя это «внутри исходной»).</summary>
    public static bool IsInsideContainerOf(string path, string dataDir)
    {
        var container = Full(Path.Combine(dataDir, ContainerName));
        return Full(path).StartsWith(container + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContainer(string dir) => string.Equals(Path.GetFileName(dir), ContainerName, StringComparison.OrdinalIgnoreCase);
    private static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    private static string Full(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}
