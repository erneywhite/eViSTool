using eViSTool.Core.Hosting;

namespace eViSTool.Tests.Hosting;

/// <summary>
/// Поддельный FTP над временной папкой — для тестов операций на хосте без сети. Ведёт себя как сервер на Linux:
/// регистр в именах важен (даже на Windows), «..» не пройдёт. Причуды настоящих серверов включаются свойствами;
/// <see cref="GPortal"/> — как у G-Portal (поверх не переименовывает, время MLSD в UTC до секунды).
/// Один экземпляр — и «сервер», и соединение: закрытие (DisposeAsync) только рвёт связь, следующее ConnectAsync
/// подключает снова — так пул соединений можно проверять одной подделкой. Подключает в общий файл через
/// <see cref="Register"/> (свой адрес на тест: тесты Core идут параллельно).
/// </summary>
public sealed class FolderRemoteFiles(string root) : IRemoteFiles
{
    private readonly Lock _gate = new();

    /// <summary>Папка на этой машине, которую изображает сервер.</summary>
    public string Root { get; } = Directory.CreateDirectory(root).FullName;

    /// <summary>Сервер не читает с позиции (REST): <see cref="RemoteFailure.RangeUnsupported"/>.</summary>
    public bool NoRange { get; set; }

    /// <summary>Не переименовывает поверх существующего файла (G-Portal, Pure-FTPd без опции): <see cref="RemoteFailure.Exists"/>.</summary>
    public bool NoRenameOverwrite { get; set; }

    /// <summary>Время в листинге как у LIST: местное время сервера с точностью до минуты, без пояса.</summary>
    public bool ListLocalTime { get; set; }

    /// <summary>Сбой операции: (операция, путь) → true — бросить <see cref="RemoteFilesException"/> вместо выполнения.
    /// Операции: connect, list, stat, read, download, upload, rename, delete, mkdir (у rename путь — откуда).</summary>
    public Func<string, string, bool>? FailOn { get; set; }

    /// <summary>Вид сбоя для <see cref="FailOn"/>.</summary>
    public RemoteFailure FailKind { get; set; } = RemoteFailure.Denied;

    /// <summary>Готовое исключение для <see cref="FailOn"/> вместо собранного по <see cref="FailKind"/> — когда важны его
    /// подробности (ключ сервера SFTP сменился и т. п.).</summary>
    public Func<RemoteFilesException>? FailWith { get; set; }

    /// <summary>Вызывается перед каждой операцией (операция, путь) — для гонок: «пока мы читали, файл поменяли».</summary>
    public Action<string, string>? OnBefore { get; set; }

    /// <summary>Следующая операция оборвёт соединение (<see cref="RemoteFailure.Connect"/>), потом нужно переподключиться.</summary>
    public bool DropNext { get; set; }

    /// <summary>
    /// Передачи файла (скачивание и докачка), которые рвутся, отдав столько байт: соединение падает
    /// (<see cref="RemoteFailure.Connect"/>), как у G-Portal после обрыва посреди файла. Каждая запись — один раз, по порядку.
    /// </summary>
    public List<(string Path, long After)> CutDownloads { get; } = [];

    /// <summary>Какие файлы и с каких позиций качались: обычное скачивание — 0, докачка — позиция.</summary>
    public List<(string Path, long Offset)> DownloadOffsets { get; } = [];

    public int Connects { get; private set; }

    /// <summary>Сколько раз выполнялась каждая операция.</summary>
    public Dictionary<string, int> Calls { get; } = [];

    /// <summary>Все записи: «upload /Mods/x.zip», «rename /a → /b», «delete /x», «mkdir /x».</summary>
    public List<string> Writes { get; } = [];

    public RemoteFeatures Features => new(!NoRange, true, false, "220 fake");

    public bool IsConnected { get; private set; }

    public static FolderRemoteFiles GPortal(string root) => new(root) { NoRenameOverwrite = true };

    /// <summary>Подключить подделку к фабрике соединений для своего адреса (Dispose снимает).</summary>
    public IDisposable Register(string host) => RemoteFilesFactory.RegisterHost(host, _ => this);

