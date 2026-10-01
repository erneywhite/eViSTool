using System.Globalization;
using System.Text.RegularExpressions;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Server;

/// <summary>Резервная копия мира в папке Backups.</summary>
public sealed record BackupFile(string Path, string Name, DateTime Time, long Size)
{
    /// <summary>В имени есть отметка времени «…-2026-09-30_17-51-32.vcdbs» — время копии берётся из неё.</summary>
    public bool IsStamped { get; init; }

    /// <summary>
    /// Копия этого профиля, сделанная eViSTool («&lt;профиль&gt;-&lt;время&gt;.vcdbs»): только такие удаляет ротация.
    /// Копии с другим именем (от другого профиля, прежние «default-…», положенные руками) не трогаются.
    /// </summary>
    public bool IsOwn { get; init; }

    /// <summary>Размер архива с данными модов рядом с копией («….mods.zip»); 0 — архива нет (копия старая или модам нечего хранить).</summary>
    public long ModDataSize { get; init; }
}

/// <summary>
/// Папка Backups серверного профиля. Копии называются «&lt;профиль&gt;-ГГГГ-ММ-ДД_ЧЧ-ММ-СС.vcdbs»: по имени видно, чей это мир,
/// даже если файл унесли в другую папку. Без имени профиля (prefix = null) «своей» считается любая копия с отметкой времени.
/// </summary>
public sealed partial class BackupStore(string dataDir, string? prefix = null)
{
    private const string Extension = ".vcdbs";
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    public string Dir { get; } = Path.Combine(dataDir, "Backups");

    /// <summary>
    /// Имя профиля для имени файла: команда сервера /genbackup не принимает пробелы, а в имени файла нельзя ещё ряд знаков —
    /// всё такое становится «_». Пустое имя — «world».
    /// </summary>
    public static string Slug(string? profileName)
    {
        var slug = string.Concat((profileName ?? "").Trim().Select(c => char.IsWhiteSpace(c) || System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))
            .Trim('_', '.');
        return slug.Length == 0 ? "world" : slug;
    }

    /// <summary>Имя файла для копии, сделанной сейчас.</summary>
    public string NameFor(DateTime now) => $"{prefix ?? "world"}-{now.ToString(StampFormat, CultureInfo.InvariantCulture)}{Extension}";

    /// <summary>Копии, новые сверху.</summary>
    public IReadOnlyList<BackupFile> List()
    {
        if (!Directory.Exists(Dir)) return [];
        return new DirectoryInfo(Dir).EnumerateFiles("*" + Extension)
            .Select(f => TryStamp(f.Name, out var time)
                ? new BackupFile(f.FullName, f.Name, time, f.Length) { IsStamped = true, IsOwn = IsOwnName(f.Name), ModDataSize = ModDataSizeOf(f.FullName) }
                : new BackupFile(f.FullName, f.Name, f.LastWriteTime, f.Length) { ModDataSize = ModDataSizeOf(f.FullName) })
            .OrderByDescending(b => b.Time)
            .ToList();
    }

