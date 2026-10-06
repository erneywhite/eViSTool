using eViSTool.Core.Localization;
using Newtonsoft.Json;

namespace eViSTool.Core.Server;

/// <summary>Точки вмешательства для тестов: свободное место, «обрыв» перед шагом подмены.</summary>
public sealed record RestoreHooks
{
    /// <summary>Свободное место на диске папки (по умолчанию — <see cref="DriveInfo"/>).</summary>
    public Func<string, long>? FreeSpace { get; init; }

    /// <summary>Вызывается перед каждым переносом подмены (номер с нуля).</summary>
    public Action<int>? BeforeMove { get; init; }

    /// <summary>Исключение — «процесс убит»: отката не будет, его сделает <see cref="WorldRestore.Recover"/>.</summary>
    public Func<Exception, bool>? IsCrash { get; init; }
}

/// <summary>
/// Подмена мира и данных модов при восстановлении — всё или ничего.
/// 1) Подготовка: мир из копии и данные модов из архива целиком раскладываются во временной папке
///    «&lt;данные&gt;/.evistool-restore» (рядом с рабочими — на том же диске) и проверяются; рабочие файлы не тронуты.
/// 2) Подмена переименованиями: текущие файлы — в сторону, подготовленные — на их место. Каждый перенос сначала
///    записывается в журнал шагов; любой сбой — все переносы возвращаются обратно.
/// 3) Успех — отложенное старое удаляется. Обрыв посередине (процесс убит, выключили свет) откатывает
///    <see cref="Recover"/> при следующем восстановлении, запуске сервера или открытии копий.
/// </summary>
public static class WorldRestore
{
    public const string StageName = ".evistool-restore";
    private const string LockName = "restore.lock";
    private const string JournalName = "restore.json";
    private const string OldSuffix = ".evistool.old";
    private const string NewSuffix = ".evistool.restore";
    private const long Margin = 64L * 1024 * 1024;
    private static readonly string[] JournalTails = ["-wal", "-shm"];

    public static string StageFor(string dataDir) => Path.Combine(dataDir, StageName);

    private sealed class Journal
    {
        public bool Swapping { get; set; }
        public bool Done { get; set; }
        public string? NewWorld { get; set; }
        public string? OldWorld { get; set; }
        public List<string[]> Moves { get; set; } = []; // [откуда, куда] — записано до самого переноса
    }

