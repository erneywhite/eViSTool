using eViSTool.Core.Localization;
using eViSTool.Core.Server;

namespace eViSTool.Core.Profiles;

/// <summary>Копия одиночного мира: какой мир, где копия, когда снята и сколько весит.</summary>
public sealed record WorldCopy(string WorldName, string WorldPath, string CopyPath, DateTime CopiedAt, long Size)
{
    /// <summary>Это не копия «перед изменением модов», а мир, отложенный перед возвратом копии (чтобы возврат можно было отменить).</summary>
    public bool IsBeforeRestore => CopyPath.EndsWith(WorldCopies.BeforeRestoreSuffix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Копии одиночных миров клиентского профиля перед изменением модов: обновление мода может испортить мир
/// (пропавшие блоки и предметы), а откат мода его уже не вернёт.
/// По одной копии на мир — в «&lt;папка данных&gt;\eViSTool-world-copies», рядом с мирами, на том же диске.
/// Копия обновляется, только если мир изменился с прошлой копии (в него играли), — иначе не трогаем.
/// Сам мир не открывается и не меняется; журнал SQLite сводится в копию (<see cref="WorldDb.Snapshot"/>).
/// </summary>
public static class WorldCopies
{
    public const string FolderName = "eViSTool-world-copies";
    public const string BeforeRestoreSuffix = ".before-restore";
    private const string WorldExt = ".vcdbs";
    private const long Margin = 64L * 1024 * 1024;

    public static string DirFor(string dataDir) => Path.Combine(dataDir, FolderName);

    /// <summary>Одиночные миры профиля: файлы .vcdbs в папке Saves.</summary>
    public static IReadOnlyList<string> Worlds(string dataDir)
    {
        var saves = Path.Combine(dataDir, "Saves");
        return Directory.Exists(saves)
            ? Directory.EnumerateFiles(saves, "*" + WorldExt).Where(f => f.EndsWith(WorldExt, StringComparison.OrdinalIgnoreCase)).Order().ToList()
            : [];
    }

    /// <summary>
    /// Когда мир последний раз менялся: сам файл или его журнал (игра пишет сначала в журнал «-wal»).
    /// </summary>
    public static DateTime ChangedUtc(string world)
    {
        var t = File.GetLastWriteTimeUtc(world);
        var wal = world + "-wal";
        return File.Exists(wal) && File.GetLastWriteTimeUtc(wal) > t ? File.GetLastWriteTimeUtc(wal) : t;
    }

    /// <summary>Мир изменился с прошлой копии (или копии нет).</summary>
    public static bool NeedsCopy(string world, string copy) =>
        !File.Exists(copy) || ChangedUtc(world) > File.GetLastWriteTimeUtc(copy);

    /// <summary>Есть ли что копировать (быстро: только время файлов).</summary>
    public static bool AnyToCopy(string dataDir) =>
        Worlds(dataDir).Any(w => NeedsCopy(w, Path.Combine(DirFor(dataDir), Path.GetFileName(w))));

    /// <summary>
    /// Обновить копии изменившихся миров. Возвращает имена скопированных миров и ошибки по мирам (места нет,
    /// файл занят) — ошибка одного мира не мешает остальным. Игра при этом должна быть закрыта (проверяет вызывающий).
    /// </summary>
    public static (IReadOnlyList<string> Copied, IReadOnlyList<string> Problems) Refresh(string dataDir,
        Func<string, long>? freeSpace = null)
    {
        var copied = new List<string>();
        var problems = new List<string>();
        var dir = DirFor(dataDir);
        foreach (var world in Worlds(dataDir))
        {
            var name = Path.GetFileName(world);
            var copy = Path.Combine(dir, name);
            if (!NeedsCopy(world, copy)) continue;
            try
            {
                Directory.CreateDirectory(dir);
                var size = new FileInfo(world).Length + (File.Exists(world + "-wal") ? new FileInfo(world + "-wal").Length : 0);
                var root = Path.GetPathRoot(Path.GetFullPath(dir))!;
                var free = freeSpace?.Invoke(root) ?? new DriveInfo(root).AvailableFreeSpace;
                if (free < size + Margin)
                    throw new IOException(Loc.T("wcopy.noSpace", (size + Margin) / (1024 * 1024), free / (1024 * 1024)));

                // снимаем рядом и подменяем одним переносом: прежняя копия цела, пока новая не готова
                var stamp = ChangedUtc(world);
                var tmp = copy + ".new";
                if (File.Exists(tmp)) File.Delete(tmp);
                WorldDb.Snapshot(world, tmp);
                File.SetCreationTimeUtc(tmp, DateTime.UtcNow);
                // время копии = время мира, с которого она снята: так видно, менялся ли мир после неё. Ставится
                // последним: на Linux времени создания у файла нет, и .NET записывает его во время изменения
                File.SetLastWriteTimeUtc(tmp, stamp);
                File.Move(tmp, copy, overwrite: true);
                copied.Add(Path.GetFileNameWithoutExtension(name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                problems.Add(Loc.T("wcopy.failed", Path.GetFileNameWithoutExtension(name), ex.Message));
                try { File.Delete(copy + ".new"); } catch (IOException) { }
            }
        }
        return (copied, problems);
    }

    /// <summary>Копии профиля, новые сверху (вместе с мирами, отложенными перед возвратом).</summary>
    public static IReadOnlyList<WorldCopy> List(string dataDir)
    {
        var dir = DirFor(dataDir);
        if (!Directory.Exists(dir)) return [];
        var saves = Path.Combine(dataDir, "Saves");
        return Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(WorldExt, StringComparison.OrdinalIgnoreCase) || f.EndsWith(BeforeRestoreSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var worldFile = f.EndsWith(BeforeRestoreSuffix, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(f)[..^BeforeRestoreSuffix.Length]
                    : Path.GetFileName(f);
                return new WorldCopy(Path.GetFileNameWithoutExtension(worldFile), Path.Combine(saves, worldFile), f,
                    File.GetCreationTimeUtc(f), new FileInfo(f).Length);
            })
            .OrderByDescending(c => c.CopiedAt)
            .ToList();
    }

    /// <summary>
    /// Вернуть мир из копии. Текущий мир (если есть) сначала откладывается в «… .before-restore» — возврат можно
    /// отменить, вернув уже его. Подмена — через <see cref="WorldRestore"/>: всё или ничего. Игра должна быть закрыта.
    /// </summary>
    public static void Restore(string dataDir, WorldCopy copy)
    {
        if (!File.Exists(copy.CopyPath)) throw new FileNotFoundException(Loc.T("wcopy.gone"), copy.CopyPath);
        var aside = Path.Combine(DirFor(dataDir), Path.GetFileName(copy.WorldPath) + BeforeRestoreSuffix);
        // возвращаем отложенный мир — откладывать нечего: им же и затрётся текущий
        if (!copy.IsBeforeRestore && File.Exists(copy.WorldPath))
        {
            var tmp = aside + ".new";
            if (File.Exists(tmp)) File.Delete(tmp);
            WorldDb.Snapshot(copy.WorldPath, tmp);
            File.SetCreationTimeUtc(tmp, DateTime.UtcNow);
            File.Move(tmp, aside, overwrite: true);
        }
        WorldRestore.Run(dataDir, copy.WorldPath, copy.CopyPath, modArchive: null);
        // мир теперь такой же, как копия: следующая копия не нужна, пока в него не поиграют
        File.SetLastWriteTimeUtc(copy.WorldPath, File.GetLastWriteTimeUtc(copy.CopyPath));
    }

    /// <summary>Сколько места занимают копии профиля.</summary>
    public static long TotalSize(string dataDir) => List(dataDir).Sum(c => c.Size);
}
