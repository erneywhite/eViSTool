using eViSTool.Core.Localization;
using eViSTool.Core.Platform;
using Newtonsoft.Json;

namespace eViSTool.Core.Hosting;

public sealed partial class HostedWorldBackups
{
    /// <summary>Спутники базы SQLite рядом с миром: остатки журнала прежнего мира испортили бы восстановленный.</summary>
    private static readonly string[] JournalTails = ["-wal", "-shm"];

    /// <summary>
    /// Восстановить мир на сервер из копии на этом компьютере — только при остановленном сервере: без
    /// <see cref="HostedRestoreOptions.ServerStopped"/> (решает вызывающий по логу) отказ, на сервер ничего не пишется.
    /// <list type="number">
    /// <item>Копия проверяется (база SQLite), архив с настройками — тоже, если что-то из него выбрано.</item>
    /// <item>Мир заливается рядом с миром на сервере как «….evistool.tmp» и сверяется по размеру; сбой — временный файл
    /// убирается, мир не тронут.</item>
    /// <item>Прежний мир и остатки журнала SQLite (-wal, -shm) уходят в «….evistool.bak», временный встаёт на место
    /// (поверх G-Portal не переименовывает — поэтому сначала bak). Сбой — всё возвращается назад; не вернулось — на этом
    /// компьютере остаётся отметка, по ней прежний мир вернёт следующая операция с копиями мира.</item>
    /// <item>Прежний мир удаляется, если не просили оставить (<see cref="HostedRestoreOptions.KeepOldWorld"/>).</item>
    /// <item>Выбранное из архива — по файлу, через временный и bak; сбой здесь — предупреждение: мир уже на месте.</item>
    /// </list>
    /// </summary>
    public async Task<HostedRestoreResult> RestoreAsync(HostedWorldBackup backup, HostedRestoreOptions options, IProgress<HostedTransfer>? progress,
        CancellationToken ct)
    {
        if (!options.ServerStopped) throw new HostedWorldException(HostedWorldProblem.NotStopped, Loc.T("hosted.worldRestoreRunning"));
        var local = new FileInfo(backup.Path);
        if (!local.Exists) throw new FileNotFoundException(Loc.T("backup.noSave", backup.Path), backup.Path);
        CheckWorldFile(local.FullName, backup.Name);

        var warnings = new List<string>();
        var extras = options.Extras;
        var archive = ExtraFor(local.FullName);
        if (extras != HostedWorldExtras.None)
        {
            if (!File.Exists(archive))
            {
                warnings.Add(Loc.T("hosted.worldNoExtra"));
                extras = HostedWorldExtras.None;
            }
            else
            {
                try
                {
                    HostedWorldExtra.Check(archive, extras);
                }
                catch (InvalidDataException ex)
                {
                    throw new HostedWorldException(HostedWorldProblem.BadArchive, Loc.T("hosted.worldBadArchive", Path.GetFileName(archive), ex.Message), ex);
                }
            }
        }

        return await HostedSessions.UseAsync(Id, Hosted.Endpoint, async files =>
        {
            warnings.AddRange(await RecoverAsync(files, ct).ConfigureAwait(false));
            var snapshot = await HostedLayoutProbe.ReadAsync(files, Hosted.Settings, ct).ConfigureAwait(false);
            var world = await HostedWorldLocator.LocateAsync(snapshot, ct).ConfigureAwait(false);
            var kept = await SwapAsync(files, world.Path, local, options.KeepOldWorld, progress, ct).ConfigureAwait(false);

            // мир на месте — дальше без отмены: это быстро, а наполовину возвращённые настройки хуже, чем ни одной
            var count = 0;
            if (extras != HostedWorldExtras.None)
            {
                try
                {
                    if (!files.IsConnected) await files.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                    count = await HostedWorldExtra.RestoreAsync(files, Root, snapshot.Layout, Id, archive, extras, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    warnings.Add(Loc.T("hosted.worldExtraRestoreFailed", HostedSecret.Describe(ex)));
                }
            }
            return new HostedRestoreResult(world.Path, warnings, kept, count);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Залить копию рядом с миром и поставить на место через bak. Возвращает, где оставлен прежний мир (null — убран или его не было).</summary>
    private async Task<string?> SwapAsync(IRemoteFiles files, string world, FileInfo local, bool keepOld, IProgress<HostedTransfer>? progress,
        CancellationToken ct)
    {
        var tmp = world + HostedSafeWrite.TmpSuffix;
        var bak = world + HostedSafeWrite.BakSuffix;
        var dir = RemotePath.Parent(world);
        var name = RemotePath.Name(world);
        var ls = new HostedListings(files, Root);
        if (await ls.ListAsync(dir, ct).ConfigureAwait(false) is null)
            throw new HostedWorldException(HostedWorldProblem.NotFound, Loc.T("hosted.worldNotFound"));

        // ---- 1) залить рядом под временным именем: сбой — убрать его, мир не тронут
        try
        {
            if (await ls.FileAsync(tmp, ct).ConfigureAwait(false) is not null) await files.DeleteFileAsync(tmp, ct).ConfigureAwait(false);
            var source = new FileStream(local.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            await using (source.ConfigureAwait(false))
            {
                var reader = new ProgressStream(source, at => progress?.Report(new HostedTransfer(HostedBackupStep.Upload, at, local.Length)));
                progress?.Report(new HostedTransfer(HostedBackupStep.Upload, 0, local.Length));
                // shortcut: заливка без докачки — оборвалась, и мир заливается заново следующей попыткой; переделать (APPE/REST
                // STOR), если на больших мирах обрывы станут обычным делом
                await files.UploadAsync(reader, tmp, null, ct).ConfigureAwait(false);
                reader.ReportNow();
            }
            ls.Forget(dir);
            var uploaded = await ls.FileAsync(tmp, ct).ConfigureAwait(false);
            if (uploaded?.Size != local.Length)
                throw new HostedWorldException(HostedWorldProblem.Incomplete, Loc.T("hosted.worldIncomplete", name, uploaded?.Size ?? 0, local.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await QuietlyAsync(files, f => f.DeleteFileAsync(tmp, CancellationToken.None)).ConfigureAwait(false);
            if (ex is OperationCanceledException or HostedWorldException) throw;
            throw new HostedWorldException(HostedWorldProblem.RestoreUndone, Loc.T("hosted.worldRestoreUndone", HostedSecret.Describe(ex)), ex);
        }

        // ---- 2) подмена: прежний мир и журнал SQLite — в bak, новый — на место
        var moves = new List<string[]>();
        bool hadWorld;
        try
        {
            ls.Forget(dir);
            var listing = await ls.ListAsync(dir, ct).ConfigureAwait(false) ?? [];
            bool Has(string fileName) => listing.Any(e => !e.IsDirectory && e.Name == fileName);
            hadWorld = Has(name);
            if (Has(RemotePath.Name(bak)))
            {
                if (hadWorld) await files.DeleteFileAsync(bak, ct).ConfigureAwait(false); // страховка прошлого раза: новой станет нынешний мир
                else
                {
                    await files.RenameAsync(bak, world, ct).ConfigureAwait(false); // мира нет, а bak есть — это и есть прежний мир
                    hadWorld = true;
                }
            }
            if (hadWorld) moves.Add([world, bak]);
            foreach (var tail in JournalTails)
            {
                if (!Has(name + tail)) continue;
                if (Has(name + tail + HostedSafeWrite.BakSuffix))
                    await files.DeleteFileAsync(world + tail + HostedSafeWrite.BakSuffix, ct).ConfigureAwait(false);
                moves.Add([world + tail, world + tail + HostedSafeWrite.BakSuffix]);
            }
            // без отметки переносить нельзя: обрыв оставил бы мир только в bak, и никто бы о нём не вспомнил
            new SwapMark { World = world, Tmp = tmp, Size = local.Length, KeepOld = keepOld, Moves = moves }.Save(Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // до переносов: мир на месте, убрать только залитое
            await QuietlyAsync(files, f => f.DeleteFileAsync(tmp, CancellationToken.None)).ConfigureAwait(false);
            if (ex is OperationCanceledException) throw;
            throw new HostedWorldException(HostedWorldProblem.RestoreUndone, Loc.T("hosted.worldRestoreUndone", HostedSecret.Describe(ex)), ex);
        }

        try
        {
            foreach (var move in moves) await files.RenameAsync(move[0], move[1], ct).ConfigureAwait(false);
            await files.RenameAsync(tmp, world, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // ответ на последнее переименование мог потеряться (обрыв), а новый мир уже на месте — тогда всё получилось
            if (!await LandedAsync(files, world, tmp, local.Length).ConfigureAwait(false))
            {
                if (!await PutBackAsync(files, moves).ConfigureAwait(false))
                    throw new HostedWorldException(HostedWorldProblem.RestoreStuck,
                        Loc.T("hosted.worldRestoreStuck", hadWorld ? bak : world, HostedSecret.Describe(ex)), ex); // отметка остаётся
                SwapMark.Clear(Id);
                await QuietlyAsync(files, f => f.DeleteFileAsync(tmp, CancellationToken.None)).ConfigureAwait(false);
                if (ex is OperationCanceledException) throw;
                throw new HostedWorldException(HostedWorldProblem.RestoreUndone, Loc.T("hosted.worldRestoreUndone", HostedSecret.Describe(ex)), ex);
            }
        }
        SwapMark.Clear(Id);
        if (keepOld) return hadWorld ? bak : null;
        foreach (var move in moves) await QuietlyAsync(files, f => f.DeleteFileAsync(move[1], CancellationToken.None)).ConfigureAwait(false);
        return null;
    }

    /// <summary>Новый мир на месте: временного файла нет, а мир — ровно того размера, что заливали. Не узнать — false.</summary>
    private async Task<bool> LandedAsync(IRemoteFiles files, string world, string tmp, long size)
    {
        try
        {
            if (!files.IsConnected) await files.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            var ls = new HostedListings(files, Root);
            return await ls.FileAsync(tmp, CancellationToken.None).ConfigureAwait(false) is null
                   && (await ls.FileAsync(world, CancellationToken.None).ConfigureAwait(false))?.Size == size;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Вернуть перенесённое назад, в обратном порядке: перенос, которого не было (исходный файл на месте), пропускается.
    /// Связь упала — сначала переподключиться. false — что-то вернуть не вышло.
    /// </summary>
    private async Task<bool> PutBackAsync(IRemoteFiles files, IReadOnlyList<string[]> moves)
    {
        try
        {
            if (!files.IsConnected) await files.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            var ls = new HostedListings(files, Root);
            for (var i = moves.Count - 1; i >= 0; i--)
            {
                var (from, to) = (moves[i][0], moves[i][1]);
                if (await ls.FileAsync(from, CancellationToken.None).ConfigureAwait(false) is not null) continue;
                if (await ls.FileAsync(to, CancellationToken.None).ConfigureAwait(false) is null) continue;
                await files.RenameAsync(to, from, CancellationToken.None).ConfigureAwait(false);
                ls.Forget(RemotePath.Parent(from));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Прошлое восстановление оборвалось (процесс закрыли, возврат не удался): по отметке на этой машине вернуть прежний
    /// мир на место, если нового там нет. Новый на месте — убрать лишнее (bak, если не просили оставить, и временный файл).
    /// На месте мира чужой файл (сервер запускали, и он создал новый мир) — прежний мир не удаляется, а предупреждение
    /// говорит, где он. Сбой — исключение, отметка остаётся до следующего раза.
    /// </summary>
    private async Task<IReadOnlyList<string>> RecoverAsync(IRemoteFiles files, CancellationToken ct)
    {
        if (SwapMark.Read(Id) is not { } mark) return [];
        var warnings = new List<string>();
        var ls = new HostedListings(files, Root);
        var bak = mark.World + HostedSafeWrite.BakSuffix;
        var now = await ls.FileAsync(mark.World, ct).ConfigureAwait(false);
        if (now is null)
        {
            if (!await PutBackAsync(files, mark.Moves).ConfigureAwait(false))
                throw new HostedWorldException(HostedWorldProblem.RestoreStuck, Loc.T("hosted.worldRestoreStuck", bak, ""));
            warnings.Add(Loc.T("hosted.worldRecovered"));
        }
        else if (now.Size != mark.Size)
        {
            if (await ls.FileAsync(bak, ct).ConfigureAwait(false) is not null) warnings.Add(Loc.T("hosted.worldBakLeft", bak));
        }
        else if (!mark.KeepOld)
            foreach (var move in mark.Moves)
                if (await ls.FileAsync(move[1], ct).ConfigureAwait(false) is not null)
                    await files.DeleteFileAsync(move[1], ct).ConfigureAwait(false);
        ls.Forget(RemotePath.Parent(mark.World));
        if (await ls.FileAsync(mark.Tmp, ct).ConfigureAwait(false) is not null) await files.DeleteFileAsync(mark.Tmp, ct).ConfigureAwait(false);
        SwapMark.Clear(Id);
        return warnings;
    }

    /// <summary>Уборка на сервере, которая не должна заслонить основную ошибку; связь упала — сначала переподключиться.</summary>
    private static async Task QuietlyAsync(IRemoteFiles files, Func<IRemoteFiles, Task> step)
    {
        try
        {
            if (!files.IsConnected) await files.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await step(files).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // уборка после сбоя или лишнего bak: основное дело уже решено
        }
    }

    /// <summary>
    /// Отметка «мир на сервере сейчас подменяется»: <c>&lt;данные eViSTool&gt;/hosted/&lt;id&gt;/world-swap.json</c> — путь мира,
    /// временного файла, размер залитого и переносы в bak. Ставится перед первым переносом, снимается, когда мир на месте
    /// (новый или прежний). Битая — как нет: тогда прежний мир, оставшийся только как bak, вернёт само восстановление.
    /// </summary>
    internal sealed class SwapMark
    {
        public const string FileName = "world-swap.json";

        public string World { get; set; } = "";
        public string Tmp { get; set; } = "";
        public long Size { get; set; }
        public bool KeepOld { get; set; }
        public List<string[]> Moves { get; set; } = [];

        private static string PathFor(string profileId) => Path.Combine(HostedJournal.DirFor(profileId), FileName);

        public static SwapMark? Read(string profileId)
        {
            try
            {
                var file = PathFor(profileId);
                if (!File.Exists(file) || JsonConvert.DeserializeObject<SwapMark>(File.ReadAllText(file)) is not { } mark) return null;
                // пути пришли с диска — проверяем так же, как чужие
                mark.World = RemotePath.Normalize(mark.World);
                mark.Tmp = RemotePath.Normalize(mark.Tmp);
                mark.Moves = [.. mark.Moves.Where(m => m is { Length: 2 }).Select(m => new[] { RemotePath.Normalize(m[0]), RemotePath.Normalize(m[1]) })];
                return mark;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>Записать до первого переноса. Не записалась — переносить нельзя: без неё обрыв оставил бы мир только в bak.</summary>
        public void Save(string profileId)
        {
            var file = PathFor(profileId);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
            SafeFile.Replace(tmp, file);
        }

        public static void Clear(string profileId)
        {
            try
            {
                File.Delete(PathFor(profileId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // осталась — следующая операция увидит мир на месте и просто снимет её
            }
        }
    }
}
