using eViSTool.Core.Localization;
using Microsoft.Data.Sqlite;

namespace eViSTool.Core.Server;

/// <summary>
/// Файл мира — база SQLite в режиме WAL: после нештатной остановки сервера часть подтверждённых изменений лежит
/// не в самом .vcdbs, а в журнале «-wal» рядом. Копия мира должна быть одним цельным файлом — журнал сводится внутрь.
/// </summary>
public static class WorldDb
{
    /// <summary>Временная папка рядом с назначением, где журнал сводится в копию: «&lt;назначение&gt;.evistool-db».</summary>
    public const string WorkSuffix = ".evistool-db";

    /// <summary>Копия пишется под этим окончанием и получает своё имя, только когда записана целиком.</summary>
    public const string PartSuffix = ".part";

    /// <summary>Запас места сверх размера мира и журнала.</summary>
    private const long Margin = 64L * 1024 * 1024;

    /// <summary>Места под копию (мир и журнал) не хватит — понятная ошибка до начала, а не обрезанный файл посреди.</summary>
    private static void EnsureSpace(string source, string target)
    {
        long free;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(target));
            if (string.IsNullOrEmpty(root)) return;
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return; // место не узнать (сетевой диск) — пробуем как есть
        }
        var need = new FileInfo(source).Length + (new FileInfo(source + "-wal") is { Exists: true } wal ? wal.Length : 0);
        if (free < need + Margin)
            throw new IOException(Loc.T("backup.noSpaceCopy", Path.GetPathRoot(Path.GetFullPath(target)) ?? "",
                (need + Margin) / (1024 * 1024), free / (1024 * 1024)));
    }

    /// <summary>Рядом с файлом мира непустой журнал SQLite.</summary>
    public static bool HasJournal(string file) => new FileInfo(file + "-wal") is { Exists: true, Length: > 0 };

    /// <summary>
    /// Цельная копия мира в <paramref name="target"/> (его не должно быть). Без журнала — просто копия файла.
    /// С журналом — исходник и журнал копируются во временную папку рядом с назначением, SQLite переносит журнал
    /// в базу (checkpoint), и уже цельный файл встаёт на место. Сам мир при этом не открывается и не меняется.
    /// </summary>
    public static void Snapshot(string source, string target)
    {
        if (File.Exists(target)) throw new IOException(Loc.T("backup.exists", Path.GetFileName(target)));
        EnsureSpace(source, target);
        if (!HasJournal(source))
        {
            // во временное имя и переименованием: оборванная копия (кончилось место) не остаётся под именем копии —
            // иначе она считалась самой свежей, и ротация ради неё удаляла старые целые
            var part = target + PartSuffix;
            try
            {
                File.Copy(source, part, overwrite: true);
                File.Move(part, target, overwrite: false);
            }
            finally
            {
                try { if (File.Exists(part)) File.Delete(part); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return;
        }

        var work = Directory.CreateDirectory(target + WorkSuffix).FullName;
        try
        {
            var db = Path.Combine(work, "world.vcdbs");
            File.Copy(source, db);
            File.Copy(source + "-wal", db + "-wal");
            Checkpoint(db);
            File.Move(db, target, overwrite: false);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Перенести журнал в базу и убрать его: после этого файл самодостаточен.</summary>
    private static void Checkpoint(string db)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        try
        {
            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var r = cmd.ExecuteReader();
            // (busy, страниц в журнале, перенесено): busy ≠ 0 — журнал перенесён не весь
            if (!r.Read() || r.GetInt32(0) != 0 || r.GetInt32(1) != r.GetInt32(2))
                throw new InvalidOperationException(Loc.T("backup.journalFailed", "checkpoint"));
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(Loc.T("backup.journalFailed", ex.Message), ex);
        }
    }
}
