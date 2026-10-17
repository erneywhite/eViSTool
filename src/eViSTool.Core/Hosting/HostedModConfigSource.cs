using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Hosting;

/// <summary>
/// «Настройки модов» сервера у хостера: папка ModConfig в папке данных сервера на FTP (у G-Portal — /ModConfig). То же,
/// что <see cref="ModConfigService"/> делает с диском: список с привязкой к модам, чтение, запись с проверкой «файл
/// поменяли с момента открытия», шаг назад, сброс.
/// <list type="bullet">
/// <item>файлы привязываются к модам последнего скана профиля (<see cref="HostedMods.LastScan"/>); скана в этом окне ещё
/// не было — список сначала сканирует моды;</item>
/// <item>время файла — из листинга (MLSD — UTC, LIST — местное время сервера) и сравнивается только на равенство;</item>
/// <item>резервные копии — на этой машине (<see cref="ModConfigBackups"/> профиля): у хостера других мест нет;</item>
/// <item>запись — через временный файл и bak (<see cref="HostedSafeWrite"/>); сорвалась посередине — файл вернёт на место
/// следующая операция или скан модов (<see cref="RecoverAsync"/>);</item>
/// <item>что операции заодно починили, копится в <see cref="Warnings"/>, пока окно не заберёт (<see cref="TakeWarnings"/>).</item>
/// </list>
/// Каждая операция — одна очередь соединения профиля (<see cref="HostedSessions"/>). Пароль сюда приходит только внутри
/// <see cref="ModTarget"/> и дальше соединения не уходит.
/// </summary>
public sealed class HostedModConfigSource : IModConfigSource
{
    /// <summary>Глубже стольких вложенных папок в ModConfig не листаем.</summary>
    public const int MaxDepth = 8;

    private const string SwapDirName = "modconfig-swap";

    // относительные пути из последнего прочитанного списка по профилям — для ссылки «Настройки мода» в карточке без
    // отдельного запроса к серверу
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> Listed = new(StringComparer.Ordinal);

    private readonly ModConfigBackups _backups;
    private readonly Lock _warningsGate = new();
    private List<string> _warnings = [];

    public HostedModConfigSource(ModTarget target, ModConfigBackups? backups = null)
    {
        Target = target.IsHosted ? target : throw new ArgumentException("not a hosted target", nameof(target));
        _backups = backups ?? ModConfigBackups.ForProfile(target.Profile);
    }

    public ModTarget Target { get; }

    /// <summary>Папка ModConfig на сервере; null — ещё не искали.</summary>
    public string? Folder { get; private set; }

    /// <summary>Папка ModConfig нашлась при последнем поиске (false — сервер ещё не запускали с модами, у которых есть настройки).</summary>
    public bool FolderExists { get; private set; } = true;

