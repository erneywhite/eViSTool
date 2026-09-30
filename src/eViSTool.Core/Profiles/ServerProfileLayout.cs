namespace eViSTool.Core.Profiles;

/// <summary>
/// Где живут папки данных серверных профилей. Чтобы не плодить папки рядом с VintagestoryData, профили лежат внутри неё:
/// <c>VintagestoryData\ServerProfiles\&lt;имя&gt;</c> — у каждого свои конфиг, мир, данные игроков, настройки модов, бэкапы и журналы,
/// а папка модов по умолчанию общая — <c>VintagestoryData\Mods</c> («дом»).
/// </summary>
public static class ServerProfileLayout
{
    public const string ContainerName = "ServerProfiles";
    private static readonly ProfileContainer Container = new(ContainerName, "server");

    /// <summary>«Дом» профиля: для папки внутри ServerProfiles — папка над контейнером, иначе сама папка данных.</summary>
    public static string HomeOf(string dataDir) => Container.HomeOf(dataDir);

    /// <summary>Папка находится внутри контейнера ServerProfiles (у неё есть «дом» с общей папкой модов).</summary>
    public static bool IsInContainer(string dataDir) => Container.Contains(dataDir);

    /// <summary>Контейнер профилей для «дома» этой папки данных.</summary>
    public static string ContainerFor(string dataDir) => Container.For(dataDir);

    /// <summary>Общая папка модов «дома».</summary>
    public static string SharedModsDir(string dataDir) => Path.Combine(HomeOf(dataDir), "Mods");

    /// <summary>Свободная папка для нового профиля: «…\ServerProfiles\имя», при занятости — «имя-2» и т. д.</summary>
    public static string SuggestDir(string dataDir, string name) => Container.SuggestDir(dataDir, name);

    /// <summary>Путь лежит внутри контейнера профилей папки данных (туда клонировать можно, хотя это «внутри исходной»).</summary>
    public static bool IsInsideContainerOf(string path, string dataDir) => Container.IsInsideContainerOf(path, dataDir);
}

/// <summary>
/// Где живут папки данных клиентских профилей — по тому же принципу: <c>VintagestoryData\ClientProfiles\&lt;имя&gt;</c>.
/// У профиля свои настройки игры, настройки модов и миры; игра запускается с ним через <c>--dataPath</c>.
/// </summary>
public static class ClientProfileLayout
{
    public const string ContainerName = "ClientProfiles";
    private static readonly ProfileContainer Container = new(ContainerName, "client");

    /// <summary>«Дом» профиля: для папки внутри ClientProfiles — папка над контейнером, иначе сама папка данных.</summary>
    public static string HomeOf(string dataDir) => Container.HomeOf(dataDir);

    public static bool IsInContainer(string dataDir) => Container.Contains(dataDir);

    /// <summary>Свободная папка для нового профиля: «…\ClientProfiles\имя», при занятости — «имя-2» и т. д.</summary>
    public static string SuggestDir(string dataDir, string name) => Container.SuggestDir(dataDir, name);

    public static bool IsInsideContainerOf(string path, string dataDir) => Container.IsInsideContainerOf(path, dataDir);
}

/// <summary>Контейнер папок профилей внутри «домашней» папки данных.</summary>
internal sealed class ProfileContainer(string name, string fallbackFolder)
{
    public string HomeOf(string dataDir)
    {
        var full = Full(dataDir);
        var parent = Path.GetDirectoryName(full);
        return parent is not null && IsContainer(parent) && Path.GetDirectoryName(parent) is { } home ? home : full;
    }

    public bool Contains(string dataDir) => !string.Equals(HomeOf(dataDir), Full(dataDir), StringComparison.OrdinalIgnoreCase);

    public string For(string dataDir) => Path.Combine(HomeOf(dataDir), name);

    public string SuggestDir(string dataDir, string profileName)
    {
        var container = For(dataDir);
        // имя папки — как название профиля (пробелы остаются); заменяем только то, что в имени файла недопустимо
        var slug = string.Concat(profileName.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim(' ', '_', '.');
        if (slug.Length == 0) slug = fallbackFolder;
        var candidate = Path.Combine(container, slug);
        for (var n = 2; Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any(); n++)
            candidate = Path.Combine(container, $"{slug}-{n}");
        return candidate;
    }

    public bool IsInsideContainerOf(string path, string dataDir)
    {
        var container = Full(Path.Combine(dataDir, name));
        return Full(path).StartsWith(container + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsContainer(string dir) => string.Equals(Path.GetFileName(dir), name, StringComparison.OrdinalIgnoreCase);
    private static string Full(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}
