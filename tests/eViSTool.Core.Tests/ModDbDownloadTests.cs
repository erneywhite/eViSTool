using System.Net;
using eViSTool.Core.ModDb;

namespace eViSTool.Core.Tests;

/// <summary>Скачивание архива мода: только с модбазы, и после перенаправлений тоже; недокачанного файла не остаётся.</summary>
public sealed class ModDbDownloadTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-dl-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Отвечает файлом; <see cref="RedirectTo"/> — как будто HttpClient пришёл туда по перенаправлению.</summary>
    private sealed class Cdn : HttpMessageHandler
    {
        public string? RedirectTo { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var final = RedirectTo is null ? request : new HttpRequestMessage(HttpMethod.Get, RedirectTo);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]), RequestMessage = final });
        }
    }

    private const string Url = "https://moddbcdn.vintagestory.at/files/carryon.zip";

    [Fact]
    public async Task FromModDb_IsDownloaded()
    {
        using var db = new ModDbClient(new HttpClient(new Cdn()) { BaseAddress = new Uri(ModDbClient.ApiBase) });
        var dest = Path.Combine(_dir, "carryon.zip");
        await db.DownloadAsync(Url, dest);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(dest));
    }

    // адрес модбазы перенаправил на чужой хост — файл не берём
    [Fact]
    public async Task RedirectAwayFromModDb_IsRefused()
    {
        using var db = new ModDbClient(new HttpClient(new Cdn { RedirectTo = "https://files.example.org/carryon.zip" })
            { BaseAddress = new Uri(ModDbClient.ApiBase) });
        var dest = Path.Combine(_dir, "carryon.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => db.DownloadAsync(Url, dest));
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }
}
