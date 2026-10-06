using eViSTool.Core.Localization;
using Microsoft.Data.Sqlite;

namespace eViSTool.Core.Server;

/// <summary>
/// Файл мира — база SQLite в режиме WAL: после нештатной остановки сервера часть подтверждённых изменений лежит
/// не в самом .vcdbs, а в журнале «-wal» рядом. Копия мира должна быть одним цельным файлом — журнал сводится внутрь.
/// </summary>
public static class WorldDb
{
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
        if (!HasJournal(source))
        {
            File.Copy(source, target, overwrite: false);
            return;
        }

        var work = Directory.CreateDirectory(target + ".evistool-db").FullName;
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
