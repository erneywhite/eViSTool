using System.Text;

namespace eViSTool.Core.Hosting;

/// <summary>Файл или папка на сервере. <paramref name="Path"/> — абсолютный через «/»; <paramref name="Modified"/> —
/// как отдал листинг (MLSD — UTC, LIST — местное время сервера с точностью до минуты), null — неизвестно.</summary>
public sealed record RemoteEntry(string Path, string Name, bool IsDirectory, long Size, DateTime? Modified);

/// <summary>Что умеет сервер — выясняется при подключении и пробами (FEAT у хостеров бывает не реализован).</summary>
/// <param name="RangeReads">Чтение с позиции (REST) работает.</param>
/// <param name="Utf8">Имена в UTF-8.</param>
/// <param name="Encrypted">Соединение зашифровано (TLS).</param>
/// <param name="Banner">Приветствие сервера (строка 220), если было.</param>
public sealed record RemoteFeatures(bool RangeReads, bool Utf8, bool Encrypted, string? Banner);

/// <summary>Что именно не вышло — по нему окно подбирает текст (<see cref="HostedSecret.Describe"/>).</summary>
public enum RemoteFailure
{
    Connect, Auth, Timeout, Tls, CertificateUntrusted, NotFound, Denied, Quota, RangeUnsupported, Exists, Protocol, Unsupported,
}

/// <summary>
/// Единственное исключение, которое выпускает наружу <see cref="IRemoteFiles"/>. Наследник <see cref="IOException"/>:
/// фильтры по всему окну ловят IOException, и обрыв сети не должен попасть в журнал вылетов.
/// В тексте сообщения не бывает пароля — его туда не кладёт ни адаптер, ни Core.
/// </summary>
public sealed class RemoteFilesException(RemoteFailure kind, string message, Exception? inner = null) : IOException(message, inner)
{
    public RemoteFailure Kind { get; } = kind;

    /// <summary>Для <see cref="RemoteFailure.CertificateUntrusted"/>: отпечаток SHA-256 сертификата сервера.</summary>
    public string? CertificateSha256 { get; init; }

    /// <summary>Для <see cref="RemoteFailure.CertificateUntrusted"/>: на кого выписан сертификат.</summary>
    public string? CertificateSubject { get; init; }
}

/// <summary>
/// Файлы сервера у хостера: FTP сейчас, SFTP — потом. Пути абсолютные через «/». Один экземпляр — одно соединение,
/// не потокобезопасен (очередь операций — у <see cref="HostedSessions"/>). Любой сбой — <see cref="RemoteFilesException"/>,
/// отмена — <see cref="OperationCanceledException"/> как есть.
/// </summary>
public interface IRemoteFiles : IAsyncDisposable
{
    RemoteFeatures Features { get; }

    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct);

    /// <summary>Содержимое папки; папки нет — <see cref="RemoteFailure.NotFound"/>.</summary>
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string dir, CancellationToken ct);

    /// <summary>Сведения о файле или папке; нет — null.</summary>
    Task<RemoteEntry?> StatAsync(string path, CancellationToken ct);

    /// <summary>Кусок файла с позиции; вернулось меньше <paramref name="count"/> — дошли до конца файла.
    /// Сервер не умеет читать с позиции — <see cref="RemoteFailure.RangeUnsupported"/>.</summary>
    Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct);

    Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Залить файл; существующий перезаписывается.</summary>
    Task UploadAsync(Stream source, string path, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Переименовать; назначение занято — <see cref="RemoteFailure.Exists"/> (многие серверы поверх не переименовывают).</summary>
    Task RenameAsync(string from, string to, CancellationToken ct);

    Task DeleteFileAsync(string path, CancellationToken ct);

    Task CreateDirectoryAsync(string path, CancellationToken ct);
}

public static class RemoteFilesExtensions
{
    /// <summary>Текстовый файл целиком; кодировка — по BOM, без него UTF-8.</summary>
    public static async Task<string> ReadTextAsync(this IRemoteFiles files, string path, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await files.DownloadAsync(path, buffer, null, ct).ConfigureAwait(false);
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Записать текст в UTF-8 без BOM (так пишет свои файлы игра).</summary>
    public static async Task WriteTextAsync(this IRemoteFiles files, string path, string text, CancellationToken ct)
    {
        using var buffer = new MemoryStream(new UTF8Encoding(false).GetBytes(text), writable: false);
        await files.UploadAsync(buffer, path, null, ct).ConfigureAwait(false);
    }
}
