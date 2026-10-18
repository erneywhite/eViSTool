using System.IO.Compression;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Hosting;

/// <summary>Что вернуть на сервер из архива рядом с копией мира, кроме самого мира.</summary>
[Flags]
public enum HostedWorldExtras
{
    None = 0,

    /// <summary>ModConfig — настройки модов.</summary>
    ModConfig = 1,

    /// <summary>Playerdata — роли, баны, белый список.</summary>
    Playerdata = 2,

    /// <summary>Данные модов по миру: ModData и всё в Saves, кроме самих миров (как <see cref="Server.WorldModData"/>).</summary>
    ModData = 4,

    /// <summary>serverconfig.json. По умолчанию не трогается: в нём пароль сервера, порт и настройки хостера.</summary>
    ServerConfig = 8,
}

/// <summary>
/// Архив «&lt;копия&gt;.extra.zip» рядом с копией мира сервера у хостера: serverconfig.json, ModConfig/**, Playerdata/**,
/// ModData/**, Saves/** без файлов миров (данные модов по миру, как у <see cref="Server.WorldModData"/> своего сервера) и
/// mods.json — список модов последнего скана (modid, версия, файл) без самих архивов модов. Пути в архиве — от папки данных
/// сервера, имена верхних папок — как их пишет игра.
/// </summary>
internal static class HostedWorldExtra
{
    public const string ConfigEntry = "serverconfig.json";
    public const string ModsEntry = "mods.json";
    public const string ModConfigDir = "ModConfig";
    public const string PlayerdataDir = "Playerdata";
    public const string ModDataDir = "ModData";

    private static readonly string[] Folders = [ModConfigDir, PlayerdataDir, ModDataDir, HostedWorldLocator.SavesDir];
    private const int MaxDepth = 16;