    /// <summary>Положить файл на «сервер» (путь — как на FTP).</summary>
    public string Put(string remotePath, byte[] bytes)
    {
        var local = Local(remotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllBytes(local, bytes);
        return local;
    }

    public string Put(string remotePath, string text) => Put(remotePath, new System.Text.UTF8Encoding(false).GetBytes(text));

    public void MakeDir(string remotePath) => Directory.CreateDirectory(Local(remotePath));

    public bool Has(string remotePath) => Exact(remotePath) is not null;

    public string Text(string remotePath) => File.ReadAllText(Exact(remotePath) ?? throw new FileNotFoundException(remotePath));

    /// <summary>Имена в папке «сервера», по порядку.</summary>
    public string[] Names(string remoteDir) =>
        Exact(remoteDir) is { } d && Directory.Exists(d)
            ? [.. Directory.EnumerateFileSystemEntries(d).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
            : [];

    public Task ConnectAsync(CancellationToken ct)
    {
        Step("connect", "/");
        IsConnected = true;
        Connects++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string dir, CancellationToken ct)
    {
        Step("list", dir);
        var local = Exact(dir);
        if (local is null || !Directory.Exists(local)) throw new RemoteFilesException(RemoteFailure.NotFound, $"550 {dir}: no such directory");
        var path = RemotePath.Normalize(dir);
        IReadOnlyList<RemoteEntry> list = [.. Directory.EnumerateFileSystemEntries(local).Select(e => EntryOf(RemotePath.Combine(path, Path.GetFileName(e)), e))];
        return Task.FromResult(list);
    }

    public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct)
    {
        Step("stat", path);
        return Task.FromResult(Exact(path) is { } local ? EntryOf(RemotePath.Normalize(path), local) : null);
    }

    public Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct)
    {
        Step("read", path);
        if (NoRange) throw new RemoteFilesException(RemoteFailure.RangeUnsupported, "502 REST not implemented");
        var local = FileOf(path);
        using var s = System.IO.File.OpenRead(local);
        s.Position = Math.Min(offset, s.Length);
        var buffer = new byte[(int)Math.Min(count, s.Length - s.Position)];
        s.ReadExactly(buffer);
        return Task.FromResult(buffer);
    }

