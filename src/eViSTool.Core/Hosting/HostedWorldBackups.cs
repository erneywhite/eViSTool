using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Server;
using eViSTool.Core.Settings;
using Newtonsoft.Json;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Копия мира сервера у хостера на этом компьютере: «&lt;профиль&gt;-ГГГГ-ММ-ДД_ЧЧ-ММ-СС.vcdbs» (время этой машины), рядом
/// архив настроек и данных модов «….extra.zip» и сведения «….info.json» (откуда скачана).
/// </summary>
/// <param name="Time">Время копии: из отметки в имени, у копии без отметки — время файла.</param>
/// <param name="Size">Размер мира.</param>
public sealed record HostedWorldBackup(string Path, string Name, DateTime Time, long Size)
{
    /// <summary>В имени есть отметка времени.</summary>
    public bool IsStamped { get; init; }

    /// <summary>Копия этого профиля, сделанная eViSTool: только такие удаляет ротация.</summary>
    public bool IsOwn { get; init; }

    /// <summary>Размер архива с настройками и данными модов; 0 — архива нет.</summary>
    public long ExtraSize { get; init; }

    /// <summary>Откуда скачана; null — сведений нет (копию положили руками).</summary>
    public HostedWorldOrigin? Origin { get; init; }

    /// <summary>Путь на FTP, откуда скачана.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Время файла на сервере, как его отдал листинг (MLSD — UTC, LIST — время сервера).</summary>
    public DateTime? SourceTime { get; init; }

    public bool HasExtra => ExtraSize > 0;
}

/// <summary>Итог копии: сама копия, предупреждения (архив не собрался, довели прерванное восстановление) и что убрала ротация.</summary>
public sealed record HostedBackupResult(HostedWorldBackup Backup, IReadOnlyList<string> Warnings, IReadOnlyList<HostedWorldBackup> Pruned);

/// <summary>Как восстанавливать мир на сервер у хостера.</summary>
public sealed record HostedRestoreOptions
{
    /// <summary>Сервер точно остановлен (по логу, <see cref="HostedLogs.IsSurelyDown"/>) — без этого восстановление отказывает.</summary>
    public bool ServerStopped { get; init; }

    /// <summary>Что ещё вернуть из архива рядом с копией. serverconfig.json — только явным флагом.</summary>
    public HostedWorldExtras Extras { get; init; }

    /// <summary>
    /// Оставить прежний мир на сервере как «….evistool.bak» (место у хостера он занимает целиком). Хранится один: bak,
    /// оставленный в прошлый раз, следующее восстановление заменяет.
    /// </summary>
    public bool KeepOldWorld { get; init; }
}

/// <summary>Итог восстановления: куда встал мир, предупреждения, где на сервере оставлен прежний (null — убран).</summary>
public sealed record HostedRestoreResult(string WorldPath, IReadOnlyList<string> Warnings, string? KeptOldWorld, int ExtraFiles);

/// <summary>
/// Копии мира сервера у хостера — на этом компьютере: скачать (сам мир при остановленном сервере, на работающем — свежую
/// целую копию игры из Backups/BackupSaves), список, ротация, удаление, восстановление на сервер. Все обращения к серверу —
/// через очередь соединения профиля (<see cref="HostedSessions"/>); пока идёт скачивание или заливка, остальные операции
/// профиля ждут. Решение «сервер остановлен» принимает вызывающий (по логу) и передаёт сюда.
/// <para>
/// Скачивание идёт в «….part» с докачкой после обрыва (новое соединение, чтение с позиции); если файл на сервере за это
/// время поменялся (размер или время в листинге до и после разные) — копия выбрасывается. Целая копия переименовывается в
/// итоговое имя, отмена и сбой удаляют «….part». Остался журнал SQLite (-wal, сервер упал) — он скачивается тоже и сводится
/// в копию (<see cref="WorldDb"/>): копия — один цельный файл.
/// </para>
/// </summary>
public sealed partial class HostedWorldBackups
{
    public const string Extension = HostedWorldLocator.Extension;
    public const string ExtraSuffix = ".extra.zip";
    public const string InfoSuffix = ".info.json";
    public const string PartSuffix = ".part";
    private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";
    private const long Margin = 64L * 1024 * 1024;

