using System.Net;
using System.Security.Cryptography;
using eViSTool.Core.AppUpdate;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Tests;

/// <summary>
/// Скачивание обновления программы, когда сеть подводит: архив обрезан, связь оборвалась посреди файла, сервер
/// замолчал. Ставиться ничего не должно, и недокачанный архив не должен оставаться в папке загрузок.
/// </summary>
public sealed class AppUpdaterDownloadTests : IDisposable
{
    private static readonly byte[] Archive = [.. Enumerable.Range(0, 200_000).Select(i => (byte)i)];
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData(Archive));

    private readonly string _downloads = Path.Combine(Path.GetTempPath(), "evistool-appdl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_downloads)) Directory.Delete(_downloads, recursive: true);
    }

    private static AppRelease Release => new()
    {
        Version = ModVersion.ParseOrNull("0.2.0")!, Tag = "v0.2.0", AssetName = "eViSTool-0.2.0-win-x64.zip",
        AssetUrl = "https://dl/e.zip", Sha256 = Hash, AssetSize = Archive.Length,
    };

    /// <summary>Архив — из body; подпись («.sig») — настоящая, тестовым ключом.</summary>
    private sealed class Handler(Func<Stream> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith(".sig") ? new StringContent(TestSigning.Sign(Archive)) : new StreamContent(body()),
            });
    }

    private static AppUpdater Updater(Func<Stream> body) => new(new HttpClient(new Handler(body)), TestSigning.PublicKey);

    private bool LeftoverFiles() => Directory.Exists(_downloads) && Directory.EnumerateFiles(_downloads).Any();

    [Fact]
    public async Task TruncatedArchive_IsRefusedByTheHash_AndRemoved()
    {
        var updater = Updater(() => new MemoryStream(Archive[..150_000]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAsync(Release, _downloads));
        Assert.False(LeftoverFiles());
    }

    [Fact]
    public async Task ConnectionDroppedMidway_LeavesNoPartialArchive()
    {
        var updater = Updater(() => new BreakingStream(Archive, breakAfter: 100_000));
        await Assert.ThrowsAsync<IOException>(() => updater.DownloadAsync(Release, _downloads));
        Assert.False(LeftoverFiles());
    }

    [Fact]
    public async Task StalledServer_TimesOut_InsteadOfWaitingForever()
    {
        var updater = new AppUpdater(new HttpClient(new Handler(() => new BreakingStream(Archive, breakAfter: 100_000, stall: true))), TestSigning.PublicKey)
        {
            StallTimeout = TimeSpan.FromMilliseconds(300),
        };
        await Assert.ThrowsAsync<TimeoutException>(() => updater.DownloadAsync(Release, _downloads));
        Assert.False(LeftoverFiles());
    }

    [Fact]
    public async Task Cancelled_LeavesNoPartialArchive()
    {
        var updater = Updater(() => new BreakingStream(Archive, breakAfter: 100_000, stall: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.DownloadAsync(Release, _downloads, ct: cts.Token));
        Assert.False(LeftoverFiles());
    }

    [Fact]
    public async Task WholeArchive_Arrives()
    {
        var updater = Updater(() => new MemoryStream(Archive));
        var path = await updater.DownloadAsync(Release, _downloads);
        Assert.Equal(Archive, await File.ReadAllBytesAsync(path));
    }

    /// <summary>Отдаёт первые <c>breakAfter</c> байт, потом рвёт связь (IOException) или молчит до отмены.</summary>
    private sealed class BreakingStream(byte[] data, int breakAfter, bool stall = false) : Stream
    {
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position >= breakAfter)
            {
                if (!stall) throw new IOException("Соединение разорвано");
                await Task.Delay(Timeout.Infinite, ct);
            }
            var n = Math.Min(buffer.Length, Math.Min(breakAfter, data.Length) - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
