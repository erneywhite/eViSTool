using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using eViSTool.Core.Mods;
using eViSTool.Presentation;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Значок мода на Image (docs/design.md, раздел 7): <c>&lt;Image ctl:ModIconImage.Source="{Binding Icon}" /&gt;</c>.
/// Первое, что есть: иконка modicon.png из файла мода (<see cref="ModIcon"/>), логотип с модбазы (его грузит
/// <see cref="RemoteImage"/>), иначе <c>Image.Source</c> = null — разметка показывает буквы.
/// </summary>
/// <remarks>
/// Иконка читается и декодируется в фоне, сразу в маленький размер (<see cref="DecodeWidthProperty"/>), и остаётся в памяти
/// по отметке файла (путь, время, размер): мод обновили — отметка другая, иконка читается заново. Что иконки в файле нет,
/// тоже запоминается: архив без неё не открывается при каждой прокрутке. Таблица переиспользует строки — опоздавшая
/// иконка прежнего мода строки не подменит (проверка на потоке окна, как у <see cref="RemoteImage"/>).
/// </remarks>
public static class ModIconImage
{
    /// <summary>Что показывать: файл мода и запасной логотип (<see cref="ModIconSource"/>); null — ничего.</summary>
    public static readonly AttachedProperty<ModIconSource?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, ModIconSource?>("Source", typeof(ModIconImage));

    /// <summary>
    /// Ширина, до которой уменьшать (пиксели; у неквадратной иконки — короткая сторона). Плитке в строке 30 px хватает
    /// 64 — чётко и на увеличенном масштабе экрана.
    /// </summary>
    public static readonly AttachedProperty<int> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("DecodeWidth", typeof(ModIconImage), DefaultSize);

    /// <summary>Показана иконка из файла мода, а не логотип (только для чтения).</summary>
    public static readonly AttachedProperty<bool> FromFileProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("FromFile", typeof(ModIconImage));

    public const int DefaultSize = 64;

    // память — по объёму пикселей: иконка 64 px — 16 КБ, сотня модов — пара мегабайт
    private const long MemoryBudget = 24L << 20;

    // архивов разом читаем не больше четырёх: быстрая прокрутка сотни модов не забьёт диск
    private static readonly SemaphoreSlim Gate = new(4);