    /// <summary>Сколько обрывов подряд без единого нового байта терпим, прежде чем сдаться.</summary>
    private const int MaxStalls = 3;

    public HostedWorldBackups(ModTarget target)
    {
        Target = target.IsHosted ? target : throw new ArgumentException("not a hosted target", nameof(target));
        Prefix = BackupStore.Slug(target.Name);
        var chosen = Hosted.Settings.BackupDir?.Trim();
        // своя папка — только полным путём (окно проверяет её CheckDir); мусор в настройках не уводит копии в текущую папку
        Dir = !string.IsNullOrEmpty(chosen) && Path.IsPathFullyQualified(chosen) ? chosen : DefaultDir(target.Name);
    }

    public ModTarget Target { get; }

    /// <summary>Где копии лежат на этом компьютере: своя папка профиля или <see cref="DefaultDir"/>.</summary>
    public string Dir { get; }

    /// <summary>Начало имени копий — имя профиля, годное для файла (<see cref="BackupStore.Slug"/>).</summary>
    public string Prefix { get; }

    /// <summary>Часы этой машины: имя копии — по местному времени.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Свободное место на диске папки (для тестов); null — неизвестно, не проверять.</summary>
    internal Func<string, long?>? FreeSpace { get; init; }

    private HostedTarget Hosted => Target.Hosted!;
    private string Id => Target.ProfileId;
    private string Root => HostedLayoutProbe.Root(Hosted.Settings);

    /// <summary>Папка копий по умолчанию: папка данных программы, HostedBackups/&lt;имя профиля&gt;.</summary>
    public static string DefaultDir(string? profileName) => Path.Combine(AppPaths.Root, "HostedBackups", BackupStore.Slug(profileName));

    /// <summary>Можно ли складывать копии в эту папку (как у копий своего сервера); null — можно, иначе — что не так.</summary>
    public static string? CheckDir(string? dir) => BackupStore.CheckDir(dir);

    public static string ExtraFor(string backupPath) => Path.ChangeExtension(backupPath, null) + ExtraSuffix;

    public static string InfoFor(string backupPath) => Path.ChangeExtension(backupPath, null) + InfoSuffix;

    /// <summary>Имя копии, сделанной в это время.</summary>
    public string NameFor(DateTime local) => $"{Prefix}-{local.ToString(StampFormat, CultureInfo.InvariantCulture)}{Extension}";

    // ---------- на сервере

    /// <summary>Где мир на сервере. Не нашёлся — <see cref="HostedWorldException"/> с понятным текстом.</summary>
    public Task<HostedWorldLocation> LocateAsync(CancellationToken ct) =>
        HostedSessions.UseAsync(Id, Hosted.Endpoint, async files =>
        {
            var snapshot = await HostedLayoutProbe.ReadAsync(files, Hosted.Settings, ct).ConfigureAwait(false);
            return await HostedWorldLocator.LocateAsync(snapshot, ct).ConfigureAwait(false);
        }, readOnly: true, ct);

    /// <summary>
    /// Что скачает <see cref="CreateAsync"/>: при <paramref name="serverStopped"/> — сам мир, иначе — самая свежая целая
    /// копия игры из Backups и BackupSaves; её нет — отказ «останови сервер или сделай /genbackup».
    /// </summary>
    public Task<HostedWorldSource> SourceAsync(bool serverStopped, CancellationToken ct) =>
        HostedSessions.UseAsync(Id, Hosted.Endpoint, async files =>
        {
            var snapshot = await HostedLayoutProbe.ReadAsync(files, Hosted.Settings, ct).ConfigureAwait(false);
            return await PickAsync(snapshot, serverStopped, ct).ConfigureAwait(false);
        }, readOnly: true, ct);

