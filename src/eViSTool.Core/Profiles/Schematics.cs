using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Core.Profiles;

/// <summary>Схема WorldEdit: имя файла в папке WorldEdit, размер, когда сохранена.</summary>
public sealed record SchematicEntry(string Name, long Size, DateTime ModifiedUtc);

/// <summary>Имя схемы для запросов к агенту (чтение, удаление).</summary>
public sealed record SchematicNameRequest(string Name);

/// <summary>
/// Постройки, сохранённые WorldEdit («/we export имя») — файлы *.json в папке WorldEdit папки данных. Там же их ищет
/// «/we import имя»: скопировать файл в папку другого профиля — значит перенести постройку в его миры. У сервера папка
/// своя: экспорт на сервере пишет в папку данных сервера. Схемы лежат плоско — подпапки не показываем.
/// </summary>
public static class Schematics
{
    public const string FolderName = "WorldEdit";
    public const string Extension = ".json";

    public static string DirFor(string dataDir) => Path.Combine(dataDir, FolderName);

    /// <summary>Схемы папки данных: сначала свежие. Папки ещё нет (ничего не экспортировали) — пусто.</summary>
    public static IReadOnlyList<SchematicEntry> List(string dataDir)
    {
        var dir = DirFor(dataDir);
        if (!Directory.Exists(dir)) return [];
        return [.. new DirectoryInfo(dir).EnumerateFiles("*" + Extension)
            .Select(f => new SchematicEntry(f.Name, f.Length, f.LastWriteTimeUtc))
            .OrderByDescending(e => e.ModifiedUtc)];
    }

    /// <summary>Имя схемы → файл в папке WorldEdit; путь, «..», не .json — ошибка (имя может прийти по сети).</summary>
    public static string PathOf(string dataDir, string name) =>
        IsValidName(name) && PathRules.FileIn(DirFor(dataDir), name) is { } path
            ? path
            : throw new InvalidDataException(Loc.T("schem.badName", name));

    public static bool IsValidName(string? name) =>
        name is { Length: > 5 } && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && name.IndexOfAny(['/', '\\']) < 0;

    /// <summary>
    /// Похоже ли содержимое на схему: JSON-объект (WorldEdit пишет схему одним объектом). Проверка грубая — отсечь
    /// случайно выбранный не тот файл, а не разбирать формат игры.
    /// </summary>
    public static bool LooksLikeSchematic(byte[] content)
    {
        try
        {
            using var reader = new Newtonsoft.Json.JsonTextReader(new StreamReader(new MemoryStream(content)));
            if (!reader.Read() || reader.TokenType != Newtonsoft.Json.JsonToken.StartObject) return false;
            reader.Skip();
            return !reader.Read(); // за объектом ничего нет
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return false;
        }
    }

    public static byte[] Read(string dataDir, string name)
    {
        var path = PathOf(dataDir, name);
        return File.Exists(path) ? File.ReadAllBytes(path) : throw new FileNotFoundException(Loc.T("schem.notFound", name));
    }

