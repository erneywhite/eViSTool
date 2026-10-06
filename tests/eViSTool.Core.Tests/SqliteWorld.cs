using Microsoft.Data.Sqlite;

namespace eViSTool.Core.Tests;

/// <summary>Настоящие базы SQLite для тестов копий мира.</summary>
internal static class SqliteWorld
{
    /// <summary>
    /// Мир «после вылета сервера»: строки <paramref name="saved"/> уже в файле базы, а <paramref name="pending"/> —
    /// подтверждены, но лежат только в журнале -wal рядом (checkpoint не успел).
    /// </summary>
    public static void WriteCrashed(string path, string[] saved, string[] pending)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-sqlite-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var db = Path.Combine(work, "world.vcdbs");
            using (var conn = Open(db))
            {
                Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE t(v TEXT);");
                foreach (var v in saved) Insert(conn, v);
                Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE);");
                foreach (var v in pending) Insert(conn, v);
                // «вылет»: файлы снимаются, пока соединение открыто, — журнал не перенесён в базу
                CopyShared(db, path);
                CopyShared(db + "-wal", path + "-wal");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>Обычный цельный мир без журнала.</summary>
    public static void WriteClean(string path, params string[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var conn = Open(path))
        {
            Exec(conn, "PRAGMA journal_mode=WAL; CREATE TABLE t(v TEXT);");
            foreach (var v in rows) Insert(conn, v);
        }
        Assert.False(File.Exists(path + "-wal"));
    }

    /// <summary>Строки базы — как их увидит игра, открыв ровно этот файл (журнал рядом, если есть, тоже учитывается).</summary>
    public static string[] Read(string path)
    {
        // читаем копию: само чтение SQLite не должно менять проверяемые файлы
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-sqlite-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var db = Path.Combine(work, "world.vcdbs");
            File.Copy(path, db);
            if (File.Exists(path + "-wal")) File.Copy(path + "-wal", db + "-wal");
            using var conn = Open(db);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM t ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            var rows = new List<string>();
            while (r.Read()) rows.Add(r.GetString(0));
            return [.. rows];
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection conn, string v)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO t(v) VALUES ($v)";
        cmd.Parameters.AddWithValue("$v", v);
        cmd.ExecuteNonQuery();
    }

    private static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = File.Create(to);
        src.CopyTo(dst);
    }
}