    /// <summary>
    /// Оставить keep последних своих копий, остальные удалить (насовсем — это и есть ротация).
    /// keep ≤ 0 — ничего не удалять. Возвращает удалённые.
    /// </summary>
    public IReadOnlyList<BackupFile> Prune(int keep)
    {
        if (keep <= 0) return [];
        var removed = new List<BackupFile>();
        foreach (var old in List().Where(b => b.IsOwn).Skip(keep))
        {
            try
            {
                File.Delete(old.Path);
                File.Delete(WorldModData.ArchiveFor(old.Path)); // данные модов этой копии — вместе с ней
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
        var name = NameFor(now);
        var target = Path.Combine(Dir, name);
        File.Copy(saveFile, target, overwrite: false);
        var made = new BackupFile(target, name, now, new FileInfo(target).Length) { IsStamped = true, IsOwn = true };
        return made with { ModDataSize = PackModData(made) };
    }

    /// <summary>
    /// Упаковать данные модов рядом с копией мира (Saves без файлов миров и ModData — см. <see cref="WorldModData"/>).
    /// Возвращает размер архива, 0 — модам нечего хранить.
    /// </summary>
    public long PackModData(BackupFile backup) =>
        WorldModData.Pack(dataDir, WorldModData.ArchiveFor(backup.Path)) is { } archive ? new FileInfo(archive).Length : 0;

    private static long ModDataSizeOf(string backupPath) =>
        new FileInfo(WorldModData.ArchiveFor(backupPath)) is { Exists: true } f ? f.Length : 0;

    /// <summary>
    /// Восстановить мир из копии — только при ОСТАНОВЛЕННОМ сервере. Текущий мир сначала сохраняется в Backups как
    /// «&lt;профиль&gt;-before-restore-&lt;время&gt;.vcdbs» (вместе с журналом SQLite, если он остался) — восстановление можно откатить;
    /// ротация такой файл не удаляет. Затем копия встаёт на место файла мира, а остатки журнала убираются:
    /// они от прежнего мира и испортили бы восстановленный.
    /// Данные модов (Saves без миров, ModData) возвращаются из архива копии, если он есть, — текущие перед этим
    /// упаковываются рядом со страховочной копией. У старой копии без архива данные модов остаются как есть.
    /// Возвращает страховочную копию (null — файла мира не было).
    /// </summary>
    public BackupFile? Restore(BackupFile backup, string saveFile, DateTime now)
    {
        var source = new FileInfo(backup.Path);
        if (!source.Exists) throw new FileNotFoundException(Loc.T("backup.noSave", backup.Path), backup.Path);
        if (source.Length == 0) throw new InvalidOperationException(Loc.T("backup.empty", backup.Name));

        BackupFile? safety = null;
        var safetyName = $"{prefix ?? "world"}-{BeforeRestore}-{now.ToString(StampFormat, CultureInfo.InvariantCulture)}{Extension}";
        var modArchive = WorldModData.ArchiveFor(backup.Path);
        if (File.Exists(modArchive))
            WorldModData.Pack(dataDir, WorldModData.ArchiveFor(Path.Combine(Dir, safetyName))); // текущие данные модов — в сторону
        if (File.Exists(saveFile))
        {
            Directory.CreateDirectory(Dir);
            var name = safetyName;
            var target = Path.Combine(Dir, name);
            File.Copy(saveFile, target, overwrite: false);
            foreach (var tail in JournalTails)
                if (File.Exists(saveFile + tail)) File.Copy(saveFile + tail, target + tail, overwrite: true);
            safety = new BackupFile(target, name, now, new FileInfo(target).Length) { IsStamped = true };
        }

        // сначала рядом, потом подмена: оборванное копирование не оставит на месте мира половину файла
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(saveFile)!);
        var tmp = saveFile + ".evistool.restore";
        File.Copy(backup.Path, tmp, overwrite: true);
        File.Move(tmp, saveFile, overwrite: true);
        foreach (var tail in JournalTails) File.Delete(saveFile + tail);
        if (File.Exists(modArchive)) WorldModData.Restore(dataDir, modArchive);
        return safety;
    }

    /// <summary>
    /// Дождаться копии, которую делает сервер (/genbackup): файл с этим именем (или, без имени, — любой новый, которого
    /// не было в <paramref name="before"/>) появился, сервер его дописал и отпустил. Не по строке «Backup complete!»:
    /// её сервер пишет на своём языке («Резервное копирование завершено!»). null — не дождались.
    /// </summary>
    public async Task<BackupFile?> WaitForAsync(string? name, IReadOnlySet<string> before, TimeSpan timeout,
        CancellationToken ct = default, TimeSpan? poll = null)
    {
        var until = DateTime.UtcNow + timeout;
        long lastSize = -1;
        while (DateTime.UtcNow < until)
        {
            var file = name is not null ? Find(name) : List().FirstOrDefault(b => !before.Contains(b.Name));
            // дописан — размер перестал меняться и файл никто не держит открытым
            if (file is not null && file.Size == lastSize && file.Size > 0 && !IsOpenElsewhere(file.Path)) return file;
            lastSize = file?.Size ?? -1;
            await Task.Delay(poll ?? TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        return null;
    }

    private static bool IsOpenElsewhere(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Сервер взялся за копию: «Handling Console Command /genbackup [имя]». Эту строку сервер пишет по-английски
    /// при любом языке; имя — если его задали.
    /// </summary>
    public static bool IsBackupCommand(string line, out string? name)
    {
        const string marker = "Handling Console Command /genbackup";
        name = null;
        var at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return false;
        var rest = line[(at + marker.Length)..].Trim();
        if (rest.Length > 0) name = rest;
        return true;
    }

    /// <summary>Часть имени страховочной копии, сделанной перед восстановлением.</summary>
    public const string BeforeRestore = "before-restore";

    private static readonly string[] JournalTails = ["-wal", "-shm"];

    /// <summary>Файл копии по имени (null — такого нет).</summary>
    public BackupFile? Find(string name) => List().FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool IsOwnName(string name) =>
        prefix is null || (name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)
                           && name.Length == prefix.Length + 1 + StampFormat.Length + Extension.Length);

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
    private bool _copiedHere; // копию делали при этом планировщике — «никто не заходил» отсчитано от неё
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

    /// <summary>
    /// Нужна ли внеочередная копия (перед перезапуском по расписанию). Не нужна, только если включено «только когда
    /// играли», прошлую копию делали при нас и с тех пор никто не заходил — она была бы той же самой и лишь вытеснила
    /// бы из ротации более старую.
    /// </summary>
    public bool NeedsCopy(ServerAutomation settings) => !settings.BackupOnlyWhenPlayed || _played || !_copiedHere;

    /// <summary>Копия запущена.</summary>
    public void MarkDone(DateTime now, int playersOnline)
    {
        _copiedHere = true;
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
