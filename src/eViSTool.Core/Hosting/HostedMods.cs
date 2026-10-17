using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Операции с модами сервера у хостера — то же, что <see cref="ModTargets"/> делает со своим профилем, но по FTP:
/// скан (листинг + кэш modinfo), установка через временный файл, вкл/выкл в serverconfig.json, удаление с копией
/// на этой машине, отпечаток для опроса изменений со стороны. Каждый публичный метод — одна очередь соединения профиля
/// (<see cref="HostedSessions"/>); всё внутри идёт по уже выданному соединению.
/// <para>
/// Ничего из этого не трогает диск этой машины по путям сервера: пути модов здесь — пути на FTP («/Mods/x.zip»), а на
/// Windows такой путь значил бы C:\Mods\x.zip. На этой машине пишется только своё: кэш, журнал, копии, история.
/// </para>
/// </summary>
public sealed partial class HostedMods(ModTarget target)
{
    public ModTarget Target { get; } = target.IsHosted ? target : throw new ArgumentException("not a hosted target", nameof(target));

    /// <summary>Раскладка, найденная последним сканом этого экземпляра (null — ещё не сканировали).</summary>
    public HostedLayout? Layout { get; private set; }

    /// <summary>Откуда взялась версия игры при последнем скане (null — не нашлась или ещё не сканировали).</summary>
    public GameVersionSource? VersionSource { get; private set; }

    // раскладка последнего скана профиля — для отпечатка: опрос раз в 30 с не должен заново искать папки и читать лог
    private static readonly ConcurrentDictionary<string, HostedLayout> LastLayouts = new(StringComparer.Ordinal);

    // моды последнего скана профиля — «Настройки модов» привязывают по ним файлы к модам, не сканируя заново
    private static readonly ConcurrentDictionary<string, IReadOnlyList<LocalMod>> LastMods = new(StringComparer.Ordinal);

    /// <summary>
    /// Раскладка и моды последнего скана профиля в этом окне; скана ещё не было или корень сервера в профиле с тех пор
    /// сменили — null.
    /// </summary>
    public static (HostedLayout Layout, IReadOnlyList<LocalMod> Mods)? LastScan(ModTarget target) =>
        target.Hosted is { } hosted
        && LastLayouts.TryGetValue(target.ProfileId, out var layout) && layout.Root == HostedLayoutProbe.Root(hosted.Settings)
        && LastMods.TryGetValue(target.ProfileId, out var mods)
            ? (layout, mods)
            : null;

    /// <summary>
    /// Скан на уже выданном соединении (для тех, кто сам держит очередь профиля): моды и разовые предупреждения скана
    /// (довели прерванную установку, вернули конфиг, хостер переписал конфиг). Второй раз скан их не выдаст, поэтому
    /// показать их должен вызывающий. Постоянные («нет конфига», папки-моды) сюда не входят: их покажут «Мои моды»
    /// своим сканом. Раскладка — в <see cref="Layout"/>.
    /// </summary>
    internal async Task<(IReadOnlyList<LocalMod> Mods, IReadOnlyList<string> Once)> ScanModsAsync(IRemoteFiles files, CancellationToken ct)
    {
        var scan = await ScanCoreAsync(files, null, ct).ConfigureAwait(false);
        return (scan.Mods, scan.Once);
    }

    private HostedTarget Hosted => Target.Hosted!;
    private string Id => Target.ProfileId;
    private string Root => HostedLayoutProbe.Root(Hosted.Settings);

    /// <summary>
    /// Что нашёл скан: профиль и моды, плюс то, что нужно операциям (записи листинга, папки-моды), и разовые
    /// предупреждения из <see cref="ResolvedProfile.Warnings"/> — те, что следующий скан уже не повторит.
    /// </summary>
    private sealed record Scan(ResolvedProfile Profile, IReadOnlyList<LocalMod> Mods, HostedSnapshot Snapshot,
        IReadOnlyDictionary<string, RemoteEntry> Entries, IReadOnlySet<string> Folders, IReadOnlyList<string> Once);

