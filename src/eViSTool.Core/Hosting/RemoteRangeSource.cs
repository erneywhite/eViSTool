namespace eViSTool.Core.Hosting;

/// <summary>
/// Архив мода на сервере у хостера как источник кусков для <see cref="ZipProbe"/>. Чтение с позиции — REST + RETR;
/// сервер этого не умеет — <see cref="RemoteFailure.RangeUnsupported"/>, и ZipProbe берёт архив целиком
/// (<see cref="OpenAllAsync"/>). Небольшой архив скачивается в память, большой — во временный файл в
/// <paramref name="tempDir"/>, который удаляется при закрытии потока.
/// </summary>
public sealed class RemoteRangeSource(IRemoteFiles files, RemoteEntry file, string tempDir) : IRangeSource
{
    public long Length => file.Size;

    public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) =>
        files.Features.RangeReads
            ? files.ReadRangeAsync(file.Path, offset, count, ct)
            : Task.FromException<byte[]>(new RemoteFilesException(RemoteFailure.RangeUnsupported, "the server cannot read from an offset"));

    public async Task<Stream> OpenAllAsync(CancellationToken ct)
    {
        if (file.Size <= ZipProbe.WholeFileLimit)
        {
            var memory = new MemoryStream();
            await files.DownloadAsync(file.Path, memory, null, ct).ConfigureAwait(false);
            memory.Position = 0;
            return memory;
        }

        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".zip");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await files.DownloadAsync(file.Path, stream, null, ct).ConfigureAwait(false);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
