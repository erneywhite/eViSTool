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
/// Копии мира серверного профиля. Копии называются «&lt;профиль&gt;-ГГГГ-ММ-ДД_ЧЧ-ММ-СС.vcdbs»: по имени видно, чей это мир,
/// даже если файл унесли в другую папку. Без имени профиля (prefix = null) «своей» считается любая копия с отметкой времени.
/// Сервер пишет копии в Backups своей папки данных (<see cref="ServerDir"/>); если выбрана своя папка (<paramref name="backupDir"/>),
/// готовые копии переносятся туда (<see cref="Relocate"/>). Список, ротация и восстановление видят обе папки: копия, которую
/// не удалось перенести (сетевая папка недоступна), не теряется.
/// </summary>
public sealed partial class BackupStore(string dataDir, string? prefix = null, string? backupDir = null)
{
    private const string Extension = ".vcdbs";
    private const string PartSuffix = ".part";
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>Backups в папке данных сервера: сюда копию пишет сам сервер (/genbackup).</summary>
    public string ServerDir { get; } = Path.Combine(dataDir, "Backups");

    /// <summary>Где копии хранятся: выбранная папка или <see cref="ServerDir"/>.</summary>
    public string Dir { get; } = string.IsNullOrWhiteSpace(backupDir) ? Path.Combine(dataDir, "Backups") : backupDir.Trim();

    /// <summary>Выбрана своя папка, отличная от Backups сервера.</summary>
    public bool IsElsewhere => !SamePath(Dir, ServerDir);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Можно ли складывать копии в эту папку: путь полный (диск или сетевая папка), папка создаётся, в неё пишется.
    /// null — можно, иначе — что не так, своими словами.
    /// </summary>
    public static string? CheckDir(string? dir)
    {
        dir = dir?.Trim();
        if (string.IsNullOrEmpty(dir)) return null; // по умолчанию — Backups сервера
        if (!Path.IsPathFullyQualified(dir) || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return Loc.T(OperatingSystem.IsWindows() ? "backupdir.notFull" : "backupdir.notFullLinux");
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".evistool-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ex is UnauthorizedAccessException ? Loc.T("backupdir.denied") : Loc.T("backupdir.unreachable", ex.Message);
        }
    }

    /// <summary>
    /// Перенести готовую копию (и архив данных модов рядом) из Backups сервера в выбранную папку. Сначала файл целиком
    /// пишется под временным именем, потом переименовывается и только тогда удаляется исходный: оборвётся связь посреди —
    /// копия остаётся на сервере. Своя папка не выбрана или копия уже там — возвращается как есть.
    /// </summary>
    public BackupFile Relocate(BackupFile backup)
    {
        if (!IsElsewhere || !SamePath(Path.GetDirectoryName(backup.Path)!, ServerDir)) return backup;
        Directory.CreateDirectory(Dir);
        var target = Path.Combine(Dir, backup.Name);
        var archive = WorldModData.ArchiveFor(backup.Path);
        if (File.Exists(archive)) MoveSafely(archive, WorldModData.ArchiveFor(target));
        MoveSafely(backup.Path, target);
        return backup with { Path = target };
    }

    /// <summary>
    /// Свои копии, застрявшие в Backups сервера (выбранная папка была недоступна), — перенести. Возвращает, сколько
    /// перенесли; не вышло — исключение, остальное остаётся на месте.
    /// </summary>
    public int RelocateLeftovers()
    {
        if (!IsElsewhere) return 0;
        var moved = 0;
        foreach (var b in ListIn(ServerDir).Where(b => b.IsOwn))
        {
            Relocate(b);
            moved++;
        }
        return moved;
    }

    private static void MoveSafely(string from, string to)
    {
        var part = to + PartSuffix;
        File.Copy(from, part, overwrite: true);
        File.Move(part, to, overwrite: true);
        File.Delete(from);
    }

    /// <summary>
    /// Имя профиля для имени файла: команда сервера /genbackup не принимает пробелы, а в имени файла нельзя ещё ряд знаков —
    /// всё такое становится «_». Знаки — по правилам Windows и на Linux: копия переносится между машинами и в сетевые
    /// папки, а окно и агент из одного названия получают одно имя. Пустое имя — «world».
    /// </summary>
    public static string Slug(string? profileName)
    {
        var slug = string.Concat((profileName ?? "").Trim().Select(c => char.IsWhiteSpace(c) || PathRules.IsBadInFileName(c) ? '_' : c))
            .Trim('_', '.');
        return slug.Length == 0 ? "world" : slug;
    }

    /// <summary>Имя файла для копии, сделанной сейчас.</summary>
    public string NameFor(DateTime now) => $"{prefix ?? "world"}-{now.ToString(StampFormat, CultureInfo.InvariantCulture)}{Extension}";

    /// <summary>Копии, новые сверху: из выбранной папки и из Backups сервера (одноимённая — та, что в выбранной).</summary>
    public IReadOnlyList<BackupFile> List()
    {
        var all = ListIn(Dir);
        if (IsElsewhere)
        {
            var names = all.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            all = [.. all, .. ListIn(ServerDir).Where(b => !names.Contains(b.Name))];
        }
        return all.OrderByDescending(b => b.Time).ToList();
    }

