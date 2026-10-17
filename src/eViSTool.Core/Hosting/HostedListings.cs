namespace eViSTool.Core.Hosting;

/// <summary>
/// Листинги папок сервера за одну операцию: каждую папку листаем один раз, «есть ли файл» — по листингу родителя
/// (STAT, MDTM и FEAT у хостеров бывают не реализованы, LIST/MLSD есть всегда). После своей записи папку надо
/// <see cref="Forget"/>. Регистр имён важен; поиск без учёта регистра — отдельным методом.
/// </summary>
public sealed class HostedListings(IRemoteFiles files)
{
    private readonly Dictionary<string, IReadOnlyList<RemoteEntry>?> _lists = new(StringComparer.Ordinal);

    public IRemoteFiles Files => files;

    /// <summary>Содержимое папки; папки нет — null.</summary>
    public async Task<IReadOnlyList<RemoteEntry>?> ListAsync(string dir, CancellationToken ct)
    {
        dir = RemotePath.Normalize(dir);
        if (_lists.TryGetValue(dir, out var known)) return known;
        IReadOnlyList<RemoteEntry>? list;
        try
        {
            // «.» и «..» иные серверы отдают в листинге — это не файлы папки
            list = [.. (await files.ListAsync(dir, ct).ConfigureAwait(false)).Where(e => e.Name is not ("." or ".." or ""))];
        }
        catch (RemoteFilesException ex) when (ex.Kind is RemoteFailure.NotFound or RemoteFailure.Denied)
        {
            list = null;
        }
        _lists[dir] = list;
        return list;
    }

    /// <summary>Файл или папка по точному пути; нет — null. Сначала проверяется, что есть сам родитель.</summary>
    public async Task<RemoteEntry?> EntryAsync(string path, CancellationToken ct)
    {
        path = RemotePath.Normalize(path);
        if (path == "/") return null;
        var parent = RemotePath.Parent(path);
        if (parent != "/" && await EntryAsync(parent, ct).ConfigureAwait(false) is not { IsDirectory: true }) return null;
        var name = RemotePath.Name(path);
        return (await ListAsync(parent, ct).ConfigureAwait(false))?.FirstOrDefault(e => e.Name == name);
    }

    public async Task<RemoteEntry?> FileAsync(string path, CancellationToken ct) =>
        await EntryAsync(path, ct).ConfigureAwait(false) is { IsDirectory: false } e ? e : null;

    public async Task<bool> IsDirectoryAsync(string path, CancellationToken ct) =>
        RemotePath.Normalize(path) == "/" || await EntryAsync(path, ct).ConfigureAwait(false) is { IsDirectory: true };

    /// <summary>
    /// Папка по пути без учёта регистра (каждая часть: сначала точное имя, потом без регистра). Возвращает настоящий
    /// путь на сервере; нет — null.
    /// </summary>
    public async Task<string?> DirectoryIgnoreCaseAsync(string path, CancellationToken ct)
    {
        var current = "/";
        foreach (var part in RemotePath.Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var dirs = (await ListAsync(current, ct).ConfigureAwait(false))?.Where(e => e.IsDirectory).ToList();
            if (dirs is null) return null;
            var hit = dirs.FirstOrDefault(e => e.Name == part)
                      ?? dirs.FirstOrDefault(e => string.Equals(e.Name, part, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return null;
            current = RemotePath.Combine(current, hit.Name);
        }
        return current;
    }

    /// <summary>Папку поменяли (своя запись) — следующий запрос перечитает её.</summary>
    public void Forget(string dir) => _lists.Remove(RemotePath.Normalize(dir));
}
