using System.IO.Compression;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Server;

/// <summary>
/// Данные модов, которые лежат рядом с миром, а не в нём: всё в папке Saves, кроме самих файлов миров
/// (например, Saves/XLeveling — прогресс навыков), и папка ModData профиля (моды кладут туда данные по каждому миру).
/// Копия мира без них восстановила бы мир, а прогресс модов остался бы «из будущего».
/// Архив лежит рядом с копией мира: «&lt;копия&gt;.mods.zip». Playerdata (роли, баны) — настройки сервера, не мира: не трогаем.
/// </summary>
public static class WorldModData
{
    public const string Suffix = ".mods.zip";
    private const string SavesDir = "Saves";
    private const string ModDataDir = "ModData";

    /// <summary>«…/Backups/x-2026-10-01_22-08-00.vcdbs» → «…/Backups/x-2026-10-01_22-08-00.mods.zip».</summary>
    public static string ArchiveFor(string backupPath) => Path.ChangeExtension(backupPath, null) + Suffix;

    /// <summary>Файл мира и его спутники (журнал SQLite, временные файлы восстановления) — это не данные модов.</summary>
    private static bool IsWorldFile(string name) => name.Contains(".vcdbs", StringComparison.OrdinalIgnoreCase);

    /// <summary>Упаковать данные модов профиля. Нечего упаковывать — архив не создаётся, null.</summary>
    public static string? Pack(string dataDir, string archive)
    {
        var files = Collect(dataDir).ToList();
        if (files.Count == 0) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        var tmp = archive + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var (path, name) in files)
            {
                try
                {
                    // сервер может держать файл открытым — читаем, не мешая ему писать
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = File.GetLastWriteTime(path);
                    using var target = entry.Open();
                    source.CopyTo(target);
                }
                catch (FileNotFoundException)
                {
                    // мод успел удалить файл, пока собирали архив
                }
            }
        }
        File.Move(tmp, archive, overwrite: true);
        return archive;
    }

    /// <summary>
    /// Распаковать архив данных модов в папку <paramref name="root"/> (её содержимое — как у папки данных профиля).
    /// Сначала проверяются все пути — пишется только в Saves и ModData, запись «..\..\что-то» из чужого архива
    /// отвергается, — потом читается весь архив: битый обнаружится здесь, до того как рабочие файлы тронуты.
    /// </summary>
    public static void ExtractTo(string archive, string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries) Target(full, entry.FullName);
        foreach (var entry in zip.Entries)
        {
            // файл мира в подпапке Saves (так паковали прежние версии) — не данные модов
            if (entry.FullName.EndsWith('/') || IsWorldFile(entry.Name)) continue;
            var target = Target(full, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>Сколько займут данные модов после распаковки.</summary>
    public static long UnpackedSize(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        return zip.Entries.Sum(e => e.Length);
    }

    /// <summary>
    /// Данные модов профиля крупными кусками — пути относительно папки данных: файлы и папки внутри Saves и ModData.
    /// Папка, где лежит файл мира (на любой глубине), целиком не берётся — только её содержимое без миров.
    /// </summary>
    public static IEnumerable<string> Items(string dataDir)
    {
        foreach (var top in new[] { SavesDir, ModDataDir })
            if (Directory.Exists(Path.Combine(dataDir, top)))
                foreach (var item in Expand(dataDir, top)) yield return item;
    }

    private static IEnumerable<string> Expand(string dataDir, string rel)
    {
        var dir = Path.Combine(dataDir, rel);
        foreach (var file in Directory.EnumerateFiles(dir).Where(f => !IsWorldFile(Path.GetFileName(f))))
            yield return Path.Combine(rel, Path.GetFileName(file));
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var subRel = Path.Combine(rel, Path.GetFileName(sub));
            if (HasWorld(sub)) foreach (var item in Expand(dataDir, subRel)) yield return item;
            else yield return subRel;
        }
    }

    private static bool HasWorld(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(f => IsWorldFile(Path.GetFileName(f)));

    private static string Target(string root, string name)
    {
        var top = name.Split('/', '\\')[0];
        var target = Path.GetFullPath(Path.Combine(root, name));
        var allowed = (top.Equals(SavesDir, StringComparison.OrdinalIgnoreCase) || top.Equals(ModDataDir, StringComparison.OrdinalIgnoreCase))
                      && target.StartsWith(root, PathRules.Comparison) // на Linux «../DATA» — уже не папка данных
                      && !(top.Equals(SavesDir, StringComparison.OrdinalIgnoreCase) && IsWorldFile(Path.GetFileName(target))
                           && Path.GetDirectoryName(target)!.Equals(Path.Combine(root, SavesDir), StringComparison.OrdinalIgnoreCase));
        return allowed ? target : throw new InvalidDataException(Loc.T("backup.badModData", name));
    }

    /// <summary>Что упаковать: (путь, имя в архиве).</summary>
    private static IEnumerable<(string Path, string Name)> Collect(string dataDir)
    {
        var saves = Path.Combine(dataDir, SavesDir);
        if (Directory.Exists(saves))
        {
            foreach (var file in Directory.EnumerateFiles(saves).Where(f => !IsWorldFile(Path.GetFileName(f))))
                yield return (file, $"{SavesDir}/{Path.GetFileName(file)}");
            foreach (var dir in Directory.EnumerateDirectories(saves))
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !IsWorldFile(Path.GetFileName(f))))
                    yield return (file, $"{SavesDir}/{Rel(saves, file)}");
        }
        var modData = Path.Combine(dataDir, ModDataDir);
        if (Directory.Exists(modData))
            foreach (var file in Directory.EnumerateFiles(modData, "*", SearchOption.AllDirectories))
                yield return (file, $"{ModDataDir}/{Rel(modData, file)}");
    }

    private static string Rel(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
}
