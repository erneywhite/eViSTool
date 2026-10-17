using eViSTool.Core.Versioning;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Где на сервере у хостера что лежит. Пути абсолютные через «/». <paramref name="DataDir"/> — папка, где нашёлся
/// конфиг; <paramref name="Warnings"/> — уже на языке окна.
/// </summary>
public sealed record HostedLayout(string Root, string? ConfigPath, string? DataDir, string? GameDir,
    IReadOnlyList<string> ModDirs, string? InstallDir, IReadOnlyList<string> Warnings);

/// <summary>Откуда взялась версия игры сервера — в порядке, в котором источники проверяются.</summary>
public enum GameVersionSource { Manual, VersionFile, LastLaunch, ServerLog, ServerDll }

/// <summary>Итог «Проверить подключение»: раскладка, версия игры и её источник, число модов (только по листингу).</summary>
public sealed record HostedCheckResult(HostedLayout Layout, ModVersion? GameVersion, GameVersionSource? VersionSource, int ModCount,
    RemoteFeatures Features);

/// <summary>Проверка подключения из окна «Подключение по FTP» и закрепление найденных путей в профиле.</summary>
public static class HostedProbe
{
    /// <summary>Вход → возможности сервера → раскладка → конфиг → версия → число модов.</summary>
    public static Task<HostedCheckResult> CheckAsync(HostedEndpoint endpoint, HostedConnection settings, CancellationToken ct) =>
        // shortcut: заглушка до пакета P3; ошибка ловится фильтрами окна, в отличие от NotImplementedException
        Task.FromException<HostedCheckResult>(new RemoteFilesException(RemoteFailure.Unsupported, "HostedProbe.CheckAsync is not implemented yet"));

    /// <summary>
    /// Копия настроек с закреплёнными путями из раскладки (относительно <see cref="HostedConnection.Root"/>): следующие
    /// чтения их не ищут. Путь вне корня не закрепляется — его поищут заново.
    /// </summary>
    public static HostedConnection Pin(HostedConnection settings, HostedLayout layout)
    {
        var pinned = settings.Clone();
        var root = settings.Root;
        string? Rel(string? path)
        {
            if (path is null || !RemotePath.IsInside(root, path)) return null;
            return RemotePath.Relative(root, path);
        }
        pinned.ConfigPath = Rel(layout.ConfigPath);
        pinned.GameDir = Rel(layout.GameDir);
        pinned.InstallDir = Rel(layout.InstallDir);
        var dirs = layout.ModDirs.Select(Rel).OfType<string>().ToList();
        pinned.ModDirs = dirs.Count > 0 ? dirs : null;
        return pinned;
    }
}
