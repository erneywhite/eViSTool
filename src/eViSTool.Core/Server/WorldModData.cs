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
    /// Вернуть данные модов из архива: текущие убираются (вызывающий их перед этим упаковал), содержимое архива встаёт на место.
    /// Пишется только в Saves и ModData профиля — запись «..\..\что-то» из чужого архива отвергается.
    /// </summary>
    public static void Restore(string dataDir, string archive)
    {
        var root = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        // сначала проверка всего архива, потом изменения: на битом архиве ничего не сотрём
        foreach (var entry in zip.Entries) Target(root, entry.FullName);

        Clear(dataDir);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var target = Target(root, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string Target(string root, string name)
    {
        var top = name.Split('/', '\\')[0];
        var target = Path.GetFullPath(Path.Combine(root, name));
        var allowed = (top.Equals(SavesDir, StringComparison.OrdinalIgnoreCase) || top.Equals(ModDataDir, StringComparison.OrdinalIgnoreCase))
                      && target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
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
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    yield return (file, $"{SavesDir}/{Rel(saves, file)}");
        }
        var modData = Path.Combine(dataDir, ModDataDir);
        if (Directory.Exists(modData))
            foreach (var file in Directory.EnumerateFiles(modData, "*", SearchOption.AllDirectories))
                yield return (file, $"{ModDataDir}/{Rel(modData, file)}");
    }

    private static string Rel(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>Убрать текущие данные модов: в Saves — всё, кроме файлов миров; ModData — целиком.</summary>
    private static void Clear(string dataDir)
    {
        var saves = Path.Combine(dataDir, SavesDir);
        if (Directory.Exists(saves))
        {
            foreach (var file in Directory.EnumerateFiles(saves).Where(f => !IsWorldFile(Path.GetFileName(f)))) File.Delete(file);
            foreach (var dir in Directory.EnumerateDirectories(saves)) Directory.Delete(dir, recursive: true);
        }
        var modData = Path.Combine(dataDir, ModDataDir);
        if (Directory.Exists(modData)) Directory.Delete(modData, recursive: true);
    }
}