    private static async Task<HostedWorldSource> PickAsync(HostedSnapshot snapshot, bool serverStopped, CancellationToken ct)
    {
        if (serverStopped)
        {
            var world = await HostedWorldLocator.LocateAsync(snapshot, ct).ConfigureAwait(false);
            if (world.Entry is not { } entry)
                throw new HostedWorldException(HostedWorldProblem.Missing, Loc.T("hosted.worldMissing", world.ServerPath ?? world.Path));
            return new HostedWorldSource(HostedWorldOrigin.World, entry, world);
        }
        HostedWorldLocation? located = null;
        try
        {
            located = await HostedWorldLocator.LocateAsync(snapshot, ct).ConfigureAwait(false);
        }
        catch (HostedWorldException)
        {
            // на работающем сервере сам мир не нужен — нужна копия игры
        }
        var copy = await HostedWorldLocator.FreshestGameCopyAsync(snapshot.Listings, snapshot.Layout.DataDir ?? snapshot.Layout.Root, ct)
                       .ConfigureAwait(false)
                   ?? throw new HostedWorldException(HostedWorldProblem.NeedsStop, Loc.T("hosted.worldNeedsStop"));
        return new HostedWorldSource(HostedWorldOrigin.GameBackup, copy, located);
    }

    /// <summary>
    /// Сделать копию мира на этом компьютере. <paramref name="serverStopped"/> — сервер точно остановлен (решает вызывающий
    /// по логу): тогда качается сам мир, иначе — свежая копия игры. Рядом — архив настроек и данных модов (его сбой не губит
    /// копию — только предупреждение), затем ротация по <see cref="HostedConnection.BackupKeep"/>. Отмена посреди мира —
    /// <see cref="OperationCanceledException"/>, недокачанное удаляется; отмена, когда мир уже сохранён, — копия остаётся,
    /// архива нет, предупреждение.
    /// </summary>
    public async Task<HostedBackupResult> CreateAsync(bool serverStopped, IProgress<HostedTransfer>? progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (CheckDir(Dir) is { } dirProblem) throw new IOException(dirProblem);
        CleanStaleParts();

        // где мир, что качать и не остался ли рядом журнал SQLite; заодно — довести оборванное восстановление
        var (source, journal, layout) = await HostedSessions.UseAsync(Id, Hosted.Endpoint, async files =>
        {
            warnings.AddRange(await RecoverAsync(files, ct).ConfigureAwait(false));
            var snapshot = await HostedLayoutProbe.ReadAsync(files, Hosted.Settings, ct).ConfigureAwait(false);
            var picked = await PickAsync(snapshot, serverStopped, ct).ConfigureAwait(false);
            RemoteEntry? wal = null;
            if (picked.Origin == HostedWorldOrigin.World)
                wal = (await snapshot.Listings.ListAsync(RemotePath.Parent(picked.File.Path), ct).ConfigureAwait(false))?
                    .FirstOrDefault(e => !e.IsDirectory && e.Name == picked.File.Name + "-wal" && e.Size > 0);
            return (picked, wal, snapshot.Layout);
        }, readOnly: SwapMark.Read(Id) is null, ct).ConfigureAwait(false);

        var total = source.File.Size + (journal?.Size ?? 0);
        // с журналом копия собирается ещё раз рядом (SQLite сводит журнал в базу) — места нужно вдвое
        CheckSpace(total + (journal is null ? 0 : total));

        var name = NameFor(Time.GetLocalNow().DateTime);
        var target = Path.Combine(Dir, name);
        if (File.Exists(target)) throw new IOException(Loc.T("backup.exists", name));
        var part = target + PartSuffix;
        var partWal = part + "-wal";
        try
        {
            await DownloadAsync(source.Origin, source.File, part, 0, total, progress, ct).ConfigureAwait(false);
            if (journal is not null) await DownloadAsync(source.Origin, journal, partWal, source.File.Size, total, progress, ct).ConfigureAwait(false);
            var downloaded = new List<(RemoteEntry, string)> { (source.File, part) };
            if (journal is not null) downloaded.Add((journal, partWal));
            await VerifyAsync(source.Origin, downloaded, ct).ConfigureAwait(false);
            CheckWorldFile(part, source.File.Name);
            if (journal is null) File.Move(part, target);
            else
            {
                try
                {
                    WorldDb.Snapshot(part, target); // журнал — внутрь: копия одним цельным файлом
                }
                catch (InvalidOperationException ex)
                {
                    throw new IOException(ex.Message, ex); // журнал не свёлся — для окна это обычная ошибка операции
                }
            }
        }
        finally
        {
            foreach (var file in new[] { part, partWal }) DeleteQuietly(file);
        }
        File.SetLastWriteTimeUtc(target, Time.GetUtcNow().UtcDateTime); // ротация — по этому времени, а не по времени в имени
        WriteInfo(target, source);

        try
        {
            await HostedSessions.UseAsync(Id, Hosted.Endpoint, async files =>
                    await HostedWorldExtra.PackAsync(files, Root, layout, HostedMods.LastScan(Target)?.Mods, ExtraFor(target), progress, ct)
                        .ConfigureAwait(false),
                readOnly: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            warnings.Add(Loc.T("hosted.worldExtraFailed", Loc.T("hosted.worldCancelled")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            warnings.Add(Loc.T("hosted.worldExtraFailed", HostedSecret.Describe(ex)));
        }

        var keep = Hosted.Settings.BackupKeep;
        var pruned = keep > 0 ? Prune(keep) : [];
        return new HostedBackupResult(Describe(new FileInfo(target)), warnings, pruned);
    }

    /// <summary>
    /// Скачать файл в <paramref name="local"/> с докачкой: обрыв (соединение упало, сервер замолчал) — новое соединение и
    /// чтение с того, что уже есть. Перед докачкой файл на сервере сверяется с листингом до начала: другой — мир менялся.
    /// <see cref="MaxStalls"/> обрывов подряд без единого байта — сдаёмся с последней ошибкой.
    /// </summary>
    private async Task DownloadAsync(HostedWorldOrigin origin, RemoteEntry entry, string local, long baseDone, long total,
        IProgress<HostedTransfer>? progress, CancellationToken ct)
    {
        var file = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            var sink = new ProgressStream(file, at => progress?.Report(new HostedTransfer(HostedBackupStep.World, baseDone + at, total)));
            progress?.Report(new HostedTransfer(HostedBackupStep.World, baseDone, total));
            var stalls = 0;
            var restarts = 0;
            for (var first = true; ; first = false)
            {
                await file.FlushAsync(ct).ConfigureAwait(false);
                var have = file.Length;
                if (have > entry.Size) throw Changed(origin, entry); // на сервере файл вырос
                if (have == entry.Size && !first) return;
                try
                {
                    await HostedSessions.UseAsync(Id, Hosted.Endpoint, async files =>
                    {
                        // докачка после обрыва: тот ли это файл?
                        if (have > 0 && !Same(await new HostedListings(files, Root).FileAsync(entry.Path, ct).ConfigureAwait(false), entry))
                            throw Changed(origin, entry);
                        await files.DownloadFromAsync(entry.Path, have, sink, null, ct).ConfigureAwait(false);
                        return true;
                    }, ct).ConfigureAwait(false);
                }
                catch (Exception) when (sink.WriteError is { } localError)
                {
                    // сбой записи на этой машине (место, права) адаптер выдаёт за обрыв — показываем настоящий
                    ExceptionDispatchInfo.Throw(localError);
                }
                catch (RemoteFilesException ex) when (ex.Kind == RemoteFailure.RangeUnsupported && have > 0 && !ct.IsCancellationRequested)
                {
                    // сервер не читает с позиции — качаем заново с начала, но не без конца: обрывается каждый раз — сдаёмся
                    if (++restarts >= MaxStalls) throw;
                    file.SetLength(0);
                    file.Position = 0;
                    continue;
                }
                catch (RemoteFilesException ex) when (ex.Kind is RemoteFailure.Connect or RemoteFailure.Timeout or RemoteFailure.Protocol
                                                      && !ct.IsCancellationRequested)
                {
                    await file.FlushAsync(ct).ConfigureAwait(false);
                    var grew = file.Length > have;
                    // невнятный отказ сервера посреди передачи — докачиваем, только если до него что-то успело прийти
                    if (ex.Kind == RemoteFailure.Protocol && !grew) throw;
                    if (grew) stalls = 0;
                    else if (++stalls >= MaxStalls) throw;
                    continue;
                }
                await file.FlushAsync(ct).ConfigureAwait(false);
                sink.ReportNow();
                // передача кончилась без ошибки: целиком — готово; не целиком, но что-то пришло — докачиваем; ничего — сверка решит
                if (file.Length >= entry.Size || file.Length == have) return;
            }
        }
    }

    /// <summary>Сверка в конце: файлы на сервере те же, что в листинге до начала, и на этой машине их ровно столько же.</summary>
    private Task VerifyAsync(HostedWorldOrigin origin, IReadOnlyList<(RemoteEntry Entry, string Local)> files, CancellationToken ct) =>
        HostedSessions.UseAsync(Id, Hosted.Endpoint, async remote =>
        {
            var ls = new HostedListings(remote, Root);
            foreach (var (entry, local) in files)
            {
                if (!Same(await ls.FileAsync(entry.Path, ct).ConfigureAwait(false), entry)) throw Changed(origin, entry);
                var length = new FileInfo(local).Length;
                if (length != entry.Size)
                    throw new HostedWorldException(HostedWorldProblem.Incomplete, Loc.T("hosted.worldIncomplete", entry.Name, length, entry.Size));
            }
            return true;
        }, readOnly: true, ct);

    private static bool Same(RemoteEntry? now, RemoteEntry before) => now is not null && now.Size == before.Size && now.Modified == before.Modified;

    private static HostedWorldException Changed(HostedWorldOrigin origin, RemoteEntry entry) =>
        origin == HostedWorldOrigin.World
            ? new HostedWorldException(HostedWorldProblem.Changed, Loc.T("hosted.worldChanged"))
            : new HostedWorldException(HostedWorldProblem.CopyChanged, Loc.T("hosted.worldCopyChanged", entry.Name));

    private void CheckSpace(long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Dir)) ?? Dir;
        var free = FreeSpace is { } hook ? hook(root) : AvailableFreeSpace(root);
        if (free is { } f && f < bytes + Margin)
            throw new HostedWorldException(HostedWorldProblem.NoSpace,
                Loc.T("hosted.worldNoSpace", root, (bytes + Margin) / (1024 * 1024), f / (1024 * 1024)));
    }