    /// <summary>
    /// Поставить мир <paramref name="worldSource"/> (журнал рядом сводится внутрь) и, если есть, данные модов из
    /// <paramref name="modArchive"/> на место рабочих. При ошибке рабочие файлы остаются прежними.
    /// </summary>
    public static void Run(string dataDir, string saveFile, string worldSource, string? modArchive, RestoreHooks? hooks = null)
    {
        Recover(dataDir);
        var stage = StageFor(dataDir);
        if (Directory.Exists(stage)) throw new IOException(Loc.T("backup.restoreBusy"));
        Directory.CreateDirectory(stage);
        var journalPath = Path.Combine(stage, JournalName);
        var journal = new Journal { NewWorld = saveFile + NewSuffix, OldWorld = saveFile + OldSuffix };
        var crashed = false;
        var keep = false; // откат не удался — временная папка хранит прежние файлы, её не трогаем
        try
        {
            // замок на всё время: другой процесс (окно, агент) не примет идущее восстановление за оборванное
            using (new FileStream(Path.Combine(stage, LockName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                Save(journalPath, journal);
                try
                {
                    Prepare(dataDir, saveFile, worldSource, modArchive, stage, journal, hooks);
                    journal.Swapping = true;
                    Save(journalPath, journal);
                    Swap(dataDir, saveFile, modArchive is not null, stage, journalPath, journal, hooks);
                    journal.Done = true;
                    Save(journalPath, journal);
                }
                catch (Exception ex) when (hooks?.IsCrash?.Invoke(ex) == true)
                {
                    crashed = true;
                    throw;
                }
                catch (Exception ex)
                {
                    if (!Rollback(journal))
                    {
                        keep = true;
                        throw new IOException(Loc.T("backup.rollbackFailed", stage, ex.Message), ex);
                    }
                    // всё вернулось — так и сказать, с понятной причиной (окно и агент ловят IOException)
                    throw new IOException(Loc.T("backup.restoreUndone", ex.Message), ex);
                }
            }
        }
        finally
        {
            if (!crashed && !keep) Cleanup(stage, journal);
        }
    }

    /// <summary>
    /// Довести до конца или откатить оборванное восстановление. false — нечего делать или оно ещё идёт в другом процессе.
    /// </summary>
    public static bool Recover(string dataDir)
    {
        var stage = StageFor(dataDir);
        if (!Directory.Exists(stage)) return false;
        FileStream gate;
        try
        {
            gate = new FileStream(Path.Combine(stage, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return false; // замок держит другой процесс — восстановление идёт прямо сейчас
        }

        Journal? journal;
        using (gate)
        {
            var path = Path.Combine(stage, JournalName);
            journal = File.Exists(path) ? JsonConvert.DeserializeObject<Journal>(File.ReadAllText(path)) : null;
            // подмена не закончена — вернуть прежнее; закончена или не начиналась — только убрать временное
            if (journal is { Swapping: true, Done: false } && !Rollback(journal))
                throw new InvalidOperationException(Loc.T("backup.rollbackFailed", stage, ""));
        }
        Cleanup(stage, journal);
        return true;
    }

    private static void Prepare(string dataDir, string saveFile, string worldSource, string? modArchive, string stage,
        Journal journal, RestoreHooks? hooks)
    {
        var saveDir = Path.GetDirectoryName(Path.GetFullPath(saveFile))!;
        Directory.CreateDirectory(saveDir);

        // место: подготовленный мир — рядом с рабочим, данные модов — во временной папке; старое удаляется только в конце
        var world = new FileInfo(worldSource).Length + (File.Exists(worldSource + "-wal") ? new FileInfo(worldSource + "-wal").Length : 0);
        long mods;
        try
        {
            mods = modArchive is null ? 0 : WorldModData.UnpackedSize(modArchive);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(Loc.T("backup.badArchive", Path.GetFileName(modArchive), ex.Message), ex);
        }
        var needs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Need(string dir, long bytes) => needs[Path.GetPathRoot(Path.GetFullPath(dir))!] = needs.GetValueOrDefault(Path.GetPathRoot(Path.GetFullPath(dir))!) + bytes;
        Need(saveDir, world);
        Need(stage, mods);
        foreach (var (root, bytes) in needs)
        {
            var free = hooks?.FreeSpace?.Invoke(root) ?? new DriveInfo(root).AvailableFreeSpace;
            if (free < bytes + Margin)
                throw new IOException(Loc.T("backup.noSpace", root, (bytes + Margin) / (1024 * 1024), free / (1024 * 1024)));
        }

        File.Delete(journal.NewWorld!);
        WorldDb.Snapshot(worldSource, journal.NewWorld!);
        if (modArchive is null) return;
        try
        {
            WorldModData.ExtractTo(modArchive, Path.Combine(stage, "new"));
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(Loc.T("backup.badArchive", Path.GetFileName(modArchive), ex.Message), ex);
        }
    }

    private static void Swap(string dataDir, string saveFile, bool withMods, string stage, string journalPath, Journal journal,
        RestoreHooks? hooks)
    {
        var step = 0;
        void Move(string from, string to)
        {
            hooks?.BeforeMove?.Invoke(step++);
            journal.Moves.Add([from, to]);
            Save(journalPath, journal);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // обычно — файл держит другая программа; путь — относительно папки данных, чтобы было понятно, что именно
                throw new IOException(Loc.T("backup.busyPath", Path.GetRelativePath(dataDir, from)), ex);
            }
        }

        // в сторону: данные модов (если копия их несёт) и мир с журналом
        if (withMods)
            foreach (var rel in WorldModData.Items(dataDir).ToList())
                Move(Path.Combine(dataDir, rel), Path.Combine(stage, "old", rel));
        foreach (var tail in JournalTails.Prepend(""))
            if (File.Exists(saveFile + tail)) Move(saveFile + tail, journal.OldWorld + tail);

        // на место
        if (withMods)
        {
            var fresh = Path.Combine(stage, "new");
            if (Directory.Exists(fresh))
                foreach (var rel in WorldModData.Items(fresh).ToList())
                    MoveIn(Path.Combine(fresh, rel), Path.Combine(dataDir, rel), Move);
        }
        Move(journal.NewWorld!, saveFile);
    }

    /// <summary>Папка с таким именем осталась (в ней лежит мир) — переносим её содержимое по одному.</summary>
    private static void MoveIn(string from, string to, Action<string, string> move)
    {
        if (Directory.Exists(from) && Directory.Exists(to))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(from).ToList())
                MoveIn(child, Path.Combine(to, Path.GetFileName(child)), move);
            return;
        }
        move(from, to);
    }

    /// <summary>Вернуть всё перенесённое; false — что-то вернуть не удалось (временную папку тогда не удалять).</summary>
    private static bool Rollback(Journal journal)
    {
        var ok = true;
        // в обратном порядке; перенос, который не успел случиться, пропускается
        for (var i = journal.Moves.Count - 1; i >= 0; i--)
        {
            var (from, to) = (journal.Moves[i][0], journal.Moves[i][1]);
            if (File.Exists(from) || Directory.Exists(from)) continue;
            try
            {
                if (Directory.Exists(to)) Directory.Move(to, from);
                else if (File.Exists(to)) File.Move(to, from);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        return ok;
    }

    private static void Cleanup(string stage, Journal? journal)
    {
        if (journal?.NewWorld is { } fresh) Silently(() => File.Delete(fresh));
        if (journal?.OldWorld is { } old)
            foreach (var tail in JournalTails.Prepend(""))
                Silently(() => File.Delete(old + tail));
        Silently(() => Directory.Delete(stage, recursive: true));
    }

    private static void Silently(Action action)
    {
        try { action(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Save(string path, Journal journal)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs))
        {
            w.Write(JsonConvert.SerializeObject(journal));
            w.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