    /// <summary>
    /// Что починили операции («вернулась прежняя версия …», разовые предупреждения скана модов) и ещё не забрал
    /// <see cref="TakeWarnings"/> — уже на языке окна.
    /// </summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_warningsGate) return [.. _warnings];
        }
    }

    /// <summary>
    /// Забрать накопленные предупреждения: окно показывает их после каждой своей операции, каждое по разу. Они копятся,
    /// а не перезаписываются: следующая операция (чтение открытого файла сразу после списка) не должна стереть
    /// предупреждение прошлой, пока его не показали.
    /// </summary>
    public IReadOnlyList<string> TakeWarnings()
    {
        lock (_warningsGate)
        {
            var taken = _warnings;
            _warnings = [];
            return taken;
        }
    }

    private void Warn(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;
        lock (_warningsGate) _warnings.AddRange(warnings);
    }

    // у файлов на FTP нет пути на этой машине: «Показать файл» скрыт
    public string? LocalPath(string relativePath) => null;
    public string? LocalFolder => null;

    /// <summary>Пути внутри ModConfig из последнего списка этого профиля в этом окне; списка ещё не читали — null.</summary>
    public static IReadOnlyList<string>? LastListed(string profileId) => Listed.TryGetValue(profileId, out var list) ? list : null;

    private HostedTarget Hosted => Target.Hosted!;
    private string Id => Target.ProfileId;
    private string Root => HostedLayoutProbe.Root(Hosted.Settings);

    private Task<T> UseAsync<T>(Func<IRemoteFiles, HostedListings, Task<T>> work, bool readOnly, CancellationToken ct) =>
        HostedSessions.UseAsync(Id, Hosted.Endpoint, async files =>
        {
            Warn(await RecoverAsync(files, Id, Root, ct).ConfigureAwait(false));
            return await work(files, new HostedListings(files, Root)).ConfigureAwait(false);
        }, readOnly, ct);

    /// <summary>
    /// Операция записи: <paramref name="work"/> зовёт <c>done</c> с итогом, как только файл на сервере уже в новом виде.
    /// Связь оборвалась или вышло время после этого (на уборке bak, на листинге ради времени файла) — это не «не удалось
    /// сохранить»: ответ — итог из done. Мёртвое соединение очередь при этом закрывает, как после любого обрыва.
    /// </summary>
    private async Task<T> ChangeAsync<T>(Func<IRemoteFiles, HostedListings, Action<T>, Task<T>> work, CancellationToken ct)
        where T : class
    {
        T? done = null;
        try
        {
            return await UseAsync((files, ls) => work(files, ls, result => done = result), readOnly: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (done is not null && ex is IOException or OperationCanceledException)
        {
            // shortcut: время файла после такой записи не узнали (в итоге его нет) — следующая запись пройдёт без проверки
            // «поменяли с момента открытия»; переделать, если окно начнёт перечитывать файл после неё
            return done;
        }
    }

    /// <summary>Итог записи без свежего листинга: файл — с этим текстом, время неизвестно.</summary>
    private ModConfigContent Written(ConfigFile file, string text) =>
        new(file.Relative, text, true, _backups.Versions(file.Key).Count, null);

    public Task<IReadOnlyList<ModConfigEntry>> ListAsync(CancellationToken ct = default) =>
        UseAsync<IReadOnlyList<ModConfigEntry>>(async (files, ls) =>
        {
            var scan = HostedMods.LastScan(Target);
            IReadOnlyList<LocalMod> mods;
            HostedLayout? layout = null;
            if (scan is { } known) (layout, mods) = known;
            else
            {
                // разовые предупреждения скана (довели установку, вернули конфиг…) второй раз никто не выдаст — их
                // покажет окно настроек
                var hosted = new HostedMods(Target);
                (mods, var once) = await hosted.ScanModsAsync(files, ct).ConfigureAwait(false);
                Warn(once);
                layout = hosted.Layout;
            }
            Folder = null; // список — повод поискать папку заново: её могли создать или убрать
            var folder = await FolderAsync(files, ls, layout, ct).ConfigureAwait(false);
            var found = new List<(string, long, DateTime)>();
            if (FolderExists) await CollectAsync(ls, folder, folder, 0, found, ct).ConfigureAwait(false);
            var entries = ModConfigs.Entries(found, mods);
            Listed[Id] = [.. entries.Select(e => e.RelativePath)];
            return entries;
        }, readOnly: true, ct);

    public Task<ModConfigContent> ReadAsync(string relativePath, CancellationToken ct = default) =>
        UseAsync(async (files, ls) =>
        {
            var file = PathOf(await FolderAsync(files, ls, null, ct).ConfigureAwait(false), relativePath);
            return await ReadCoreAsync(files, ls, file, ct).ConfigureAwait(false);
        }, readOnly: true, ct);

    public Task<ModConfigSaveResult> SaveAsync(ModConfigSaveRequest request, CancellationToken ct = default)
    {
        // сломанный JSON не уходит на сервер — проверка до соединения, как у файла на диске
        if (ModConfigs.KindOf(request.Path) == ModConfigKind.Json && ModConfigs.JsonError(request.Text) is { } error)
            return Task.FromException<ModConfigSaveResult>(new InvalidDataException(error));
        return ChangeAsync<ModConfigSaveResult>(async (files, ls, done) =>
        {
            var file = PathOf(await FolderAsync(files, ls, null, ct).ConfigureAwait(false), request.Path);
            // shortcut: «поменяли с момента открытия» — только по времени из листинга (MLSD — до секунды, LIST — до минуты):
            // правку в ту же секунду (минуту) не заметим; переделать, если в запросе появится размер или отпечаток текста
            var entry = await ls.FileAsync(file.Remote, ct).ConfigureAwait(false);
            if (request.ExpectedChangedUtc is { } expected && entry?.Modified != expected) return new ModConfigSaveResult(null, Changed: true);

            var bom = false;
            if (entry is not null)
            {
                // прежняя версия — копией на этой машине до записи: без копии на сервер ничего не пишем
                var old = await DownloadAsync(files, file.Remote, ct).ConfigureAwait(false);
                bom = HasBom(old);
                _backups.Keep(old, file.Key);
            }
            var body = Encoding.UTF8.GetBytes(request.Text);
            await WriteAsync(files, ls, file, bom ? [.. Encoding.UTF8.Preamble, .. body] : body, ct).ConfigureAwait(false);
            done(new ModConfigSaveResult(Written(file, request.Text), Changed: false));
            return new ModConfigSaveResult(await ReadCoreAsync(files, ls, file, ct, request.Text).ConfigureAwait(false), Changed: false);
        }, ct);
    }

    /// <summary>Шаг назад: последняя сохранённая на этой машине версия заливается на сервер и уходит из истории.</summary>
    public Task<ModConfigContent> UndoAsync(string relativePath, CancellationToken ct = default) =>
        ChangeAsync<ModConfigContent>(async (files, ls, done) =>
        {
            var file = PathOf(await FolderAsync(files, ls, null, ct).ConfigureAwait(false), relativePath);
            string? written = null;
            if (_backups.Versions(file.Key).FirstOrDefault() is { } last)
            {
                var bytes = await File.ReadAllBytesAsync(last, ct).ConfigureAwait(false);
                await WriteAsync(files, ls, file, bytes, ct).ConfigureAwait(false);
                File.Delete(last);
                written = Decode(bytes); // залитое — уже известно, снова не качаем
                done(Written(file, written));
            }
            return await ReadCoreAsync(files, ls, file, ct, written).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Сбросить к стандартным: копия — на эту машину, файл на сервере удаляется (мод создаст его заново при запуске
    /// сервера). Отменяется «Вернуть предыдущую версию».
    /// </summary>
    public Task<ModConfigContent> ResetAsync(string relativePath, CancellationToken ct = default) =>
        ChangeAsync<ModConfigContent>(async (files, ls, done) =>
        {
            var file = PathOf(await FolderAsync(files, ls, null, ct).ConfigureAwait(false), relativePath);
            if (await ls.FileAsync(file.Remote, ct).ConfigureAwait(false) is not null)
            {
                _backups.Keep(await DownloadAsync(files, file.Remote, ct).ConfigureAwait(false), file.Key);
                await files.DeleteFileAsync(file.Remote, ct).ConfigureAwait(false);
                ls.Forget(RemotePath.Parent(file.Remote));
                done(new ModConfigContent(file.Relative, "", false, _backups.Versions(file.Key).Count, null));
            }
            return await ReadCoreAsync(files, ls, file, ct).ConfigureAwait(false);
        }, ct);

    // ---------- пути

    /// <summary>
    /// Файл настроек: путь на сервере, путь внутри ModConfig в чистом виде и ключ резервных копий на этой машине
    /// (<see cref="BackupKey"/>).
    /// </summary>
    private sealed record ConfigFile(string Remote, string Relative, string Key);

    /// <summary>
    /// Ключ резервных копий файла на этой машине: путь внутри ModConfig, части — именами, годными для любой ОС (на Linux
    /// в имени бывает «:», на Windows — нет). Копии разных файлов не должны смешаться: на сервере «Carry.json» и
    /// «carry.json» — разные файлы, а папки копий на Windows регистр не различают; SafeLocalName вдобавок сводит
    /// «a:b.json» и «a_b.json» в одно имя. Поэтому путь не в нижнем регистре или с заменёнными знаками получает к имени
    /// отпечаток точного пути: «CarryOn.json» → «CarryOn~1a2b3c4d.json».
    /// </summary>
    public static string BackupKey(string relativePath)
    {
        var parts = relativePath.Split('/').Select(RemotePath.SafeLocalName).ToArray();
        var key = string.Join('/', parts);
        if (key == relativePath && relativePath == relativePath.ToLowerInvariant()) return key;
        var stamp = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(relativePath)))[..8].ToLowerInvariant();
        var leaf = parts[^1];
        var ext = Path.GetExtension(leaf);
        parts[^1] = leaf[..^ext.Length] + "~" + stamp + ext;
        return string.Join('/', parts);
    }

    /// <summary>
    /// Путь внутри ModConfig («sub/x.json») → файл на сервере. Правила — как у <see cref="ModConfigService.FullPath"/>:
    /// путь от корня, с диском Windows, с выходом наружу и не-текстовые файлы — отказ. «..» не принимается совсем: пути
    /// приходят из списка, а там их нет.
    /// </summary>
    private static ConfigFile PathOf(string folder, string relativePath)
    {
        InvalidOperationException Bad() => new(Loc.T("mcfg.badPath", relativePath));
        if (string.IsNullOrEmpty(relativePath)) throw Bad();
        var rel = relativePath.Replace('\\', '/');
        if (rel.StartsWith('/') || (rel.Length >= 2 && rel[1] == ':')) throw Bad();
        string remote;
        try
        {
            remote = RemotePath.Combine(folder, rel);
        }
        catch (InvalidDataException)
        {
            throw Bad(); // «..», управляющие знаки
        }
        if (remote == RemotePath.Normalize(folder) || !RemotePath.IsInside(folder, remote) || ModConfigs.KindOf(remote) is null) throw Bad();
        var clean = RemotePath.Relative(folder, remote);
        return new ConfigFile(remote, clean, BackupKey(clean));
    }

    /// <summary>
    /// Папка ModConfig: в папке данных сервера (где лежит serverconfig.json; у G-Portal — корень), как и Logs. Раскладка —
    /// переданная, из последнего скана или найденная заново. Игра создаёт «ModConfig»; в другом регистре — тоже годится.
    /// </summary>
    private async Task<string> FolderAsync(IRemoteFiles files, HostedListings ls, HostedLayout? layout, CancellationToken ct)
    {
        if (Folder is { } known) return known;
        layout ??= HostedMods.LastScan(Target)?.Layout
                   ?? await HostedLayoutProbe.DetectAsync(files, Hosted.Settings, ct).ConfigureAwait(false);
        var exact = RemotePath.Combine(layout.DataDir ?? layout.Root, ModConfigs.FolderName);
        var folder = await ls.IsDirectoryAsync(exact, ct).ConfigureAwait(false) ? exact
            : await ls.DirectoryIgnoreCaseAsync(exact, ct).ConfigureAwait(false);
        FolderExists = folder is not null;
        return Folder = folder ?? exact;
    }

    /// <summary>Файлы настроек папки и её подпапок: путь внутри ModConfig, размер, время из листинга.</summary>
    private static async Task CollectAsync(HostedListings ls, string folder, string dir, int depth, List<(string, long, DateTime)> into,
        CancellationToken ct)
    {
        foreach (var e in await ls.ListAsync(dir, ct).ConfigureAwait(false) ?? [])
        {
            if (e.IsDirectory)
            {
                // shortcut: глубже MaxDepth не идём — ссылка папки на саму себя на FTP выглядит бесконечной вложенностью;
                // переделать, если моды начнут класть настройки глубже
                if (depth < MaxDepth) await CollectAsync(ls, folder, e.Path, depth + 1, into, ct).ConfigureAwait(false);
                continue;
            }
            // наши .evistool.tmp и .bak — не настройки (KindOf их не узнает)
            if (ModConfigs.KindOf(e.Name) is null) continue;
            into.Add((RemotePath.Relative(folder, e.Path), e.Size, e.Modified ?? DateTime.MinValue));
        }
    }

    // ---------- чтение и запись

    /// <summary>Содержимое файла по свежему листингу; <paramref name="written"/> — только что записанный текст (не качать его снова).</summary>
    private async Task<ModConfigContent> ReadCoreAsync(IRemoteFiles files, HostedListings ls, ConfigFile file, CancellationToken ct,
        string? written = null)
    {
        var versions = _backups.Versions(file.Key).Count;
        if (await ls.FileAsync(file.Remote, ct).ConfigureAwait(false) is not { } entry)
            return new ModConfigContent(file.Relative, "", false, versions, null);
        var text = written ?? Decode(await DownloadAsync(files, file.Remote, ct).ConfigureAwait(false));
        return new ModConfigContent(file.Relative, text, true, versions, entry.Modified);
    }

    /// <summary>Залить через временный файл и bak; папки (ModConfig, подпапки) нет — создать, сверху вниз.</summary>
    private async Task WriteAsync(IRemoteFiles files, HostedListings ls, ConfigFile file, byte[] content, CancellationToken ct)
    {
        var parent = RemotePath.Parent(file.Remote);
        var missing = new Stack<string>();
        for (var dir = parent; !await ls.IsDirectoryAsync(dir, ct).ConfigureAwait(false); dir = RemotePath.Parent(dir))
            missing.Push(dir); // выше папки данных не уйдёт: она есть (в ней нашёлся конфиг) или это корень сервера
        while (missing.TryPop(out var dir))
        {
            await files.CreateDirectoryAsync(dir, ct).ConfigureAwait(false);
            ls.Forget(RemotePath.Parent(dir));
            FolderExists = true;
        }
        await HostedSafeWrite.WriteAsync(files, Root, file.Remote, content,
            () => MarkSwap(Id, file.Remote, content), () => ClearSwap(Id, file.Remote), keepBak: false, ct).ConfigureAwait(false);
        ls.Forget(parent);
    }

    /// <summary>Файл целиком (до конца: оборванная передача у G-Portal роняет соединение).</summary>
    private static async Task<byte[]> DownloadAsync(IRemoteFiles files, string path, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await files.DownloadAsync(path, buffer, null, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Текст как у файла на диске: кодировка по BOM, без него UTF-8.</summary>
    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool HasBom(byte[] bytes) => bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);

    // ---------- прерванная запись

    /// <summary>
    /// Запись сорвалась посередине (связь оборвалась между «прежний файл → bak» и «временный → на место», и вернуть bak
    /// тоже не вышло): по отметкам на этой машине вернуть bak на место. Файл на месте уже есть — убрать лишний bak; если
    /// это не тот файл, что мы заливали (его, пока запись стояла недоделанной, заново создал мод), — сказать об этом.
    /// Ответ — что случилось (на языке окна). Сервер отказал — отметка остаётся до следующего раза; обрыв связи —
    /// исключение, как у любой операции.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RecoverAsync(IRemoteFiles files, string profileId, string root, CancellationToken ct)
    {
        var warnings = new List<string>();
        foreach (var (marker, path, fingerprint) in Swaps(profileId))
        {
            try
            {
                var ls = new HostedListings(files, root);
                var bak = path + HostedSafeWrite.BakSuffix;
                var tmp = path + HostedSafeWrite.TmpSuffix;
                var hasBak = await ls.FileAsync(bak, ct).ConfigureAwait(false) is not null;
                if (await ls.FileAsync(path, ct).ConfigureAwait(false) is null)
                {
                    if (hasBak)
                    {
                        await files.RenameAsync(bak, path, ct).ConfigureAwait(false);
                        warnings.Add(Loc.T("mcfg.hostedRestored", RemotePath.Name(path)));
                    }
                }
                else if (hasBak)
                {
                    // встал наш новый файл (ответ на переименование потерялся) — bak лишний. Другой — его создал заново мод
                    // (сервер запускали, он записал стандартные значения): прежняя версия есть в копиях на этой машине
                    // (до записи без копии ничего не пишем), но человек должен об этом знать
                    var recreated = fingerprint is not null
                                    && Fingerprint(await DownloadAsync(files, path, ct).ConfigureAwait(false)) != fingerprint;
                    await files.DeleteFileAsync(bak, ct).ConfigureAwait(false);
                    if (recreated) warnings.Add(Loc.T("mcfg.hostedRecreated", RemotePath.Name(path)));
                }
                // временный файл той записи (связь оборвалась раньше, чем его убрали) — тоже не нужен
                if (await ls.FileAsync(tmp, ct).ConfigureAwait(false) is not null) await files.DeleteFileAsync(tmp, ct).ConfigureAwait(false);
                Quietly(() => File.Delete(marker));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                           || ex is IOException && ex is not RemoteFilesException { Kind: RemoteFailure.Connect or RemoteFailure.Timeout })
            {
                // сервер не дал (права, место) — отметка остаётся, попробуем при следующей операции
            }
        }
        return warnings;
    }

    private static string SwapDir(string profileId) => Path.Combine(HostedJournal.DirFor(profileId), SwapDirName);

    /// <summary>Отметка на файл: имя — по пути на сервере, внутри — сам путь и отпечаток заливаемого содержимого.</summary>
    private static string SwapFile(string profileId, string path) =>
        Path.Combine(SwapDir(profileId), Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path)))[..16] + ".txt");

    /// <summary>Отпечаток содержимого: по нему восстановление узнаёт, наш ли файл встал на место.</summary>
    private static string Fingerprint(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>Отметка «файл сейчас лежит на сервере как bak». Подстраховка: не записалась — запись идёт и без неё.</summary>
    private static void MarkSwap(string profileId, string path, byte[] content) =>
        Quietly(() =>
        {
            Directory.CreateDirectory(SwapDir(profileId));
            File.WriteAllText(SwapFile(profileId, path), path + "\n" + Fingerprint(content));
        });

    private static void ClearSwap(string profileId, string path) => Quietly(() => File.Delete(SwapFile(profileId, path)));

    /// <summary>
    /// Отметки профиля: файл отметки, путь на сервере и отпечаток заливаемого (нет — null). Битая отметка — как нет (и
    /// убирается).
    /// </summary>
    private static List<(string Marker, string Path, string? Fingerprint)> Swaps(string profileId)
    {
        var found = new List<(string, string, string?)>();
        var dir = SwapDir(profileId);
        try
        {
            if (!Directory.Exists(dir)) return found;
            foreach (var marker in Directory.EnumerateFiles(dir, "*.txt"))
            {
                try
                {
                    var lines = File.ReadAllText(marker).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0) throw new InvalidDataException("empty swap mark");
                    found.Add((marker, RemotePath.Normalize(lines[0]), lines.ElementAtOrDefault(1)));
                }
                catch (InvalidDataException)
                {
                    Quietly(() => File.Delete(marker));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не прочитали отметки — попробуем в следующий раз
        }
        return found;
    }

    private static void Quietly(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // подстраховка на этой машине; основное дело — на сервере
        }
    }
}
