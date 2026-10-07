using System.Text.RegularExpressions;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Profiles;

/// <summary>Насколько смело убирать: «безопасно» — игра или eViSTool создадут заново; «осторожно» — пропадёт что-то нужное.</summary>
public enum CleanupSafety { Safe, Careful }

/// <summary>Что убирать — по виду (подписи — в окне).</summary>
public enum CleanupKind
{
    /// <summary>Cache\unpack — моды, распакованные игрой; при запуске распакует заново.</summary>
    UnpackedMods,
    /// <summary>ModsByServer\&lt;сервер&gt; — моды, скачанные с серверов; скачаются заново при входе.</summary>
    ServerMods,
    /// <summary>Logs\Archive — логи прошлых запусков.</summary>
    OldLogs,
    /// <summary>Maps\&lt;мир&gt;*.db — разведанная карта мира (одиночного или сетевого).</summary>
    WorldMaps,
    /// <summary>ModData\&lt;папка мода&gt; — данные мода, которого в профиле нет.</summary>
    RemovedModData,
    /// <summary>ModData\&lt;id мира&gt; — данные модов по мирам.</summary>
    WorldModData,
    /// <summary>ModConfig — файлы настроек, у которых не нашлось мода (скорее всего, мод удалён).</summary>
    OrphanConfigs,
    /// <summary>Сохранённые eViSTool версии модов, которых в профиле уже нет.</summary>
    RemovedModVersions,
    /// <summary>Сохранённые eViSTool прежние версии установленных модов (без них откат недоступен).</summary>
    InstalledModVersions,
    /// <summary>Прежние версии конфигов модов (без них «Вернуть предыдущую версию» недоступна).</summary>
    ConfigVersions,
    /// <summary>Недокачанные или забытые загрузки eViSTool.</summary>
    Downloads,
}

/// <summary>Один пункт уборки: файл или папка (или несколько файлов одного мира), размер, когда менялся, выбран ли по умолчанию.</summary>
public sealed record CleanupItem(CleanupKind Kind, string Title, IReadOnlyList<string> Paths, long Size, DateTime ChangedUtc, bool Default);

/// <summary>Вид уборки со своими пунктами.</summary>
public sealed record CleanupGroup(CleanupKind Kind, CleanupSafety Safety, IReadOnlyList<CleanupItem> Items)
{
    public long Size => Items.Sum(i => i.Size);
}

/// <summary>
/// Уборка диска профиля: что копит игра (распакованные моды, моды серверов, карты, логи, данные модов) и eViSTool
/// (версии модов и конфигов, загрузки). Только поиск и удаление — что выбирать, решает пользователь; по умолчанию
/// отмечено лишь то, что создастся заново само или точно больше не нужно. Удаление — через переданный способ (корзина).
/// </summary>
public static partial class DiskCleanup
{
    /// <summary>Моды сервера, на который не заходили дольше этого, отмечены по умолчанию.</summary>
    public static readonly TimeSpan StaleServer = TimeSpan.FromDays(30);

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex WorldId();

