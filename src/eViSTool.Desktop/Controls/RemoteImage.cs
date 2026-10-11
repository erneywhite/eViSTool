using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using eViSTool.Core;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Картинка по адресу в сети: <c>&lt;Image c:RemoteImage.Source="{Binding Logo}" c:RemoteImage.DecodeWidth="92" /&gt;</c>.
/// Avalonia сама по http не грузит (WPF умел), поэтому загрузка здесь: в фоне, с кэшем в памяти и на диске. Смена адреса
/// отменяет прежнюю загрузку — в списке с переработкой строк опоздавший ответ не подменит картинку соседа. Пока картинки
/// нет, <c>Image.Source</c> = null: разметка показывает буквы, глядя на него, на <see cref="IsLoadingProperty"/> или
/// <see cref="FailedProperty"/>.
/// </summary>
public static class RemoteImage
{
    /// <summary>Адрес картинки. null или пустая строка — картинки нет, запроса тоже.</summary>
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(RemoteImage));

    /// <summary>
    /// Ширина, до которой декодировать (пиксели, пропорции сохраняются); 0 — как есть. Логотипу в строке списка
    /// не нужна картинка 1920 px: в памяти она заняла бы мегабайты.
    /// </summary>
    public static readonly AttachedProperty<int> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("DecodeWidth", typeof(RemoteImage));

    /// <summary>Идёт загрузка (только для чтения).</summary>
    public static readonly AttachedProperty<bool> IsLoadingProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("IsLoading", typeof(RemoteImage));

    /// <summary>Загрузить не вышло: нет сети, ответ не 200, битая картинка (только для чтения).</summary>
    public static readonly AttachedProperty<bool> FailedProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("Failed", typeof(RemoteImage));

    // память — по объёму пикселей, а не по числу: логотип 92 px и скриншот во весь экран различаются в сотни раз
    private const long MemoryBudget = 96L << 20;
    private const long DiskBudget = 200L << 20;
    private static readonly TimeSpan DiskMaxAge = TimeSpan.FromDays(30);

    // загрузки и декодирование — не больше шести разом: строки, пролистанные до своей очереди, отменятся, не дойдя до сети
    private static readonly SemaphoreSlim Gate = new(6);

    private static readonly ConditionalWeakTable<Image, CancellationTokenSource> Current = new();
    private static readonly Dictionary<Key, Load> Loads = new();
    private static readonly LinkedList<(Key Key, Bitmap Bitmap, long Bytes)> Lru = new();
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, Bitmap Bitmap, long Bytes)>> LruIndex = new();
    private static long _lruBytes;
    private static int _diskTrimmed;
    private static HttpClient? _http;
    private static string? _cacheDir;

    static RemoteImage()
    {
        SourceProperty.Changed.AddClassHandler<Image>((image, _) => Start(image));
        DecodeWidthProperty.Changed.AddClassHandler<Image>((image, _) => Start(image));
    }

    public static string? GetSource(Image image) => image.GetValue(SourceProperty);
    public static void SetSource(Image image, string? value) => image.SetValue(SourceProperty, value);
    public static int GetDecodeWidth(Image image) => image.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image image, int value) => image.SetValue(DecodeWidthProperty, value);
    public static bool GetIsLoading(Image image) => image.GetValue(IsLoadingProperty);
    public static bool GetFailed(Image image) => image.GetValue(FailedProperty);

    /// <summary>Папка дискового кэша: cache/images в папке данных программы.</summary>
    internal static string CacheDir
    {
        get => _cacheDir ??= Path.Combine(AppPaths.Cache, "images");
        set => _cacheDir = value;
    }

    /// <summary>Для тестов: подменить сеть (null — снова настоящая).</summary>
    internal static HttpMessageHandler? HandlerForTests
    {
        set => _http = value is null ? null : NewClient(value);
    }

    /// <summary>Для тестов: забыть картинки в памяти (дисковый кэш остаётся).</summary>
    internal static void ClearMemory()
    {
        lock (Lru)
        {
            Lru.Clear();
            LruIndex.Clear();
            _lruBytes = 0;
        }
    }

    private static HttpClient Http => LazyInitializer.EnsureInitialized(ref _http,
        () => NewClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        }));

    private static HttpClient NewClient(HttpMessageHandler handler)
    {
        // 32 МБ хватит любому скриншоту; больше — не картинка, а ошибка или чужой файл
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 32 << 20 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"eViSTool/{typeof(RemoteImage).Assembly.GetName().Version?.ToString(3)}");
        return http;
    }

    // ---- одна картинка

    private static void Start(Image image)
    {
        var url = GetSource(image);
        var previous = Current.TryGetValue(image, out var p) ? p : null;
        Current.Remove(image);
        image.SetValue(FailedProperty, false);

        if (string.IsNullOrWhiteSpace(url))
        {
            previous?.Cancel();
            image.Source = null;
            image.SetValue(IsLoadingProperty, false);
            return;
        }

        var key = new Key(url.Trim(), Math.Max(0, GetDecodeWidth(image)));
        if (FromMemory(key) is { } cached)
        {
            previous?.Cancel();
            image.Source = cached;
            image.SetValue(IsLoadingProperty, false);
            return;
        }

        // прежняя картинка — чужая: в переработанной строке списка она от другого мода
        image.Source = null;
        image.SetValue(IsLoadingProperty, true);
        var cts = new CancellationTokenSource();
        Current.Add(image, cts);
        // сначала встать в новую загрузку, потом уйти из старой: тот же адрес (сменилась только DecodeWidth или
        // привязка выставила его дважды) продолжит качаться, а не начнётся заново
        var task = Request(key, cts.Token);
        previous?.Cancel();
        _ = FinishAsync(image, cts, task);
    }

    private static async Task FinishAsync(Image image, CancellationTokenSource cts, Task<Bitmap?> task)
    {
        Bitmap? bitmap;
        try
        {
            bitmap = await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return; // адрес сменился — эта картинка уже не нужна
        }
        catch (OperationCanceledException)
        {
            bitmap = null;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // проверка на потоке окна, где адрес и меняется: опоздавший ответ сюда не пройдёт
            if (!Current.TryGetValue(image, out var current) || current != cts) return;
            Current.Remove(image);
            image.Source = bitmap;
            image.SetValue(IsLoadingProperty, false);
            image.SetValue(FailedProperty, bitmap is null);
        });
    }

    // ---- общая загрузка: одна на адрес и ширину, сколько бы картинок её ни ждало (логотип и его размытая копия)

    private readonly record struct Key(string Url, int Width);

    private sealed class Load(Key key)
    {
        public readonly Key Key = key;
        public readonly CancellationTokenSource Cts = new();
        public Task<Bitmap?> Task = null!;
        public int Users;
    }

    private static Task<Bitmap?> Request(Key key, CancellationToken ct)
    {
        Load load;
        lock (Loads)
        {
            if (!Loads.TryGetValue(key, out load!))
            {
                load = new Load(key);
                Loads[key] = load;
                var started = load;
                load.Task = Task.Run(() => LoadAsync(started));
            }
            load.Users++;
        }
        return WaitAsync(load, ct);
    }

    private static async Task<Bitmap?> WaitAsync(Load load, CancellationToken ct)
    {
        using var leave = ct.Register(() => Leave(load));
        return await load.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Картинка ушла с загрузки. Ушли все — загрузка останавливается, а новый запрос начнёт свою.</summary>
    private static void Leave(Load load)
    {
        lock (Loads)
        {
            if (--load.Users > 0 || load.Task.IsCompleted) return;
            if (Loads.TryGetValue(load.Key, out var current) && current == load) Loads.Remove(load.Key);
        }
        load.Cts.Cancel();
    }

    private static async Task<Bitmap?> LoadAsync(Load load)
    {
        var ct = load.Cts.Token;
        var file = Path.Combine(CacheDir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(load.Key.Url))));
        try
        {
            TrimDiskOnce();
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var bytes = await ReadCachedAsync(file, ct).ConfigureAwait(false)
                            ?? await DownloadAsync(load.Key.Url, file, ct).ConfigureAwait(false);
                if (bytes is null) return null;
                ct.ThrowIfCancellationRequested();

                var bitmap = Decode(bytes, load.Key.Width);
                if (bitmap is null)
                {
                    TryDelete(file); // битый ответ в кэше не держим: в следующий раз спросим заново
                    return null;
                }
                ToMemory(load.Key, bitmap);
                return bitmap;
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
                                       or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
                                       or UriFormatException)
        {
            // нет сети, таймаут, адрес не http, ответ больше предела — картинки просто нет
            return null;
        }
        finally
        {
            lock (Loads)
            {
                if (Loads.TryGetValue(load.Key, out var current) && current == load) Loads.Remove(load.Key);
            }
        }
    }

    private static async Task<byte[]?> DownloadAsync(string url, string file, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0) return null;
        Save(file, bytes);
        return bytes;
    }

    private static Bitmap? Decode(byte[] bytes, int width)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return width > 0 ? Bitmap.DecodeToWidth(stream, width) : new Bitmap(stream);
        }
        catch (Exception)
        {
            // байты из сети — что угодно; Skia на мусоре падает чем придётся, вплоть до NullReferenceException
            // (DecodeToWidth не проверяет, распознался ли формат). Для окна это одно: картинки нет.
            return null;
        }
    }

    // ---- память

    private static Bitmap? FromMemory(Key key)
    {
        lock (Lru)
        {
            if (!LruIndex.TryGetValue(key, out var node)) return null;
            Lru.Remove(node);
            Lru.AddFirst(node);
            return node.Value.Bitmap;
        }
    }

    private static void ToMemory(Key key, Bitmap bitmap)
    {
        var bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        lock (Lru)
        {
            if (LruIndex.Remove(key, out var old))
            {
                Lru.Remove(old);
                _lruBytes -= old.Value.Bytes;
            }
            LruIndex[key] = Lru.AddFirst((key, bitmap, bytes));
            _lruBytes += bytes;
            // вытесненные не освобождаем (Dispose): картинка может ещё стоять в окне, её подберёт сборщик мусора
            while (_lruBytes > MemoryBudget && Lru.Count > 1 && Lru.Last is { } last)
            {
                Lru.RemoveLast();
                LruIndex.Remove(last.Value.Key);
                _lruBytes -= last.Value.Bytes;
            }
        }
    }

    // ---- диск: файл на адрес (имя — SHA-256 адреса), содержимое — ответ сервера как есть

    private static async Task<byte[]?> ReadCachedAsync(string file, CancellationToken ct)
    {
        if (!File.Exists(file)) return null;
        try
        {
            return await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // файл занят или удалён уборкой — скачаем заново
        }
    }

    private static void Save(string file, byte[] bytes)
    {
        // через временный файл: оборванная запись не оставит в кэше половину картинки
        var temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp); // без кэша на диске картинка всё равно покажется
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Раз за запуск: убрать файлы старше месяца и самые старые сверх предела. Логотипы и скриншоты всех просмотренных
    /// модов каталога иначе копились бы без конца.
    /// </summary>
    private static void TrimDiskOnce()
    {
        if (Interlocked.Exchange(ref _diskTrimmed, 1) == 1) return;
        var dir = new DirectoryInfo(CacheDir);
        _ = Task.Run(() =>
        {
            try
            {
                if (!dir.Exists) return;
                long total = 0;
                foreach (var file in dir.EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    total += file.Length;
                    if (total > DiskBudget || DateTime.UtcNow - file.LastWriteTimeUtc > DiskMaxAge) TryDelete(file.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // уборка не к спеху: не вышло сейчас — попробуем при следующем запуске
            }
        });
    }
}