    public async Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        Step("download", path);
        lock (_gate) DownloadOffsets.Add((RemotePath.Normalize(path), 0));
        await using var s = System.IO.File.OpenRead(FileOf(path));
        await SendAsync(path, s, destination, ct);
    }

    public async Task DownloadFromAsync(string path, long offset, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        Step("download", path);
        if (offset > 0 && NoRange) throw new RemoteFilesException(RemoteFailure.RangeUnsupported, "502 REST not implemented");
        lock (_gate) DownloadOffsets.Add((RemotePath.Normalize(path), offset));
        await using var s = System.IO.File.OpenRead(FileOf(path));
        s.Position = Math.Min(offset, s.Length);
        await SendAsync(path, s, destination, ct);
    }

    /// <summary>Передать файл до конца — или, если передача должна оборваться (<see cref="CutDownloads"/>), столько байт и обрыв.</summary>
    private async Task SendAsync(string path, Stream source, Stream destination, CancellationToken ct)
    {
        long? cut = null;
        var normalized = RemotePath.Normalize(path);
        lock (_gate)
        {
            var i = CutDownloads.FindIndex(c => c.Path == normalized);
            if (i >= 0)
            {
                cut = CutDownloads[i].After;
                CutDownloads.RemoveAt(i);
            }
        }
        if (cut is not { } left)
        {
            await source.CopyToAsync(destination, ct);
            return;
        }
        var buffer = new byte[64 * 1024];
        while (left > 0)
        {
            var n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (n == 0) return; // файл кончился раньше обрыва
            await destination.WriteAsync(buffer.AsMemory(0, n), ct);
            left -= n;
        }
        if (source.Position >= source.Length) return;
        IsConnected = false;
        throw new RemoteFilesException(RemoteFailure.Connect, "426 connection lost mid-transfer");
    }

    public async Task UploadAsync(Stream source, string path, IProgress<long>? progress, CancellationToken ct)
    {
        Step("upload", path);
        var local = NewFile(path);
        await using (var s = System.IO.File.Create(local)) await source.CopyToAsync(s, ct);
        lock (_gate) Writes.Add("upload " + RemotePath.Normalize(path));
    }

    public Task RenameAsync(string from, string to, CancellationToken ct)
    {
        Step("rename", from);
        var src = Exact(from) ?? throw new RemoteFilesException(RemoteFailure.NotFound, $"550 {from}: no such file");
        var existing = Exact(to);
        if (existing is not null && NoRenameOverwrite) throw new RemoteFilesException(RemoteFailure.Exists, "550 path already exists");
        var dest = existing ?? NewFile(to);
        if (Directory.Exists(src)) Directory.Move(src, dest);
        else System.IO.File.Move(src, dest, overwrite: true);
        lock (_gate) Writes.Add($"rename {RemotePath.Normalize(from)} → {RemotePath.Normalize(to)}");
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, CancellationToken ct)
    {
        Step("delete", path);
        System.IO.File.Delete(FileOf(path));
        lock (_gate) Writes.Add("delete " + RemotePath.Normalize(path));
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        Step("mkdir", path);
        Directory.CreateDirectory(NewFile(path));
        lock (_gate) Writes.Add("mkdir " + RemotePath.Normalize(path));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    private void Step(string op, string path)
    {
        lock (_gate) Calls[op] = Calls.GetValueOrDefault(op) + 1;
        if (op != "connect" && !IsConnected) throw new RemoteFilesException(RemoteFailure.Connect, "not connected");
        OnBefore?.Invoke(op, RemotePath.Normalize(path));
        if (DropNext && op != "connect")
        {
            DropNext = false;
            IsConnected = false;
            throw new RemoteFilesException(RemoteFailure.Connect, "421 connection lost");
        }
        if (FailOn?.Invoke(op, RemotePath.Normalize(path)) == true)
            throw FailWith?.Invoke() ?? new RemoteFilesException(FailKind, $"550 {op} {path}: refused");
    }

    private RemoteEntry EntryOf(string remote, string local)
    {
        var dir = Directory.Exists(local);
        var utc = dir ? Directory.GetLastWriteTimeUtc(local) : System.IO.File.GetLastWriteTimeUtc(local);
        var when = utc.ToLocalTime();
        var modified = ListLocalTime
            ? new DateTime(when.Year, when.Month, when.Day, when.Hour, when.Minute, 0, DateTimeKind.Unspecified)
            : new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, DateTimeKind.Utc);
        return new RemoteEntry(remote, RemotePath.Name(remote), dir, dir ? 0 : new FileInfo(local).Length, modified);
    }

    /// <summary>Путь на этой машине без проверки регистра (для подготовки данных теста).</summary>
    private string Local(string remotePath) =>
        Path.Combine([Root, .. RemotePath.Normalize(remotePath).Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    /// <summary>Существующий путь с точным регистром каждой части; нет — null.</summary>
    private string? Exact(string remotePath)
    {
        var current = Root;
        foreach (var part in RemotePath.Normalize(remotePath).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current)) return null;
            var hit = Directory.EnumerateFileSystemEntries(current).FirstOrDefault(e => Path.GetFileName(e) == part);
            if (hit is null) return null;
            current = hit;
        }
        return current;
    }

    private string FileOf(string remotePath) =>
        Exact(remotePath) is { } f && System.IO.File.Exists(f) ? f : throw new RemoteFilesException(RemoteFailure.NotFound, $"550 {remotePath}: no such file");

    /// <summary>Путь нового файла: папка должна быть (с точным регистром), имя — как дали.</summary>
    private string NewFile(string remotePath)
    {
        var path = RemotePath.Normalize(remotePath);
        var parent = Exact(RemotePath.Parent(path));
        if (parent is null || !Directory.Exists(parent)) throw new RemoteFilesException(RemoteFailure.NotFound, $"550 {RemotePath.Parent(path)}: no such directory");
        return Path.Combine(parent, RemotePath.Name(path));
    }
}