    /// <summary>Файл мира и его спутники (журнал SQLite, наши временные) — не данные модов.</summary>
    private static bool IsWorldFile(string name) => name.Contains(HostedWorldLocator.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Наши временные файлы записи на сервер — не данные.</summary>
    private static bool IsOurTemp(string name) =>
        name.EndsWith(HostedSafeWrite.TmpSuffix, StringComparison.Ordinal) || name.EndsWith(HostedSafeWrite.BakSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Собрать архив с сервера. Папок нет — пропускаются; файл пропал между листингом и скачиванием — тоже. Архив пишется
    /// под временным именем и встаёт на место целым. Возвращает размер архива.
    /// </summary>
    public static async Task<long> PackAsync(IRemoteFiles files, string root, HostedLayout layout, IReadOnlyList<LocalMod>? mods, string archive,
        IProgress<HostedTransfer>? progress, CancellationToken ct)
    {
        var ls = new HostedListings(files, root);
        var dataDir = layout.DataDir ?? layout.Root;
        var items = new List<(RemoteEntry Entry, string Name)>();
        if (layout.ConfigPath is { } config && await ls.FileAsync(config, ct).ConfigureAwait(false) is { } entry)
            items.Add((entry, ConfigEntry));
        foreach (var top in Folders)
            if (await HostedWorldLocator.DirAsync(ls, dataDir, top, ct).ConfigureAwait(false) is { } dir)
                await CollectAsync(ls, dir, top, top == HostedWorldLocator.SavesDir, 0, items, ct).ConfigureAwait(false);

        var total = items.Sum(i => i.Entry.Size);
        long done = 0;
        progress?.Report(new HostedTransfer(HostedBackupStep.Extra, 0, total));
        var tmp = archive + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                foreach (var (file, name) in items)
                {
                    byte[] bytes;
                    try
                    {
                        // shortcut: файл целиком в памяти — настройки и данные модов обычно мелкие; переделать на поток во
                        // временный файл, если встретятся данные модов в сотни мегабайт
                        using var buffer = new MemoryStream();
                        await files.DownloadAsync(file.Path, buffer, null, ct).ConfigureAwait(false);
                        bytes = buffer.ToArray();
                    }
                    catch (RemoteFilesException ex) when (ex.Kind == RemoteFailure.NotFound)
                    {
                        continue; // удалили, пока собирали архив
                    }
                    var zipEntry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    if (ZipTime(file.Modified) is { } time) zipEntry.LastWriteTime = time;
                    using (var target = zipEntry.Open()) target.Write(bytes);
                    done += file.Size;
                    progress?.Report(new HostedTransfer(HostedBackupStep.Extra, done, total));
                }
                if (mods is not null)
                {
                    using var w = new StreamWriter(zip.CreateEntry(ModsEntry, CompressionLevel.Optimal).Open());
                    w.Write(ModsJson(mods));
                }
            }
            File.Move(tmp, archive, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return new FileInfo(archive).Length;
    }

    private static async Task CollectAsync(HostedListings ls, string dir, string prefix, bool skipWorlds, int depth,
        List<(RemoteEntry, string)> items, CancellationToken ct)
    {
        if (depth > MaxDepth) return; // ссылка на саму себя или что-то вроде — дальше не идём
        foreach (var e in (await ls.ListAsync(dir, ct).ConfigureAwait(false) ?? []).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (IsOurTemp(e.Name) || skipWorlds && IsWorldFile(e.Name)) continue;
            var name = prefix + "/" + e.Name;
            if (e.IsDirectory) await CollectAsync(ls, e.Path, name, skipWorlds, depth + 1, items, ct).ConfigureAwait(false);
            else items.Add((e, name));
        }
    }

    /// <summary>Время файла для записи архива: в ZIP помещается только 1980–2107; MLSD отдаёт UTC, LIST — время сервера.</summary>
    private static DateTimeOffset? ZipTime(DateTime? modified)
    {
        if (modified is not { } m || m.Year is < 1981 or > 2106) return null;
        return m.Kind == DateTimeKind.Utc ? new DateTimeOffset(m) : new DateTimeOffset(DateTime.SpecifyKind(m, DateTimeKind.Unspecified), TimeSpan.Zero);
    }

    /// <summary>Список модов последнего скана: modid, название, версия, файл и путь на сервере, выключен ли, мод хостера.</summary>
    public static string ModsJson(IReadOnlyList<LocalMod> mods) =>
        new JObject
        {
            ["mods"] = new JArray(mods.Select(m => new JObject
            {
                ["modid"] = m.Info?.ModId,
                ["name"] = m.Info?.Name,
                ["version"] = m.Info?.Version,
                ["file"] = RemotePath.Name(m.Path),
                ["path"] = m.Path,
                ["disabled"] = m.IsDisabled,
                ["hoster"] = m.IsHosterMod,
            })),
        }.ToString(Formatting.Indented);

    // ---------- восстановление

    /// <summary>Записи архива, которые относятся к выбранному: (имя записи, верхняя папка или null у serverconfig.json).</summary>
    private static IEnumerable<(ZipArchiveEntry Entry, string? Top)> Selected(ZipArchive zip, HostedWorldExtras parts)
    {
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue; // папка
            if (entry.FullName == ConfigEntry)
            {
                if (parts.HasFlag(HostedWorldExtras.ServerConfig)) yield return (entry, null);
                continue;
            }
            var top = entry.FullName.Split('/')[0];
            var wanted = top switch
            {
                ModConfigDir => parts.HasFlag(HostedWorldExtras.ModConfig),
                PlayerdataDir => parts.HasFlag(HostedWorldExtras.Playerdata),
                ModDataDir or HostedWorldLocator.SavesDir => parts.HasFlag(HostedWorldExtras.ModData) && !IsWorldFile(entry.Name),
                _ => false, // mods.json и чужое — не на сервер
            };
            if (wanted) yield return (entry, top);
        }
    }

    /// <summary>
    /// Проверить архив до того, как на сервере что-то тронуто: каждая нужная запись читается целиком (битый архив
    /// обнаружится здесь), пути — только внутри своих папок, без «..». Не так — <see cref="InvalidDataException"/>.
    /// </summary>
    public static void Check(string archive, HostedWorldExtras parts)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var (entry, _) in Selected(zip, parts))
        {
            _ = RemotePath.Normalize(entry.FullName); // «..» и управляющие знаки — InvalidDataException
            using var s = entry.Open();
            s.CopyTo(Stream.Null);
        }
    }

    /// <summary>
    /// Вернуть выбранное из архива на сервер: каждый файл — через временный и bak (<see cref="HostedSafeWrite"/>), с
    /// отметкой на этой машине — оборвётся посередине, bak вернёт следующий скан модов. serverconfig.json — как его пишет
    /// <see cref="HostedMods"/> (прежний остаётся на сервере как bak). Недостающие папки создаются. Файлы на сервере, которых
    /// нет в архиве, не удаляются. Возвращает, сколько файлов записано.
    /// </summary>
    public static async Task<int> RestoreAsync(IRemoteFiles files, string root, HostedLayout layout, string profileId, string archive,
        HostedWorldExtras parts, CancellationToken ct, IProgress<HostedTransfer>? progress = null)
    {
        var ls = new HostedListings(files, root);
        var dataDir = layout.DataDir ?? layout.Root;
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);
        var written = 0;
        using var zip = ZipFile.OpenRead(archive);
        var selected = Selected(zip, parts).ToList();
        foreach (var (entry, top) in selected)
        {
            // у G-Portal каждый файл — несколько команд (временный, bak, переименование): без хода окно казалось зависшим
            progress?.Report(new HostedTransfer(HostedBackupStep.ExtraUpload, written, selected.Count));
            byte[] bytes;
            using (var s = entry.Open())
            using (var buffer = new MemoryStream())
            {
                s.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            if (top is null)
            {
                var config = layout.ConfigPath ?? RemotePath.Combine(dataDir, ConfigEntry);
                await HostedSafeWrite.WriteAsync(files, root, config, bytes,
                    () => HostedJournal.MarkConfigSwap(profileId, config), () => HostedJournal.ClearConfigSwap(profileId), keepBak: true, ct)
                    .ConfigureAwait(false);
                ls.Forget(RemotePath.Parent(config));
                // выключенные моды теперь — из копии, а не из нашей последней правки: проверке «хостер переписал конфиг»
                // сравнивать больше не с чем. LastLaunchMods тоже из копии: запомнить его, иначе следующий скан принял бы
                // его за новый запуск сервера и отметил в истории модов запуск, которого не было
                var state = HostedState.Load(profileId);
                state.WrittenDisabled = null;
                state.LaunchAtWrite = null;
                state.LastLaunch = LastLaunchIn(bytes);
                state.Save(profileId);
                written++;
                continue;
            }
            if (!dirs.TryGetValue(top, out var topDir))
                dirs[top] = topDir = await HostedWorldLocator.DirAsync(ls, dataDir, top, ct).ConfigureAwait(false) ?? RemotePath.Combine(dataDir, top);
            var path = RemotePath.Combine(topDir, entry.FullName[(top.Length + 1)..]);
            await EnsureDirAsync(files, ls, dataDir, RemotePath.Parent(path), ct).ConfigureAwait(false);
            await HostedSafeWrite.WriteAsync(files, root, path, bytes,
                () => HostedModConfigSource.MarkSwap(profileId, path, bytes), () => HostedModConfigSource.ClearSwap(profileId, path),
                keepBak: false, ct).ConfigureAwait(false);
            ls.Forget(RemotePath.Parent(path));
            written++;
        }
        progress?.Report(new HostedTransfer(HostedBackupStep.ExtraUpload, written, selected.Count));
        return written;
    }

    /// <summary>LastLaunchMods возвращённого serverconfig.json (как его помнит <see cref="HostedState"/>); конфиг не читается — null.</summary>
    private static string? LastLaunchIn(byte[] config)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(config)); // BOM, если есть, снимается
            return HostedMods.LastLaunchOf(ModConfigEditor.ParseText(reader.ReadToEnd()));
        }
        catch (JsonException)
        {
            return null; // битый конфиг скан тоже не разберёт — сравнивать ему будет не с чем
        }
    }

    /// <summary>Папка есть — ничего; нет — создать недостающие сверху вниз (выше папки данных не поднимаемся: она есть).</summary>
    private static async Task EnsureDirAsync(IRemoteFiles files, HostedListings ls, string dataDir, string dir, CancellationToken ct)
    {
        var missing = new Stack<string>();
        for (var d = dir; RemotePath.IsInside(dataDir, d) && d != RemotePath.Normalize(dataDir) && !await ls.IsDirectoryAsync(d, ct).ConfigureAwait(false);
             d = RemotePath.Parent(d))
            missing.Push(d);
        while (missing.TryPop(out var d))
        {
            await files.CreateDirectoryAsync(d, ct).ConfigureAwait(false);
            ls.Forget(RemotePath.Parent(d));
        }
    }
}