    /// <summary>
    /// Что можно убрать в профиле. <paramref name="mods"/> — моды профиля (чьи данные и версии ещё нужны),
    /// <paramref name="appRoot"/> — данные eViSTool (версии модов и конфигов этого профиля, загрузки).
    /// </summary>
    public static IReadOnlyList<CleanupGroup> Scan(GameProfile profile, string dataDir, IReadOnlyList<LocalMod> mods, string appRoot, DateTime nowUtc)
    {
        var installed = mods.Where(m => m.Info is not null).Select(m => m.Info!.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = new List<CleanupGroup>();
        void Add(CleanupKind kind, CleanupSafety safety, IEnumerable<CleanupItem> items)
        {
            var list = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
            if (list.Count > 0) groups.Add(new CleanupGroup(kind, safety, list));
        }

        // --- что копит игра
        Add(CleanupKind.UnpackedMods, CleanupSafety.Safe, Folder(CleanupKind.UnpackedMods, Path.Combine(dataDir, "Cache", "unpack"), "Cache\\unpack", true));
        Add(CleanupKind.ServerMods, CleanupSafety.Safe, Subfolders(Path.Combine(dataDir, "ModsByServer"))
            .Select(d => Item(CleanupKind.ServerMods, ServerTitle(Path.GetFileName(d)), [d], dflt: nowUtc - Changed(d) > StaleServer)));
        Add(CleanupKind.OldLogs, CleanupSafety.Safe, Folder(CleanupKind.OldLogs, Path.Combine(dataDir, "Logs", "Archive"), "Logs\\Archive", true));
        Add(CleanupKind.WorldMaps, CleanupSafety.Careful, Maps(Path.Combine(dataDir, "Maps")));

        var modData = Subfolders(Path.Combine(dataDir, "ModData")).ToList();
        Add(CleanupKind.RemovedModData, CleanupSafety.Careful, modData
            .Where(d => !WorldId().IsMatch(Path.GetFileName(d)) && !IsInstalled(Path.GetFileName(d), installed))
            .Select(d => Item(CleanupKind.RemovedModData, Path.GetFileName(d), [d], dflt: false)));
        Add(CleanupKind.WorldModData, CleanupSafety.Careful, modData
            .Where(d => WorldId().IsMatch(Path.GetFileName(d)))
            .Select(d => Item(CleanupKind.WorldModData, Path.GetFileName(d), [d], dflt: false)));

        if (Directory.Exists(ModConfigs.DirFor(dataDir)))
            Add(CleanupKind.OrphanConfigs, CleanupSafety.Careful, ModConfigs.List(dataDir, mods)
                .Where(f => f.ModId is null)
                .Select(f => Item(CleanupKind.OrphanConfigs, f.RelativePath, [f.Path], dflt: false)));

        // --- что копит eViSTool для этого профиля
        var versions = Subfolders(Path.Combine(appRoot, "ModBackups", profile.Id)).ToList();
        Add(CleanupKind.RemovedModVersions, CleanupSafety.Safe, versions
            .Where(d => !installed.Contains(Path.GetFileName(d)))
            .Select(d => Item(CleanupKind.RemovedModVersions, Path.GetFileName(d), [d], dflt: true)));
        Add(CleanupKind.InstalledModVersions, CleanupSafety.Careful, versions
            .Where(d => installed.Contains(Path.GetFileName(d)))
            .Select(d => Item(CleanupKind.InstalledModVersions, Path.GetFileName(d), [d], dflt: false)));
        Add(CleanupKind.ConfigVersions, CleanupSafety.Careful,
            Folder(CleanupKind.ConfigVersions, Path.Combine(appRoot, "ModConfigBackups", profile.Id), "ModConfigBackups", false));
        Add(CleanupKind.Downloads, CleanupSafety.Safe, Directory.Exists(Path.Combine(appRoot, "Downloads"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(appRoot, "Downloads"))
                .Select(p => Item(CleanupKind.Downloads, Path.GetFileName(p), [p], dflt: true))
            : []);
        return groups;
    }

    /// <summary>Папка данных мода — его ли (modid или начинается с него: «distantvistas» ↔ «distantvistas»).</summary>
    private static bool IsInstalled(string folder, HashSet<string> installed)
    {
        var f = new string(folder.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return installed.Any(id => f == id || (id.Length >= 4 && (f.StartsWith(id, StringComparison.Ordinal) || id.StartsWith(f, StringComparison.Ordinal))));
    }

    /// <summary>«178.172.138.142-42420» → «178.172.138.142:42420».</summary>
    private static string ServerTitle(string folder)
    {
        var dash = folder.LastIndexOf('-');
        return dash > 0 && int.TryParse(folder[(dash + 1)..], out _) ? folder[..dash] + ":" + folder[(dash + 1)..] : folder;
    }

    /// <summary>Карты: файлы одного мира («id.db», «id-geology.db», …) — одним пунктом.</summary>
    private static IEnumerable<CleanupItem> Maps(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var g in Directory.EnumerateFiles(dir).GroupBy(f =>
                 {
                     var name = Path.GetFileNameWithoutExtension(f);
                     var m = WorldId().Match(name);
                     return m.Success ? m.Value : name.Split('-')[0];
                 }, StringComparer.OrdinalIgnoreCase))
            yield return Item(CleanupKind.WorldMaps, g.Key, [.. g], dflt: false);
    }

    private static IEnumerable<CleanupItem> Folder(CleanupKind kind, string dir, string title, bool dflt) =>
        Directory.Exists(dir) ? [Item(kind, title, [dir], dflt)] : [];

    private static IEnumerable<string> Subfolders(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateDirectories(dir) : [];

    private static CleanupItem Item(CleanupKind kind, string title, IReadOnlyList<string> paths, bool dflt) =>
        new(kind, title, paths, paths.Sum(SizeOf), paths.Select(Changed).DefaultIfEmpty(DateTime.MinValue).Max(), dflt);

    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0,
            }).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Когда менялось: у папки — самый свежий файл в ней (время самой папки не меняется при правке файлов внутри).</summary>
    private static DateTime Changed(string path)
    {
        try
        {
            if (File.Exists(path)) return File.GetLastWriteTimeUtc(path);
            if (!Directory.Exists(path)) return DateTime.MinValue;
            DateTime? latest = null;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                if (latest is null || f.LastWriteTimeUtc > latest) latest = f.LastWriteTimeUtc;
            return latest ?? Directory.GetLastWriteTimeUtc(path); // пустая папка — по её собственному времени
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Убрать выбранное (<paramref name="remove"/> — обычно в корзину). Возвращает освобождённое и ошибки по пунктам:
    /// сбой одного пункта не мешает остальным. Пути проверяются ещё раз: убираются только существующие.
    /// </summary>
    public static (long Freed, IReadOnlyList<string> Problems) Remove(IEnumerable<CleanupItem> items, Action<string> remove)
    {
        long freed = 0;
        var problems = new List<string>();
        foreach (var item in items)
            foreach (var path in item.Paths.Where(p => File.Exists(p) || Directory.Exists(p)))
            {
                var size = SizeOf(path);
                try
                {
                    remove(path);
                    freed += size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        return (freed, problems);
    }
}
