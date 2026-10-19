using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using eViSTool.Core.Mods;
using eViSTool.Desktop.Controls;
using eViSTool.Presentation;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Значок мода (docs/design.md, раздел 7): иконка из файла мода — маленькая, из памяти сразу, файл читается один раз;
/// нет её — логотип с модбазы; нет и его — пусто (разметка рисует буквы). Сеть подменена, кэш картинок — свой на тест.
/// </summary>
public sealed class ModIconImageTests : IDisposable
{
    private const string LogoUrl = "https://img.test/logo.png";
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-tests", "icons-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeHttp _http = new();
    private readonly ConcurrentDictionary<string, int> _reads = new();

    public ModIconImageTests()
    {
        RemoteImage.HandlerForTests = _http;
        RemoteImage.CacheDir = Path.Combine(_dir, "cache");
        RemoteImage.ClearMemory();
        ModIconImage.ClearMemory();
        // настоящее чтение, только со счётчиком: сколько раз открывали каждый файл
        ModIconImage.ReaderForTests = path =>
        {
            _reads.AddOrUpdate(path, 1, (_, n) => n + 1);
            return ModIcon.Read(path);
        };
        _http.Reply(LogoUrl, Png(200, 100));
    }

    public void Dispose()
    {
        ModIconImage.ReaderForTests = null;
        ModIconImage.ClearMemory();
        RemoteImage.HandlerForTests = null;
        RemoteImage.ClearMemory();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static byte[] Png(int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>Мод-архив в папке теста; <paramref name="icon"/> — байты modicon.png или null (иконки нет).</summary>
    private ModFileStamp Mod(string name, byte[]? icon)
    {
        var path = Path.Combine(_dir, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
                w.Write("""{ "modid": "alpha", "version": "1.0.0" }""");
            if (icon is not null)
                using (var s = zip.CreateEntry("modicon.png").Open())
                    s.Write(icon);
        }
        var file = new FileInfo(path);
        return new ModFileStamp(path, file.LastWriteTimeUtc, file.Length);
    }

    private static Image Show(ModFileStamp? file, string? logo, int width = ModIconImage.DefaultSize)
    {
        var image = new Image();
        ModIconImage.SetDecodeWidth(image, width);
        ModIconImage.SetSource(image, new ModIconSource(file, logo));
        return image;
    }

    private static PixelSize SizeOf(Image image) => ((Bitmap)image.Source!).PixelSize;

    [AvaloniaFact]
    public async Task Icon_from_the_file_is_small_and_the_logo_is_not_asked()
    {
        var image = Show(Mod("alpha.zip", Png(480, 480)), LogoUrl);
        Assert.Null(image.Source); // читается в фоне — пока буквы

        await Wait.Until(() => image.Source is not null);
        Assert.Equal(new PixelSize(64, 64), SizeOf(image));
        Assert.True(ModIconImage.GetFromFile(image));
        Assert.Null(RemoteImage.GetSource(image));
        await Wait.Idle(100);
        Assert.Equal(0, _http.Total);
    }

    [AvaloniaFact]
    public async Task File_is_read_once_and_then_served_from_memory_at_once()
    {
        var file = Mod("alpha.zip", Png(128, 128));
        var row = Show(file, null);
        var blurred = Show(file, null); // в шапке карточки иконка и её размытая копия — один файл
        await Wait.Until(() => row.Source is not null && blurred.Source is not null);

        var again = Show(file, null);
        Assert.Same(row.Source, again.Source); // строку переработали под этот мод снова — иконка сразу, без чтения
        Assert.Equal(1, _reads[file.Path]);
    }

    [AvaloniaFact]
    public async Task Without_an_icon_in_the_file_the_logo_is_shown()
    {
        var file = Mod("plain.zip", icon: null);
        var image = Show(file, LogoUrl);

        await Wait.Until(() => image.Source is not null);
        Assert.Equal(LogoUrl, RemoteImage.GetSource(image));
        Assert.False(ModIconImage.GetFromFile(image));
        Assert.Equal(1, _http.Count(LogoUrl));

        // «иконки нет» запомнено: другая строка того же мода сразу просит логотип, архив больше не открывается
        var other = Show(file, LogoUrl);
        Assert.Equal(LogoUrl, RemoteImage.GetSource(other));
        Assert.Equal(1, _reads[file.Path]);
    }

    [AvaloniaFact]
    public async Task Nothing_at_all_leaves_the_letters()
    {
        var file = Mod("plain.zip", icon: null);
        var image = Show(file, null);
        await Wait.Until(() => _reads.ContainsKey(file.Path));
        await Wait.Idle(100);
        Assert.Null(image.Source);
        Assert.Null(RemoteImage.GetSource(image));

        // мода нет на этом диске (сервер у хостера) и логотипа нет — тоже пусто, и ничего не читается
        var hosted = Show(null, null);
        Assert.Null(hosted.Source);
        Assert.Single(_reads);
    }

    [AvaloniaFact]
    public async Task Broken_icon_falls_back_to_the_logo()
    {
        var image = Show(Mod("broken.zip", "<html>не картинка</html>"u8.ToArray()), LogoUrl);
        await Wait.Until(() => image.Source is not null);
        Assert.Equal(LogoUrl, RemoteImage.GetSource(image));
        Assert.Equal(new PixelSize(128, 64), SizeOf(image)); // логотип — вдвое шире плитки: широкий, а режется по высоте
    }

    [AvaloniaFact]
    public async Task Mod_updated_in_place_gets_its_new_icon()
    {
        var old = Mod("alpha.zip", Png(128, 128));
        var first = Show(old, null);
        await Wait.Until(() => first.Source is not null);

        File.Delete(old.Path);
        var fresh = Mod("alpha.zip", Png(32, 32)) with { WriteTimeUtc = old.WriteTimeUtc.AddMinutes(1) };
        ModIconImage.SetSource(first, new ModIconSource(fresh, null));
        await Wait.Until(() => first.Source is not null && _reads[old.Path] == 2);
        Assert.Equal(new PixelSize(32, 32), SizeOf(first)); // новая иконка; мелкая — как есть, не растянутая до 64
        Assert.NotSame(Show(old, null).Source, first.Source);
    }

    [AvaloniaFact]
    public async Task Wide_icon_is_scaled_by_its_short_side()
    {
        var image = Show(Mod("wide.zip", Png(480, 320)), null);
        await Wait.Until(() => image.Source is not null);
        Assert.Equal(new PixelSize(96, 64), SizeOf(image)); // плитка обрежет до квадрата 64 × 64 — без мыла
    }

    /// <summary>Таблица переработала строку под другой мод: иконка прежнего, прочитанная позже, картинку не подменила.</summary>
    [AvaloniaFact]
    public async Task Late_icon_of_the_previous_mod_does_not_win()
    {
        var slow = Mod("slow.zip", Png(128, 128));
        var quick = Mod("quick.zip", Png(32, 32));
        var release = new ManualResetEventSlim();
        ModIconImage.ReaderForTests = path =>
        {
            if (path == slow.Path) release.Wait(TimeSpan.FromSeconds(20));
            return ModIcon.Read(path);
        };

        var image = Show(slow, null);
        ModIconImage.SetSource(image, new ModIconSource(quick, null));
        await Wait.Until(() => image.Source is not null);
        var mine = image.Source;

        release.Set();
        await Wait.Until(() => Show(slow, null).Source is not null); // медленная дочиталась и легла в память
        await Wait.Idle(100);
        Assert.Same(mine, image.Source);
    }

    /// <summary>Сеть для тестов: ответы по адресу и счётчик запросов.</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, byte[]> _routes = new();
        private readonly ConcurrentDictionary<string, int> _counts = new();

        public int Total => _counts.Values.Sum();
        public int Count(string url) => _counts.GetValueOrDefault(url);
        public void Reply(string url, byte[] body) => _routes[url] = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            _counts.AddOrUpdate(url, 1, (_, n) => n + 1);
            return Task.FromResult(_routes.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
