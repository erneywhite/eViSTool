using System.Collections.Concurrent;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Tests;

/// <summary>RemoteImage с подменённой сетью и своей папкой кэша на каждый тест.</summary>
public sealed class RemoteImageTests : IDisposable
{
    private readonly FakeHttp _http = new();
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "evistool-tests", "images-" + Guid.NewGuid().ToString("N"));

    public RemoteImageTests()
    {
        RemoteImage.HandlerForTests = _http;
        RemoteImage.CacheDir = _cacheDir;
        RemoteImage.ClearMemory();
    }

    public void Dispose()
    {
        RemoteImage.HandlerForTests = null;
        RemoteImage.ClearMemory();
        try { Directory.Delete(_cacheDir, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private static byte[] Png(int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static int WidthOf(Image image) => ((Bitmap)image.Source!).PixelSize.Width;

    [AvaloniaFact]
    public async Task Loads_then_serves_from_memory_and_disk()
    {
        _http.Reply("https://img.test/logo.png", Png(10, 10));

        var first = new Image();
        RemoteImage.SetSource(first, "https://img.test/logo.png");
        Assert.True(RemoteImage.GetIsLoading(first));
        Assert.Null(first.Source);

        await Wait.Until(() => first.Source is not null);
        Assert.False(RemoteImage.GetIsLoading(first));
        Assert.False(RemoteImage.GetFailed(first));
        Assert.Equal(10, WidthOf(first));

        // из памяти — сразу, без ожидания и без запроса
        var second = new Image();
        RemoteImage.SetSource(second, "https://img.test/logo.png");
        Assert.Same(first.Source, second.Source);
        Assert.False(RemoteImage.GetIsLoading(second));

        // память забыта — с диска, сеть не трогаем
        RemoteImage.ClearMemory();
        var third = new Image();
        RemoteImage.SetSource(third, "https://img.test/logo.png");
        await Wait.Until(() => third.Source is not null);
        Assert.Equal(10, WidthOf(third));
        Assert.Equal(1, _http.Count("https://img.test/logo.png"));
        Assert.Single(Directory.GetFiles(_cacheDir));
    }

    [AvaloniaFact]
    public async Task DecodeWidth_scales_picture()
    {
        _http.Reply("https://img.test/big.png", Png(40, 20));
        var image = new Image();
        RemoteImage.SetDecodeWidth(image, 10);
        RemoteImage.SetSource(image, "https://img.test/big.png");

        await Wait.Until(() => image.Source is not null);
        Assert.Equal(new PixelSize(10, 5), ((Bitmap)image.Source!).PixelSize);
    }

    [AvaloniaFact]
    public async Task Same_url_twice_is_one_request()
    {
        _http.Reply("https://img.test/logo.png", Png(10, 10), delayMs: 100);
        var logo = new Image();
        var blurred = new Image(); // в шапке карточки логотип и его размытая копия — один адрес
        RemoteImage.SetSource(logo, "https://img.test/logo.png");
        RemoteImage.SetSource(blurred, "https://img.test/logo.png");

        await Wait.Until(() => logo.Source is not null && blurred.Source is not null);
        Assert.Equal(1, _http.Count("https://img.test/logo.png"));
    }

    /// <summary>Строку списка переработали под другой мод: старый ответ пришёл позже нового и картинку не подменил.</summary>
    [AvaloniaFact]
    public async Task Late_response_of_old_url_does_not_win()
    {
        var release = new TaskCompletionSource();
        CancellationToken slowToken = default;
        // медленный сервер не слушает отмену — ответ всё равно придёт
        _http.Handle("https://img.test/old.png", async ct =>
        {
            slowToken = ct;
            await release.Task;
            return Ok(Png(10, 10));
        });
        _http.Reply("https://img.test/new.png", Png(20, 20));

        var image = new Image();
        RemoteImage.SetSource(image, "https://img.test/old.png");
        await Wait.Until(() => _http.Count("https://img.test/old.png") == 1);
        RemoteImage.SetSource(image, "https://img.test/new.png");
        await Wait.Until(() => image.Source is not null);
        Assert.Equal(20, WidthOf(image));
        Assert.True(slowToken.IsCancellationRequested); // старую загрузку отменили, а не бросили

        release.SetResult();
        await Wait.Idle();
        Assert.Equal(20, WidthOf(image));
        Assert.False(RemoteImage.GetIsLoading(image));
        Assert.False(RemoteImage.GetFailed(image));
    }

    [AvaloniaFact]
    public async Task Not_found_is_failed()
    {
        _http.Handle("https://img.test/missing.png", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var image = new Image();
        RemoteImage.SetSource(image, "https://img.test/missing.png");

        await Wait.Until(() => !RemoteImage.GetIsLoading(image));
        Assert.True(RemoteImage.GetFailed(image));
        Assert.Null(image.Source);
        Assert.False(Directory.Exists(_cacheDir) && Directory.GetFiles(_cacheDir).Length > 0);
    }

    [AvaloniaFact]
    public async Task Broken_bytes_are_failed_and_not_cached()
    {
        _http.Reply("https://img.test/broken.png", "<html>not an image</html>"u8.ToArray());
        foreach (var width in new[] { 0, 32 }) // разные пути декодирования: целиком и DecodeToWidth
        {
            var image = new Image();
            RemoteImage.SetDecodeWidth(image, width);
            RemoteImage.SetSource(image, "https://img.test/broken.png");

            await Wait.Until(() => !RemoteImage.GetIsLoading(image));
            Assert.True(RemoteImage.GetFailed(image));
            Assert.Null(image.Source);
        }
        Assert.Equal(2, _http.Count("https://img.test/broken.png")); // битое на диске не осталось — спросили заново
        Assert.Empty(Directory.GetFiles(_cacheDir));
    }

    [AvaloniaFact]
    public async Task Network_error_is_failed()
    {
        _http.Handle("https://img.test/down.png", _ => throw new HttpRequestException("no route"));
        var image = new Image();
        RemoteImage.SetSource(image, "https://img.test/down.png");

        await Wait.Until(() => !RemoteImage.GetIsLoading(image));
        Assert.True(RemoteImage.GetFailed(image));
    }

    [AvaloniaFact]
    public async Task Empty_url_clears_without_request()
    {
        _http.Reply("https://img.test/logo.png", Png(10, 10));
        var image = new Image();
        RemoteImage.SetSource(image, "https://img.test/logo.png");
        await Wait.Until(() => image.Source is not null);

        RemoteImage.SetSource(image, "");
        Assert.Null(image.Source);
        Assert.False(RemoteImage.GetIsLoading(image));
        Assert.False(RemoteImage.GetFailed(image));

        RemoteImage.SetSource(image, null);
        Assert.Null(image.Source);
        Assert.Equal(1, _http.Total);
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    /// <summary>Сеть для тестов: ответы по адресу и счётчик запросов.</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _routes = new();
        private readonly ConcurrentDictionary<string, int> _counts = new();

        public int Total => _counts.Values.Sum();
        public int Count(string url) => _counts.GetValueOrDefault(url);

        public void Handle(string url, Func<CancellationToken, Task<HttpResponseMessage>> handler) => _routes[url] = handler;

        public void Reply(string url, byte[] body, int delayMs = 0) => Handle(url, async ct =>
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            return Ok(body);
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            _counts.AddOrUpdate(url, 1, (_, n) => n + 1);
            return _routes.TryGetValue(url, out var handler)
                ? handler(cancellationToken)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