    private static readonly ConditionalWeakTable<Image, object> Current = new();
    private static readonly Dictionary<Key, Task<Bitmap?>> Loads = new();
    private static readonly LinkedList<(Key Key, Bitmap? Bitmap, long Bytes)> Lru = new();
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, Bitmap? Bitmap, long Bytes)>> LruIndex = new();
    private static long _lruBytes;

    static ModIconImage()
    {
        SourceProperty.Changed.AddClassHandler<Image>((image, _) => Start(image));
        DecodeWidthProperty.Changed.AddClassHandler<Image>((image, _) => Start(image));
    }

    public static ModIconSource? GetSource(Image image) => image.GetValue(SourceProperty);
    public static void SetSource(Image image, ModIconSource? value) => image.SetValue(SourceProperty, value);
    public static int GetDecodeWidth(Image image) => image.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image image, int value) => image.SetValue(DecodeWidthProperty, value);
    public static bool GetFromFile(Image image) => image.GetValue(FromFileProperty);

    /// <summary>Для тестов: чем читать иконку из файла мода (null — снова <see cref="ModIcon.Read"/>).</summary>
    internal static Func<string, byte[]?>? ReaderForTests { get; set; }

    /// <summary>Для тестов: забыть иконки в памяти.</summary>
    internal static void ClearMemory()
    {
        lock (Lru)
        {
            Lru.Clear();
            LruIndex.Clear();
            _lruBytes = 0;
        }
    }

    // ---- одна картинка

    private static void Start(Image image)
    {
        Current.Remove(image); // прежняя загрузка этой картинки, если шла, своё уже не покажет
        if (GetSource(image)?.File is not { } file)
        {
            ShowLogo(image);
            return;
        }

        var key = new Key(file, Math.Max(1, GetDecodeWidth(image)));
        if (FromMemory(key, out var cached))
        {
            if (cached is null) ShowLogo(image);
            else ShowIcon(image, cached);
            return;
        }

        // прежняя картинка — чужая: в переработанной строке таблицы она от другого мода. Логотип пока не просим: если
        // иконка в файле есть, сеть не нужна
        ShowIcon(image, null);
        var mine = new object();
        Current.Add(image, mine);
        _ = FinishAsync(image, mine, Request(key));
    }

    private static async Task FinishAsync(Image image, object mine, Task<Bitmap?> load)
    {
        var bitmap = await load.ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            // проверка на потоке окна, где значок и меняется: опоздавшая иконка прежнего мода сюда не пройдёт
            if (!Current.TryGetValue(image, out var current) || current != mine) return;
            Current.Remove(image);
            if (bitmap is null) ShowLogo(image);
            else ShowIcon(image, bitmap);
        });
    }

    private static void ShowIcon(Image image, Bitmap? bitmap)
    {
        // сначала отпустить логотип: RemoteImage, отпуская адрес, сам обнуляет Source
        RemoteImage.SetSource(image, null);
        image.Source = bitmap;
        image.SetValue(FromFileProperty, bitmap is not null);
    }

    private static void ShowLogo(Image image)
    {
        image.SetValue(FromFileProperty, false);
        // логотипы модбазы обычно широкие (около 16 : 9), а квадратная плитка режет их по высоте — декодируем вдвое шире,
        // чтобы высота не вышла меньше плитки
        var width = Math.Max(1, GetDecodeWidth(image)) * 2;
        if (RemoteImage.GetDecodeWidth(image) != width) RemoteImage.SetDecodeWidth(image, width);
        var logo = GetSource(image)?.Logo;
        // тот же адрес RemoteImage не перезапустит: он уже показан или грузится. Пустой — убрать иконку, если была
        if (RemoteImage.GetSource(image) == logo)
        {
            if (string.IsNullOrWhiteSpace(logo)) image.Source = null;
            return;
        }
        RemoteImage.SetSource(image, logo);
    }

    // ---- общая загрузка: одна на файл и размер, сколько бы картинок её ни ждало (иконка и её размытая копия в шапке)

    private readonly record struct Key(ModFileStamp File, int Width);

    // «в файле иконки нет» от размера не зависит — запоминается под шириной 0 и годится для любой
    private static Key NoIcon(ModFileStamp file) => new(file, 0);

    private static Task<Bitmap?> Request(Key key)
    {
        lock (Loads)
        {
            if (Loads.TryGetValue(key, out var running)) return running;
            // запись убирает сама загрузка, когда кончится, — под этой же блокировкой, то есть уже после добавления
            var task = Task.Run(() => LoadAsync(key));
            Loads[key] = task;
            return task;
        }
    }

    private static async Task<Bitmap?> LoadAsync(Key key)
    {
        try
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = (ReaderForTests ?? ModIcon.Read)(key.File.Path); // файл открывается один раз
                var bitmap = bytes is null ? null : Decode(bytes, key.Width);
                ToMemory(bitmap is null ? NoIcon(key.File) : key, bitmap);
                return bitmap;
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception)
        {
            // значок — украшение: что бы ни случилось с чужим архивом, окно покажет логотип или буквы
            return null;
        }
        finally
        {
            lock (Loads) Loads.Remove(key);
        }
    }

    /// <summary>
    /// Уменьшить до <paramref name="size"/> по короткой стороне: неквадратную иконку плитка обрежет до квадрата
    /// (UniformToFill), и короткая сторона останется чёткой. Мелкую не растягиваем — растянет окно при показе, а в памяти
    /// лишнего не будет.
    /// </summary>
    private static Bitmap? Decode(byte[] bytes, int size)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            if (ModIcon.PngSize(bytes) is not { } png) return Bitmap.DecodeToWidth(stream, size, BitmapInterpolationMode.HighQuality);
            size = Math.Min(size, Math.Min(png.Width, png.Height));
            return png.Width > png.Height
                ? Bitmap.DecodeToHeight(stream, size, BitmapInterpolationMode.HighQuality)
                : Bitmap.DecodeToWidth(stream, size, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception)
        {
            // байты из чужого архива — что угодно; Skia на мусоре падает чем придётся. Для окна это одно: иконки нет
            return null;
        }
    }

    // ---- память

    private static bool FromMemory(Key key, out Bitmap? bitmap)
    {
        lock (Lru)
        {
            if (!LruIndex.TryGetValue(key, out var node) && !LruIndex.TryGetValue(NoIcon(key.File), out node))
            {
                bitmap = null;
                return false;
            }
            Lru.Remove(node);
            Lru.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return true;
        }
    }

    private static void ToMemory(Key key, Bitmap? bitmap)
    {
        // «иконки нет» тоже занимает место в словаре: считаем его маленькой иконкой, чтобы записей не копилось без конца
        var bytes = bitmap is null ? 1024L : (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        lock (Lru)
        {
            if (LruIndex.Remove(key, out var old))
            {
                Lru.Remove(old);
                _lruBytes -= old.Value.Bytes;
            }
            LruIndex[key] = Lru.AddFirst((key, bitmap, bytes));
            _lruBytes += bytes;
            // вытесненные не освобождаем (Dispose): иконка может ещё стоять в окне, её подберёт сборщик мусора
            while (_lruBytes > MemoryBudget && Lru.Count > 1 && Lru.Last is { } last)
            {
                Lru.RemoveLast();
                LruIndex.Remove(last.Value.Key);
                _lruBytes -= last.Value.Bytes;
            }
        }
    }
}
