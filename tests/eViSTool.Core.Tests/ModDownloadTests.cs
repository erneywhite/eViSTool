using System.Net;
using eViSTool.Core.ModDb;

namespace eViSTool.Core.Tests;

/// <summary>Скачивание файла мода: откуда можно качать, зависший сервер, недокачанный файл (ревью 2026-10-10).</summary>
public sealed class ModDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-dl-" + Guid.NewGuid().ToString("N"));

    public ModDownloadTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("https://moddbcdn.vintagestory.at/Footprints-v1.2.13_abc.zip?dl=x", true)]
    [InlineData("https://mods.vintagestory.at/download?fileid=1", true)]
    [InlineData("http://moddbcdn.vintagestory.at/x.zip", false)]
    [InlineData("https://evil.example/x.zip", false)]
    [InlineData("https://vintagestory.at.evil.example/x.zip", false)]
    [InlineData("https://evilvintagestory.at/x.zip", false)]
    [InlineData("file:///C:/Windows/x.zip", false)]
    [InlineData("not a url", false)]
    public void OnlyModDbFileHosts_AreAllowed(string url, bool allowed) => Assert.Equal(allowed, ModDbClient.IsModDbFileUrl(url));

    [Fact]
    public async Task ForeignUrl_IsRefused_WithoutARequest()
    {
        var handler = new Handler(() => new MemoryStream([1, 2, 3]));
        using var db = new ModDbClient(new HttpClient(handler));
        var dest = Path.Combine(_root, "a", "mod.zip");

        await Assert.ThrowsAsync<InvalidDataException>(() => db.DownloadAsync("https://evil.example/mod.zip", dest));
        Assert.Equal(0, handler.Requests);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task StalledServer_TimesOut_AndLeavesNoPartFile()
    {
        using var db = new ModDbClient(new HttpClient(new Handler(() => new SilentStream())))
        {
            StallTimeout = TimeSpan.FromMilliseconds(300),
        };
        var dest = Path.Combine(_root, "b", "mod.zip");

        await Assert.ThrowsAsync<TimeoutException>(() => db.DownloadAsync("https://moddbcdn.vintagestory.at/mod.zip", dest));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "b")));
    }

    [Fact]
    public async Task Cancelled_ByUser_IsNotATimeout_AndLeavesNoPartFile()
    {
        using var db = new ModDbClient(new HttpClient(new Handler(() => new SilentStream())));
        var dest = Path.Combine(_root, "c", "mod.zip");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.DownloadAsync("https://moddbcdn.vintagestory.at/mod.zip", dest, ct: cts.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "c")));
    }

    [Fact]
    public async Task NormalDownload_ArrivesWhole()
    {
        using var db = new ModDbClient(new HttpClient(new Handler(() => new MemoryStream([1, 2, 3]))));
        var dest = Path.Combine(_root, "d", "mod.zip");

        await db.DownloadAsync("https://moddbcdn.vintagestory.at/mod.zip", dest);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(dest));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "d")));
    }

    [Fact]
    public void IsUnder_NeedsASeparator()
    {
        var game = Path.Combine(_root, "Vintagestory");
        Assert.True(PathRules.IsUnder(Path.Combine(game, "VintagestoryServer.dll"), game));
        Assert.False(PathRules.IsUnder(Path.Combine(_root, "Vintagestory2", "VintagestoryServer.dll"), game));
        Assert.False(PathRules.IsUnder(game, game));
        Assert.False(PathRules.IsUnder(Path.Combine(game, "..", "x.dll"), game));
    }

    private sealed class Handler(Func<Stream> body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body()) });
        }
    }

    /// <summary>Сервер отдал заголовки и замолчал: чтение ждёт, пока не отменят.</summary>
    private sealed class SilentStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
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