    public Task<(ResolvedProfile Profile, IReadOnlyList<LocalMod> Mods)> ScanAsync(IProgress<string>? progress, CancellationToken ct) =>
        HostedSessions.UseAsync<(ResolvedProfile, IReadOnlyList<LocalMod>)>(Id, Hosted.Endpoint, async files =>
        {
            var scan = await ScanCoreAsync(files, progress, ct).ConfigureAwait(false);
            return (scan.Profile, scan.Mods);
        }, readOnly: true, ct);

    public Task SetEnabledAsync(string path, bool enabled, CancellationToken ct) =>
        HostedSessions.UseAsync(Id, Hosted.Endpoint, async files =>
        {
            var scan = await ScanCoreAsync(files, null, ct).ConfigureAwait(false);
            var mod = Find(scan, path);
            EnsureWritable(scan, mod);
            var info = mod.Info ?? throw new InvalidOperationException(mod.Error ?? mod.FileName);
            var config = scan.Profile.ConfigPath ?? throw new InvalidOperationException(Loc.T("hosted.noConfig"));
            return await EditConfigAsync(files, config, root => ModConfigEditor.SetEnabled(root, ProfileKind.Server, info, enabled), ct)
                .ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Удалить мод: сначала копия в хранилище версий этой машины (корзины на FTP нет), потом удаление на сервере, запись
    /// в историю «версия → нет»; других копий мода не осталось — он уходит и из списка выключенных.
    /// </summary>
    public Task DeleteAsync(string path, CancellationToken ct) =>
        HostedSessions.UseAsync(Id, Hosted.Endpoint, async files =>
        {
            var scan = await ScanCoreAsync(files, null, ct).ConfigureAwait(false);
            var mod = Find(scan, path);
            EnsureWritable(scan, mod);

            var dir = HostedJournal.OldDirFor(Id);
            try
            {
                Directory.CreateDirectory(dir);
                var copy = Path.Combine(dir, RemotePath.SafeLocalName(RemotePath.Name(mod.Path)));
                await using (var file = File.Create(copy))
                    await files.DownloadAsync(mod.Path, file, null, ct).ConfigureAwait(false);
                ModBackupStore.ForProfileId(Id).Keep(new LocalMod(copy, mod.Info, null));
            }
            finally
            {
                DeleteLocalDir(dir);
            }

            await files.DeleteFileAsync(mod.Path, ct).ConfigureAwait(false);
            if (mod.Info is { } gone) ModHistory.Record(ModHistory.FileFor(Id), gone.ModId, gone.Name, gone.Version, null);

            // других копий мода не осталось — убираем его и из списка выключенных
            if (mod.Info is { } info && scan.Profile.ConfigPath is { } config
                && scan.Mods.Count(m => m.Info?.ModId == info.ModId) == 1)
                await EditConfigAsync(files, config, root => ModConfigEditor.SetEnabled(root, ProfileKind.Server, info, enabled: true), ct)
                    .ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>
    /// Отпечаток набора модов: SHA-1 от «имя|размер|время» файлов папок модов и от размера и времени конфига. Папки — из
    /// раскладки прошлого скана (1–2 листинга на опрос); скана ещё не было — раскладка ищется.
    /// </summary>
    public Task<string> StampAsync(CancellationToken ct) =>
        HostedSessions.UseAsync(Id, Hosted.Endpoint, async files =>
        {
            var root = Root;
            var layout = LastLayouts.TryGetValue(Id, out var known) && known.Root == root
                ? known
                : (await HostedLayoutProbe.ReadAsync(files, Hosted.Settings, ct).ConfigureAwait(false)).Layout;
            var ls = new HostedListings(files, root);
            var text = new StringBuilder();
            foreach (var dir in layout.ModDirs)
            {
                text.Append(dir).Append('\n');
                var list = await ls.ListAsync(dir, ct).ConfigureAwait(false);
                if (list is null) text.Append("(gone)\n");
                foreach (var e in (list ?? []).OrderBy(e => e.Name, StringComparer.Ordinal))
                    text.Append(e.Name).Append('|').Append(e.Size).Append('|').Append(e.Modified?.Ticks).Append('\n');
            }
            if (layout.ConfigPath is { } config && await ls.FileAsync(config, ct).ConfigureAwait(false) is { } entry)
                text.Append(config).Append('|').Append(entry.Size).Append('|').Append(entry.Modified?.Ticks).Append('\n');
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }, readOnly: true, ct);

    // ---------- скан

    private async Task<Scan> ScanCoreAsync(IRemoteFiles files, IProgress<string>? progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        var once = new List<string>(); // разовые: и в warnings, и отдельно (см. ScanModsAsync)
        void Once(string warning)
        {
            warnings.Add(warning);
            once.Add(warning);
        }

        // прерванная установка (связь оборвалась между «удалить старое» и «переименовать новое») — довести или вернуть
        if (HostedJournal.Read(Id) is { } pending)
        {
            try
            {
                var name = pending.Name ?? pending.ModId;
                Once(await HostedJournal.FinishAsync(files, Id, pending, ct, Root).ConfigureAwait(false)
                    ? Loc.T("hosted.recovered", name)
                    : Loc.T("hosted.rolledBack", name));
            }
            catch (Exception ex) when (IsNotADrop(ex))
            {
                // сервер не дал довести (права, место) или подвёл диск этой машины — журнал остаётся, попробуем при
                // следующем скане; новую установку до тех пор не начинаем (InstallCoreAsync)
            }
        }

        // запись конфига сорвалась посередине: прежний конфиг остался только как bak — возвращаем его на место
        if (HostedJournal.ConfigSwap(Id) is { } swapped)
        {
            try
            {
                var ls = new HostedListings(files, Root);
                if (await ls.FileAsync(swapped, ct).ConfigureAwait(false) is null
                    && await ls.FileAsync(swapped + BakSuffix, ct).ConfigureAwait(false) is not null)
                {
                    await files.RenameAsync(swapped + BakSuffix, swapped, ct).ConfigureAwait(false);
                    Once(Loc.T("hosted.configRestored"));
                }
                HostedJournal.ClearConfigSwap(Id);
            }
            catch (Exception ex) when (IsNotADrop(ex))
            {
                // не вышло — отметка остаётся, попробуем при следующем скане
            }
        }

        // то же для настроек модов, записанных из «Настроек модов»
        foreach (var restored in await HostedModConfigSource.RecoverAsync(files, Id, Root, ct).ConfigureAwait(false)) Once(restored);

        var settings = Hosted.Settings;
        var snapshot = await HostedLayoutProbe.ReadAsync(files, settings, ct).ConfigureAwait(false);
        var layout = snapshot.Layout;
        Layout = layout;
        LastLayouts[Id] = layout;
        warnings.AddRange(layout.Warnings);

        var disabledList = snapshot.ConfigJson is { } json ? ProfileResolver.ReadServerConfig(json).Disabled : [];
        var (version, source) = await HostedGameVersion.DetectAsync(files, settings, snapshot, Id, ct).ConfigureAwait(false);
        VersionSource = source;

        var mods = new List<LocalMod>();
        var entries = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var zips = new List<RemoteEntry>();
        foreach (var dir in layout.ModDirs)
        {
            var hoster = HostedLayoutProbe.IsHosterDir(dir);
            foreach (var e in await snapshot.Listings.ListAsync(dir, ct).ConfigureAwait(false) ?? [])
            {
                if (e.IsDirectory)
                {
                    if (await ReadFolderModAsync(files, snapshot.Listings, e, ct).ConfigureAwait(false) is { } folderMod)
                    {
                        mods.Add(folderMod);
                        folders.Add(e.Path);
                        entries[e.Path] = e;
                    }
                    continue;
                }
                var ext = Path.GetExtension(e.Name);
                if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zips.Add(e);
                    entries[e.Path] = e;
                }
                else if ((ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                         && !HostedLayoutProbe.IsBaseGameFile(e.Name))
                {
                    mods.Add(new LocalMod(e.Path, null, hoster ? null : Loc.T("scan.codeModUnsupported")));
                    entries[e.Path] = e;
                }
                // остальное (базовые dll и pdb игры, txt, json менеджеров, наши .evistool.tmp) игра модами не считает
            }
        }

        mods.AddRange(await ReadZipsAsync(files, zips, progress, ct).ConfigureAwait(false));

        var disabled = new HashSet<string>(disabledList); // игра сравнивает с учётом регистра
        var result = mods.Select(l => l with
            {
                IsDisabled = ModUpdateService.IsDisabled(l, disabled),
                IsHosterMod = HostedLayoutProbe.IsHosterDir(RemotePath.Parent(l.Path)),
            })
            .OrderBy(m => m.Info?.Name ?? m.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        LastMods[Id] = result;
        if (folders.Count > 0) warnings.Add(Loc.T("hosted.folderModReadOnly"));
        var tracked = warnings.Count;
        TrackLaunches(snapshot, disabledList, warnings);
        once.AddRange(warnings.Skip(tracked)); // «хостер переписал конфиг» TrackLaunches выдаёт один раз

        var resolved = new ResolvedProfile
        {
            Profile = Target.Profile,
            GameVersion = version,
            ConfigPath = layout.ConfigPath,
            ModDirs = layout.ModDirs,
            InstallDir = layout.InstallDir,
            DisabledMods = disabledList,
            Warnings = warnings,
            OffMachine = true, // пути — на FTP
        };
        return new Scan(resolved, result, snapshot, entries, folders, once);
    }

    /// <summary>modinfo архивов: из кэша, а промахи — с сервера (по возможности не целиком), с прогрессом «N из M».</summary>
    private async Task<IReadOnlyList<LocalMod>> ReadZipsAsync(IRemoteFiles files, IReadOnlyList<RemoteEntry> zips, IProgress<string>? progress,
        CancellationToken ct)
    {
        var cache = ModInfoCache.ForProfile(Id);
        var misses = zips.Count(z => !cache.TryGet(z, out _));
        var read = 0;
        var tempDir = Path.Combine(ModInfoCache.DirFor(Id), "dl");
        var mods = new List<LocalMod>();
        try
        {
            foreach (var zip in zips)
            {
                if (!cache.TryGet(zip, out var info))
                {
                    progress?.Report(Loc.T("hosted.readingProgress", ++read, misses));
                    try
                    {
                        info = await ZipProbe.ReadModInfoAsync(new RemoteRangeSource(files, zip, tempDir), ct).ConfigureAwait(false);
                    }
                    catch (RemoteFilesException ex) when (ex.Kind is RemoteFailure.NotFound or RemoteFailure.Denied)
                    {
                        // файл убрали или не дают читать: показываем ошибкой и не кэшируем
                        mods.Add(new LocalMod(zip.Path, null, Loc.T("scan.readFailed", HostedSecret.Describe(ex))));
                        continue;
                    }
                    cache.Put(zip, info);
                }
                mods.Add(ModScanner.FromCached(zip.Path, info));
            }
        }
        finally
        {
            // кэш — даже после обрыва посреди: прочитанное не придётся читать снова
            cache.Retain(zips.Select(z => z.Path));
            cache.Save();
        }
        return mods;
    }

    /// <summary>Распакованный мод (папка с modinfo.json); папка без него — не мод (null).</summary>
    private static async Task<LocalMod?> ReadFolderModAsync(IRemoteFiles files, HostedListings ls, RemoteEntry dir, CancellationToken ct)
    {
        var list = await ls.ListAsync(dir.Path, ct).ConfigureAwait(false);
        var info = list?.FirstOrDefault(e => !e.IsDirectory && e.Name.Equals("modinfo.json", StringComparison.OrdinalIgnoreCase));
        if (info is null) return null;
        if (info.Size > ZipProbe.MaxModInfoSize) return new LocalMod(dir.Path, null, Loc.T("scan.corrupt"));
        try
        {
            return ModScanner.FromModInfo(dir.Path, await files.ReadTextAsync(info.Path, ct).ConfigureAwait(false));
        }
        catch (RemoteFilesException ex) when (ex.Kind is RemoteFailure.NotFound or RemoteFailure.Denied)
        {
            return new LocalMod(dir.Path, null, Loc.T("scan.readFailed", HostedSecret.Describe(ex)));
        }
    }

    /// <summary>
    /// Запуски сервера — по LastLaunchMods: сервер пишет туда при старте включённые моды, нужные клиенту, и только если
    /// набор поменялся (ServerSystemHeartbeat.OnBeginRunGame игры). Поменялся — отметка запуска в истории (история
    /// разбивается на сеансы по запускам).
    /// <para>
    /// Проверка «хостер переписал конфиг» от этого не зависит: если хостер при старте вернул прежний конфиг, сервер
    /// загрузит тот же набор, что и в прошлый раз, и LastLaunchMods не изменится. Поэтому сравниваем то, что мы сами
    /// записали в DisabledMods, с тем, что там сейчас: нашей записи нет — предупреждение. Второй признак — после нашей
    /// записи сервер запускался (LastLaunchMods другой) и всё равно загрузил выключенный мод.
    /// </para>
    /// </summary>
    private void TrackLaunches(HostedSnapshot snapshot, IReadOnlyList<string> disabled, List<string> warnings)
    {
        if (snapshot.ConfigJson is not { } config) return; // конфига нет или он битый — сравнивать не с чем
        var launch = config["LastLaunchMods"] is { Type: not JTokenType.Null } token
            ? token.Type == JTokenType.String ? (string)token! : token.ToString(Formatting.None)
            : null;
        var state = HostedState.Load(Id);
        var changed = false;

        if (state.WrittenDisabled is { Count: > 0 } written)
        {
            var reverted = written.Any(w => !disabled.Any(d => IsEntryOf(d, w)));
            var launchedSince = launch is not null && launch != state.LaunchAtWrite;
            if (!reverted && launchedSince) reverted = written.Any(LaunchedIds(launch!).Contains);
            if (reverted) warnings.Add(Loc.T("hosted.configReverted"));
            if (reverted || launchedSince)
            {
                // предупреждаем один раз; запись пережила запуск — следить дальше не за чем
                state.WrittenDisabled = null;
                state.LaunchAtWrite = null;
                changed = true;
            }
        }

        if (launch is not null && launch != state.LastLaunch)
        {
            // отметка — на момент, когда запуск заметили: время файла конфига у FTP грубое (до секунды или минуты, LIST —
            // ещё и в поясе сервера) и могло бы встать раньше изменений, сделанных до запуска. Свои изменения после
            // запуска раньше скана не бывают: каждая операция сначала сканирует.
            // shortcut: запуск с тем же набором модов не виден (LastLaunchMods не меняется) и отметки не получает;
            // переделать, если найдётся надёжный признак запуска (например, время начала лога сервера)
            if (state.LastLaunch is not null) ModHistory.MarkLaunch(ModHistory.FileFor(Id));
            state.LastLaunch = launch;
            changed = true;
        }
        if (changed) state.Save(Id);
    }

    /// <summary>id модов из LastLaunchMods (то, что сервер загрузил при запуске).</summary>
    private static HashSet<string> LaunchedIds(string launch)
    {
        try
        {
            return new HashSet<string>(JArray.Parse(launch).OfType<JObject>().Select(m => (string?)m["id"]).OfType<string>(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Запись списка выключенных — этот мод («modid» или «modid@версия»).</summary>
    private static bool IsEntryOf(string entry, string modId) =>
        entry == modId || entry.StartsWith(modId + "@", StringComparison.Ordinal);

    /// <summary>Мод по пути — только из свежего скана: чужой путь сюда не пройдёт.</summary>
    private static LocalMod Find(Scan scan, string path)
    {
        string normalized;
        try
        {
            normalized = RemotePath.Normalize(path);
        }
        catch (InvalidDataException)
        {
            throw new FileNotFoundException(Loc.T("mods.remoteNotFound", path));
        }
        return scan.Mods.FirstOrDefault(m => m.Path == normalized)
               ?? throw new FileNotFoundException(Loc.T("mods.remoteNotFound", RemotePath.Name(normalized)));
    }

    /// <summary>Мод хостера и распакованная папка — только для чтения.</summary>
    private static void EnsureWritable(Scan scan, LocalMod mod)
    {
        if (HostedLayoutProbe.IsHosterDir(RemotePath.Parent(mod.Path))) throw new InvalidOperationException(Loc.T("hosted.hosterMod"));
        if (scan.Folders.Contains(mod.Path)) throw new InvalidOperationException(Loc.T("hosted.folderModReadOnly"));
    }

    // ---------- serverconfig.json

    /// <summary>
    /// Правка serverconfig.json на сервере: скачать, поправить дерево, собрать текст в том же виде, перед записью
    /// скачать ещё раз и сравнить (на FTP нет блокировок: конфиг мог поменять сервер или второе окно) — другой текст,
    /// правка повторяется по нему, до трёх раз. false — менять нечего.
    /// </summary>
    private async Task<bool> EditConfigAsync(IRemoteFiles files, string configPath, Func<JObject, bool> edit, CancellationToken ct)
    {
        var before = await files.ReadTextAsync(configPath, ct).ConfigureAwait(false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            JObject root;
            try
            {
                root = ModConfigEditor.ParseText(before);
            }
            catch (JsonException ex)
            {
                throw new IOException(Loc.T("profile.configReadFailed", HostedLayoutProbe.ConfigName, ex.Message), ex);
            }
            if (!edit(root)) return false;
            var text = ModConfigEditor.ToText(root, before);

            var now = await files.ReadTextAsync(configPath, ct).ConfigureAwait(false);
            if (now != before)
            {
                before = now;
                continue;
            }
            SavePrevious(before);
            await WriteConfigAsync(files, configPath, text, ct).ConfigureAwait(false);
            RememberWritten(root);
            return true;
        }
        throw new IOException(Loc.T("hosted.errConfigBusy"));
    }

    private const string BakSuffix = HostedSafeWrite.BakSuffix;

    /// <summary>Сбой, после которого соединение ещё живо: сервер отказал или подвёл диск этой машины.</summary>
    private static bool IsNotADrop(Exception ex) =>
        ex is UnauthorizedAccessException
        || ex is IOException && ex is not RemoteFilesException { Kind: RemoteFailure.Connect or RemoteFailure.Timeout };

    /// <summary>
    /// Записать конфиг через временный файл (<see cref="HostedSafeWrite"/>): поверх G-Portal не переименовывает — прежний
    /// конфиг уходит в .evistool.bak и там остаётся. Сорвался и возврат — на этой машине остаётся отметка
    /// (<see cref="HostedJournal.MarkConfigSwap"/>), по ней bak вернёт следующий скан.
    /// </summary>
    private Task WriteConfigAsync(IRemoteFiles files, string configPath, string text, CancellationToken ct) =>
        HostedSafeWrite.WriteAsync(files, Root, configPath, new UTF8Encoding(false).GetBytes(text),
            () => HostedJournal.MarkConfigSwap(Id, configPath), () => HostedJournal.ClearConfigSwap(Id), keepBak: true, ct);

    /// <summary>Прежний текст конфига — копией на этой машине (cache/hosted/&lt;id&gt;/serverconfig.prev.json).</summary>
    private void SavePrevious(string text)
    {
        try
        {
            var dir = ModInfoCache.DirFor(Id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "serverconfig.prev.json"), text, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // копия — подстраховка, без неё правка всё равно идёт через bak на сервере
        }
    }

    /// <summary>Что мы записали в DisabledMods и при каком запуске — для проверки «хостер переписал конфиг».</summary>
    private void RememberWritten(JObject root)
    {
        var state = HostedState.Load(Id);
        var (_, disabled) = ProfileResolver.ReadServerConfig(root);
        state.WrittenDisabled = [.. disabled.Select(d => d.Split('@')[0])];
        state.LaunchAtWrite = root["LastLaunchMods"] is { Type: not JTokenType.Null } t
            ? t.Type == JTokenType.String ? (string)t! : t.ToString(Formatting.None)
            : null;
        state.Save(Id);
    }

    private static async Task QuietlyAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // уборка после сбоя: основную ошибку бросает вызывающий
        }
    }

    private static void DeleteLocalDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // временная папка копий — место, но не вред
        }
    }
}