    private List<BackupFile> ListIn(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return [];
            return new DirectoryInfo(dir).EnumerateFiles("*" + Extension)
                .Select(f => TryStamp(f.Name, out var time)
                    ? new BackupFile(f.FullName, f.Name, time, f.Length) { IsStamped = true, IsOwn = IsOwnName(f.Name), ModDataSize = ModDataSizeOf(f.FullName) }
                    : new BackupFile(f.FullName, f.Name, f.LastWriteTime, f.Length) { ModDataSize = ModDataSizeOf(f.FullName) })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return []; // сетевая папка недоступна — показываем то, что есть на сервере
        }
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
    /// Если рядом с сохранением остался непустой журнал SQLite (-wal) — сервер завершился нештатно, — журнал сводится
    /// в копию: она получается одним цельным файлом (см. <see cref="WorldDb"/>).
    /// </summary>
    public BackupFile CopySave(string saveFile, DateTime now)
    {
        if (!File.Exists(saveFile)) throw new FileNotFoundException(Loc.T("backup.noSave", saveFile), saveFile);
        var dir = Dir;
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dir = ServerDir; // выбранная папка недоступна — копия всё равно нужна; перенесётся потом
            Directory.CreateDirectory(dir);
        }
        var name = NameFor(now);
        var target = Path.Combine(dir, name);
        // после нештатной остановки часть мира — в журнале -wal: копия получает его внутрь, одним файлом
        WorldDb.Snapshot(saveFile, target);
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
    /// «&lt;профиль&gt;-before-restore-&lt;время&gt;.vcdbs» (журнал SQLite, если он остался, сведён внутрь) — восстановление
    /// можно откатить; ротация такой файл не удаляет. Затем копия встаёт на место файла мира (журнал старой страховочной
    /// копии, лежащий рядом, тоже сводится внутрь), а остатки журнала прежнего мира убираются — они испортили бы восстановленный.
    /// Данные модов (Saves без миров, ModData) возвращаются из архива копии, если он есть, — текущие перед этим
    /// упаковываются рядом со страховочной копией. У старой копии без архива данные модов остаются как есть.
    /// Всё или ничего — см. <see cref="WorldRestore"/>: при сбое рабочие файлы остаются прежними.
    /// Возвращает страховочную копию (null — файла мира не было).
    /// </summary>
    public BackupFile? Restore(BackupFile backup, string saveFile, DateTime now, RestoreHooks? hooks = null)
    {
        var source = new FileInfo(backup.Path);
        if (!source.Exists) throw new FileNotFoundException(Loc.T("backup.noSave", backup.Path), backup.Path);
        if (source.Length == 0) throw new InvalidOperationException(Loc.T("backup.empty", backup.Name));

        WorldRestore.Recover(dataDir); // прошлое восстановление оборвалось — сначала вернуть как было

        BackupFile? safety = null;
        var safetyName = $"{prefix ?? "world"}-{BeforeRestore}-{now.ToString(StampFormat, CultureInfo.InvariantCulture)}{Extension}";
        var safetyPath = Path.Combine(ServerDir, safetyName); // рядом с миром: быстрее и не зависит от сети
        var modArchive = WorldModData.ArchiveFor(backup.Path);
        var withMods = File.Exists(modArchive);
        try
        {
            if (withMods) WorldModData.Pack(dataDir, WorldModData.ArchiveFor(safetyPath)); // текущие данные модов — в сторону
            if (File.Exists(saveFile))
            {
                Directory.CreateDirectory(ServerDir);
                WorldDb.Snapshot(saveFile, safetyPath); // прежний мир вместе с его журналом — одним цельным файлом
                safety = new BackupFile(safetyPath, safetyName, now, new FileInfo(safetyPath).Length) { IsStamped = true };
            }

            // всё или ничего: подготовка во временной папке, подмена с возвратом при сбое (журнал копии сводится внутрь)
            WorldRestore.Run(dataDir, saveFile, backup.Path, withMods ? modArchive : null, hooks);
            return safety;
        }
        catch
        {
            // рабочие файлы остались прежними — страховочная копия не нужна и только путала бы список
            // (если временная папка осталась — возврат не удался или процесс оборвался, и страховочная копия как раз нужна;
            // проверка — здесь, а не фильтром catch: фильтр выполняется раньше, чем WorldRestore уберёт за собой)
            if (!Directory.Exists(WorldRestore.StageFor(dataDir)))
                foreach (var file in new[] { safetyPath, WorldModData.ArchiveFor(safetyPath) })
                    try { File.Delete(file); } catch (IOException) { }
            throw;
        }
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


    /// <summary>Файл копии по имени (null — такого нет).</summary>
    public BackupFile? Find(string name) => List().FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool IsOwnName(string name) =>
        prefix is null || (name.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)
                           && name.Length == prefix.Length + 1 + StampFormat.Length + Extension.Length);

    private static bool TryStamp(string name, out DateTime time)
    {
        time = default;
        return Stamp().Match(name) is { Success: true } m
               && DateTime.TryParseExact(m.Groups[1].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out time);
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