    /// <summary>Записать схему (есть такая — заменить): через временный файл, чтобы игра не прочла половину.</summary>
    public static void Write(string dataDir, string name, byte[] content)
    {
        var path = PathOf(dataDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".evistool.tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Убрать схему — в корзину системы (её можно вернуть).</summary>
    public static void Delete(string dataDir, string name)
    {
        var path = PathOf(dataDir, name);
        if (File.Exists(path)) RecycleBin.Send(path);
    }
}

/// <summary>Откуда карточка «Постройки» берёт схемы: папка этой машины, агент сервера на другой или FTP хостера.</summary>
public interface ISchematicSource
{
    /// <summary>Папка на этой машине (кнопка «Открыть папку»); у удалённого сервера и сервера у хостера — null.</summary>
    string? LocalFolder { get; }
    Task<IReadOnlyList<SchematicEntry>> ListAsync(CancellationToken ct = default);
    Task<byte[]> ReadAsync(string name, CancellationToken ct = default);
    Task WriteAsync(string name, byte[] content, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
}

public static class SchematicSources
{
    /// <summary>Источник схем профиля по цели (<see cref="ModTarget.For"/>): ветка хостера — первой, как в ModTargets.</summary>
    public static ISchematicSource For(ModTarget target) =>
        target.IsHosted ? new HostedSchematicSource(target)
        : target.Remote is { } code ? new RemoteSchematicSource(code)
        : new LocalSchematicSource(target.Profile.DataDir is { Length: > 0 } dir ? dir : Game.GameInstall.DefaultDataDir);

    /// <summary>Перенести схему из одного профиля в другой (одноимённая там — заменяется).</summary>
    public static async Task CopyAsync(ISchematicSource from, ISchematicSource to, string name, CancellationToken ct = default)
    {
        var content = await from.ReadAsync(name, ct).ConfigureAwait(false);
        await to.WriteAsync(name, content, ct).ConfigureAwait(false);
    }
}

/// <summary>Свой профиль: папка на этой машине (работа с диском — в фоне).</summary>
public sealed class LocalSchematicSource(string dataDir) : ISchematicSource
{
    public string? LocalFolder => Schematics.DirFor(dataDir);
    public Task<IReadOnlyList<SchematicEntry>> ListAsync(CancellationToken ct = default) => Task.Run(() => Schematics.List(dataDir), ct);
    public Task<byte[]> ReadAsync(string name, CancellationToken ct = default) => Task.Run(() => Schematics.Read(dataDir, name), ct);
    public Task WriteAsync(string name, byte[] content, CancellationToken ct = default) => Task.Run(() => Schematics.Write(dataDir, name, content), ct);
    public Task DeleteAsync(string name, CancellationToken ct = default) => Task.Run(() => Schematics.Delete(dataDir, name), ct);
}

/// <summary>Сервер на другой машине: те же действия через его агента.</summary>
public sealed class RemoteSchematicSource(ConnectionCode code) : ISchematicSource
{
    public string? LocalFolder => null;

    private async Task<T> With<T>(Func<AgentClient, Task<T>> call)
    {
        using var agent = AgentClient.ForRemote(code);
        return await call(agent).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SchematicEntry>> ListAsync(CancellationToken ct = default) => With(a => a.SchematicsAsync(ct));
    public Task<byte[]> ReadAsync(string name, CancellationToken ct = default) => With(a => a.ReadSchematicAsync(name, ct));
    public Task WriteAsync(string name, byte[] content, CancellationToken ct = default) => With(async a => { await a.WriteSchematicAsync(name, content, ct).ConfigureAwait(false); return 0; });
    public Task DeleteAsync(string name, CancellationToken ct = default) => With(async a => { await a.DeleteSchematicAsync(name, ct).ConfigureAwait(false); return 0; });
}

/// <summary>
/// Сервер у хостера: папка WorldEdit рядом с ModConfig — в папке данных сервера (где serverconfig.json; у G-Portal —
/// корень). Запись — во временный файл и переименованием, как у настроек модов.
/// </summary>
public sealed class HostedSchematicSource : ISchematicSource
{
    private readonly ModTarget _target;

    public HostedSchematicSource(ModTarget target) =>
        _target = target.IsHosted ? target : throw new ArgumentException("not a hosted target", nameof(target));

    public string? LocalFolder => null;

    private HostedTarget Hosted => _target.Hosted!;

    private Task<T> UseAsync<T>(Func<IRemoteFiles, HostedListings, Task<T>> work, bool readOnly, CancellationToken ct) =>
        HostedSessions.UseAsync(_target.ProfileId, Hosted.Endpoint,
            files => work(files, new HostedListings(files, HostedLayoutProbe.Root(Hosted.Settings))), readOnly, ct);

    /// <summary>Папка WorldEdit на сервере (как бы ни была записана по регистру); null — её ещё нет.</summary>
    private async Task<(string Path, bool Exists)> FolderAsync(IRemoteFiles files, HostedListings ls, CancellationToken ct)
    {
        var layout = HostedMods.LastScan(_target)?.Layout ?? await HostedLayoutProbe.DetectAsync(files, Hosted.Settings, ct).ConfigureAwait(false);
        var exact = RemotePath.Combine(layout.DataDir ?? layout.Root, Schematics.FolderName);
        if (await ls.IsDirectoryAsync(exact, ct).ConfigureAwait(false)) return (exact, true);
        return await ls.DirectoryIgnoreCaseAsync(exact, ct).ConfigureAwait(false) is { } other ? (other, true) : (exact, false);
    }

    private static string FileIn(string folder, string name) =>
        Schematics.IsValidName(name) && !name.Contains("..") ? RemotePath.Combine(folder, name)
        : throw new InvalidDataException(Loc.T("schem.badName", name));

    public Task<IReadOnlyList<SchematicEntry>> ListAsync(CancellationToken ct = default) => UseAsync<IReadOnlyList<SchematicEntry>>(async (files, ls) =>
    {
        var (folder, exists) = await FolderAsync(files, ls, ct).ConfigureAwait(false);
        if (!exists) return [];
        return [.. (await ls.ListAsync(folder, ct).ConfigureAwait(false) ?? [])
            .Where(e => !e.IsDirectory && Schematics.IsValidName(e.Name))
            .Select(e => new SchematicEntry(e.Name, e.Size, e.Modified ?? DateTime.MinValue))
            .OrderByDescending(e => e.ModifiedUtc)];
    }, readOnly: true, ct);

    public Task<byte[]> ReadAsync(string name, CancellationToken ct = default) => UseAsync(async (files, ls) =>
    {
        var (folder, _) = await FolderAsync(files, ls, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await files.DownloadAsync(FileIn(folder, name), buffer, null, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }, readOnly: true, ct);

    public Task WriteAsync(string name, byte[] content, CancellationToken ct = default) => UseAsync(async (files, ls) =>
    {
        var (folder, exists) = await FolderAsync(files, ls, ct).ConfigureAwait(false);
        if (!exists) await files.CreateDirectoryAsync(folder, ct).ConfigureAwait(false);
        var path = FileIn(folder, name);
        var temp = path + ".evistool.tmp";
        using (var source = new MemoryStream(content))
            await files.UploadAsync(source, temp, null, ct).ConfigureAwait(false);
        // shortcut: замена — удалить и переименовать, без bak и разбора оборванной записи (как у настроек модов);
        // оборвётся между ними — схема пропадёт, но её копия осталась в профиле-источнике. Переделать, если будут жалобы
        if (await ls.FileAsync(path, ct).ConfigureAwait(false) is not null) await files.DeleteFileAsync(path, ct).ConfigureAwait(false);
        await files.RenameAsync(temp, path, ct).ConfigureAwait(false);
        ls.Forget(folder);
        return 0;
    }, readOnly: false, ct);

    public Task DeleteAsync(string name, CancellationToken ct = default) => UseAsync(async (files, ls) =>
    {
        var (folder, _) = await FolderAsync(files, ls, ct).ConfigureAwait(false);
        await files.DeleteFileAsync(FileIn(folder, name), ct).ConfigureAwait(false);
        ls.Forget(folder);
        return 0;
    }, readOnly: false, ct);
}
