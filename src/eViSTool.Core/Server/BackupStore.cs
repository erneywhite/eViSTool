using System.Globalization;
using System.Text.RegularExpressions;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Server;

/// <summary>Резервная копия мира в папке Backups.</summary>
public sealed record BackupFile(string Path, string Name, DateTime Time, long Size)
{
    /// <summary>Имя с отметкой времени «мир-2026-09-30_17-51-32.vcdbs» — такую копию сделал сервер или eViSTool.</summary>
    public bool IsStamped { get; init; }
}

/// <summary>
/// Папка Backups серверного профиля. Копии называются как у самого сервера (команда /genbackup):
/// «&lt;имя сохранения&gt;-ГГГГ-ММ-ДД_ЧЧ-ММ-СС.vcdbs». Ротация трогает только такие файлы — всё, что положили руками
/// под другим именем, остаётся.
/// </summary>
public sealed partial class BackupStore(string dataDir)
{
    private const string Extension = ".vcdbs";
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    public string Dir { get; } = Path.Combine(dataDir, "Backups");

    /// <summary>Копии, новые сверху.</summary>
    public IReadOnlyList<BackupFile> List()
    {
        if (!Directory.Exists(Dir)) return [];
        return new DirectoryInfo(Dir).EnumerateFiles("*" + Extension)
            .Select(f => TryStamp(f.Name, out var time)
                ? new BackupFile(f.FullName, f.Name, time, f.Length) { IsStamped = true }
                : new BackupFile(f.FullName, f.Name, f.LastWriteTime, f.Length))
            .OrderByDescending(b => b.Time)
            .ToList();
    }

    /// <summary>
    /// Оставить keep последних копий с отметкой времени, остальные удалить (насовсем — это и есть ротация).
    /// keep ≤ 0 — ничего не удалять. Возвращает удалённые.
    /// </summary>
    public IReadOnlyList<BackupFile> Prune(int keep)
    {
        if (keep <= 0) return [];
        var removed = new List<BackupFile>();
        foreach (var old in List().Where(b => b.IsStamped).Skip(keep))
        {
            try
            {
                File.Delete(old.Path);
                removed.Add(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // занят — удалим в следующий раз
            }
        }
        return removed;
    }

    /// <summary>
    /// Копия сохранения при ОСТАНОВЛЕННОМ сервере (на работающем копию делает сам сервер — /genbackup).
    /// Если рядом с сохранением остался непустой журнал SQLite (-wal), сервер завершился нештатно и файл мира
    /// может быть неполным — копировать такое как «бэкап» нельзя: InvalidOperationException.
    /// </summary>
    public BackupFile CopySave(string saveFile, DateTime now)
    {
        if (!File.Exists(saveFile)) throw new FileNotFoundException(Loc.T("backup.noSave", saveFile), saveFile);
        var wal = new FileInfo(saveFile + "-wal");
        if (wal.Exists && wal.Length > 0) throw new InvalidOperationException(Loc.T("backup.dirtySave"));

        Directory.CreateDirectory(Dir);
        var name = $"{Path.GetFileNameWithoutExtension(saveFile)}-{now.ToString(StampFormat, CultureInfo.InvariantCulture)}{Extension}";
        var target = Path.Combine(Dir, name);
        File.Copy(saveFile, target, overwrite: false);
        return new BackupFile(target, name, now, new FileInfo(target).Length) { IsStamped = true };
    }

    private static bool TryStamp(string name, out DateTime time)
    {
        time = default;
        return Stamp().Match(name) is { Success: true } m
               && DateTime.TryParseExact(m.Groups[1].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    [GeneratedRegex(@"-(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.vcdbs$", RegexOptions.IgnoreCase)]
    private static partial Regex Stamp();
}

/// <summary>
/// Когда делать очередную копию. Отсчёт — от прошлой копии или от запуска сервера (что позже): после простоя
/// сервер не бросается делать копию сразу. Если включено «только когда играли» и за интервал никто не заходил,
/// копия пропускается, а отсчёт начинается заново.
/// </summary>
public sealed class BackupScheduler
{
    private bool _played;
    private DateTime? _base;

    /// <summary>Время последней копии (сделанной этим планировщиком или найденной на диске при старте).</summary>
    public DateTime? LastBackupAt { get; private set; }

    /// <summary>На диске уже есть копии — отсчитываем от самой свежей.</summary>
    public void Seed(DateTime? lastBackupAt) => LastBackupAt = lastBackupAt;

    /// <summary>Сколько игроков сейчас на сервере (вызывать регулярно): кто-то был — мир менялся.</summary>
    public void NotePlayers(int count)
    {
        if (count > 0) _played = true;
    }

    /// <summary>Когда ждать следующую копию (null — расписание выключено или сервер не работает).</summary>
    public DateTime? NextAt(ServerAutomation settings, ServerState state, DateTime? startedAt) =>
        settings.BackupEnabled && state == ServerState.Running && startedAt is { } started ? Base(started) + settings.BackupInterval : null;

    /// <summary>Пора делать копию.</summary>
    public bool IsDue(ServerAutomation settings, DateTime now, ServerState state, DateTime? startedAt)
    {
        if (NextAt(settings, state, startedAt) is not { } next || now < next) return false;
        if (settings.BackupOnlyWhenPlayed && !_played)
        {
            _base = now; // никто не заходил — пропускаем, ждём следующий интервал
            return false;
        }
        return true;
    }

    /// <summary>Копия запущена.</summary>
    public void MarkDone(DateTime now, int playersOnline)
    {
        LastBackupAt = now;
        _base = now;
        _played = playersOnline > 0; // кто остался на сервере — продолжает менять мир
    }

    private DateTime Base(DateTime startedAt)
    {
        var last = _base is { } b && (LastBackupAt is not { } l || b > l) ? b : LastBackupAt;
        return last is { } t && t > startedAt ? t : startedAt;
    }
}
