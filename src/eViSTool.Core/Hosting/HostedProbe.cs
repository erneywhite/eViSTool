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
    /// <summary>
    /// Вход → возможности сервера → раскладка → конфиг → версия → число модов (только по листингу, архивы не читаются).
    /// Своё соединение, не из пула профиля: профиля ещё может не быть, а настройки в окне — ещё не сохранены.
    /// </summary>
    public static async Task<HostedCheckResult> CheckAsync(HostedEndpoint endpoint, HostedConnection settings, CancellationToken ct)
    {
        var files = RemoteFilesFactory.Create(endpoint);
        await using (files.ConfigureAwait(false))
        {
            await files.ConnectAsync(ct).ConfigureAwait(false);
            var snapshot = await HostedLayoutProbe.ReadAsync(files, settings, ct).ConfigureAwait(false);
            var (version, source) = await HostedGameVersion.DetectAsync(files, settings, snapshot, null, ct).ConfigureAwait(false);
            var count = 0;
            foreach (var dir in snapshot.Layout.ModDirs)
                count += (await snapshot.Listings.ListAsync(dir, ct).ConfigureAwait(false) ?? []).Count(LooksLikeMod);
            return new HostedCheckResult(snapshot.Layout, version, source, count, files.Features);
        }
    }

    /// <summary>Похоже на мод по листингу: архив, код (кроме базовых dll игры) или папка.</summary>
    private static bool LooksLikeMod(RemoteEntry e)
    {
        if (e.IsDirectory) return true;
        var ext = Path.GetExtension(e.Name);
        return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
               || (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
               && !HostedLayoutProbe.IsBaseGameFile(e.Name);
    }

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