    /// <summary>Свободно на диске; сетевую папку DriveInfo не знает — null, не проверяем.</summary>
    private static long? AvailableFreeSpace(string root)
    {
        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ReadOnlySpan<byte> SqliteHeader => "SQLite format 3\0"u8;

    /// <summary>Файл мира Vintage Story — база SQLite: заголовок проверяется до того, как копия встанет на место или уйдёт на сервер.</summary>
    internal static void CheckWorldFile(string path, string name)
    {
        Span<byte> head = stackalloc byte[16];
        int read;
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            read = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (read < head.Length || !head.SequenceEqual(SqliteHeader))
            throw new HostedWorldException(HostedWorldProblem.NotAWorld, Loc.T("hosted.worldNotDb", name));
    }

    // ---------- на этом компьютере

    /// <summary>Копии в папке, новые сверху. Папки нет или она недоступна (сетевая) — пусто.</summary>
    public IReadOnlyList<HostedWorldBackup> List()
    {
        try
        {
            if (!Directory.Exists(Dir)) return [];
            return new DirectoryInfo(Dir).EnumerateFiles("*" + Extension)
                .Where(f => f.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                .Select(Describe)
                .OrderByDescending(b => b.Time)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Оставить <paramref name="keep"/> последних своих копий, остальные удалить вместе с их архивами и сведениями. Чужие
    /// (другое имя, без отметки времени) не трогаются. keep ≤ 0 — ничего. Новые — по времени файла (UTC), как у
    /// <see cref="BackupStore.Prune"/>: в ночь перевода часов время в имени повторяется. Возвращает удалённые.
    /// </summary>
    public IReadOnlyList<HostedWorldBackup> Prune(int keep)
    {
        if (keep <= 0) return [];
        var doomed = List().Where(b => b.IsOwn)
            .OrderByDescending(b => File.GetLastWriteTimeUtc(b.Path)).ThenByDescending(b => b.Time)
            .Skip(keep).ToList();
        var removed = new List<HostedWorldBackup>();
        foreach (var old in doomed)
        {
            try
            {
                DeleteFiles(old.Path);
                removed.Add(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // занят — удалим в следующий раз
            }
        }
        return removed;
    }

    /// <summary>Удалить копию (с архивом и сведениями). Копия не из этой папки — ArgumentException; сбой диска — как есть.</summary>
    public void Delete(HostedWorldBackup backup)
    {
        if (!PathRules.IsUnder(backup.Path, Dir)) throw new ArgumentException("not a copy of this profile", nameof(backup));
        DeleteFiles(backup.Path);
    }

    private static void DeleteFiles(string path)
    {
        File.Delete(path);
        File.Delete(ExtraFor(path));
        File.Delete(InfoFor(path));
    }

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Недокачанное своё от прошлого раза (программу закрыли посреди скачивания) — удалить: продолжать его некому.</summary>
    private void CleanStaleParts()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Dir, Prefix + "-*" + Extension + PartSuffix + "*")) DeleteQuietly(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не вышло — это только место на диске
        }
    }

    private HostedWorldBackup Describe(FileInfo file)
    {
        var stamped = TryStamp(file.Name, out var time);
        var info = ReadInfo(file.FullName);
        return new HostedWorldBackup(file.FullName, file.Name, stamped ? time : file.LastWriteTime, file.Length)
        {
            IsStamped = stamped,
            IsOwn = stamped && file.Name.StartsWith(Prefix + "-", StringComparison.OrdinalIgnoreCase)
                    && file.Name.Length == Prefix.Length + 1 + StampFormat.Length + Extension.Length,
            ExtraSize = new FileInfo(ExtraFor(file.FullName)) is { Exists: true } extra ? extra.Length : 0,
            Origin = info?.Origin,
            SourcePath = info?.Source,
            SourceTime = info?.SourceModified,
        };
    }

    private static bool TryStamp(string name, out DateTime time)
    {
        time = default;
        return Stamp().Match(name) is { Success: true } m
               && DateTime.TryParseExact(m.Groups[1].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out time);
    }

    [GeneratedRegex(@"-(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.vcdbs$", RegexOptions.IgnoreCase)]
    private static partial Regex Stamp();

    /// <summary>Сведения о копии рядом с ней: откуда скачана и когда.</summary>
    private sealed class Info
    {
        [JsonConverter(typeof(TolerantEnumConverter))]
        public HostedWorldOrigin? Origin { get; set; }

        public string? Source { get; set; }
        public string? ServerPath { get; set; }
        public long SourceSize { get; set; }
        public DateTime? SourceModified { get; set; }
        public DateTime Created { get; set; }
    }

    private void WriteInfo(string backupPath, HostedWorldSource source)
    {
        var info = new Info
        {
            Origin = source.Origin,
            Source = source.File.Path,
            ServerPath = source.World?.ServerPath,
            SourceSize = source.File.Size,
            SourceModified = source.File.Modified,
            Created = Time.GetUtcNow().UtcDateTime,
        };
        try
        {
            File.WriteAllText(InfoFor(backupPath), JsonConvert.SerializeObject(info, Formatting.Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // сведения — для списка; копия мира и без них целая
        }
    }

    private static Info? ReadInfo(string backupPath)
    {
        try
        {
            var path = InfoFor(backupPath);
            return File.Exists(path) ? JsonConvert.DeserializeObject<Info>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Обёртка потока: сообщает, докуда дошли чтение или запись (не чаще, чем через мегабайт), и запоминает сбой записи на
/// этой машине — адаптеры FTP и SFTP выдали бы его за обрыв связи. Внутренний поток не закрывает.
/// </summary>
internal sealed class ProgressStream(Stream inner, Action<long>? position) : Stream
{
    private const long Step = 1024 * 1024;
    private long _reported = -1;

    public Exception? WriteError { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        Report(n == 0);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Report(n == 0);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        try
        {
            inner.Write(buffer, offset, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteError = ex;
            throw;
        }
        Report(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteError = ex;
            throw;
        }
        Report(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Сообщить, где сейчас поток, без ожидания мегабайта.</summary>
    public void ReportNow()
    {
        _reported = inner.Position;
        position?.Invoke(_reported);
    }

    private void Report(bool end)
    {
        if (position is null) return;
        var at = inner.Position;
        if (!end && _reported >= 0 && at - _reported < Step) return;
        _reported = at;
        position(at);
    }

    protected override void Dispose(bool disposing)
    {
        // внутренний поток закрывает его владелец
    }
}
