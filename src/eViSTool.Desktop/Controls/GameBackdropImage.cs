using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Картинка фона из игры (docs/design.md, раздел 5): заставка главного меню, уменьшенная до <see cref="Width"/> px и
/// размытая один раз при загрузке. Окно растягивает её на себя, поэтому размывать на каждом кадре (BlurEffect) незачем:
/// дальше это обычный Bitmap. Какой файл брать — решает <see cref="Core.Game.GameBackdrop"/>.
/// </summary>
/// <remarks>
/// Кэш — в памяти на время работы, по пути файла: заставку, подменённую игроком, окно покажет со следующего запуска.
/// Файл не читается — null (окно рисует заглушку); неудача тоже запоминается, чтобы не читать битый файл раз в час.
/// </remarks>
public static class GameBackdropImage
{
    /// <summary>Ширина, до которой декодировать: окно около 1400 px, а после размытия мелочь всё равно не видна.</summary>
    public const int Width = 480;

    /// <summary>
    /// Сила размытия — σ гауссова ядра в пикселях картинки шириной 480: около 5 экранных пикселей при окне 1400 px
    /// (картинка там растянута примерно втрое). Уменьшение и растяжение сами смягчают — больше не нужно.
    /// </summary>
    public const double Sigma = 1.7;

    private static readonly Dictionary<string, Bitmap?> Cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Загрузить не на потоке окна: декодирование и размытие — десятки миллисекунд.</summary>
    /// <param name="underlay">Цвет под прозрачными местами (у праздничных заставок есть прозрачность) — фон боковой панели.</param>
    public static Task<Bitmap?> LoadAsync(string path, Color underlay) => Task.Run(() => Load(path, underlay));

    /// <summary>Картинка из файла (или из кэша, если этот файл уже грузили); не читается — null.</summary>
    public static Bitmap? Load(string path, Color underlay)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
            return Cache[path] = Decode(path, underlay);
        }
    }

    private static Bitmap? Decode(string path, Color underlay)
    {
        WriteableBitmap? result = null;
        try
        {
            using var stream = File.OpenRead(path);
            using var decoded = Bitmap.DecodeToWidth(stream, Width, BitmapInterpolationMode.HighQuality);
            var size = decoded.PixelSize;
            if (size.Width < 1 || size.Height < 1) return null;

            // пиксели в известном виде (BGRA, альфа умножена) — Avalonia перекодирует из того, что дал декодер
            result = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var frame = result.Lock())
            {
                decoded.CopyPixels(frame);
                var stride = size.Width * 4;
                var pixels = new byte[stride * size.Height];
                for (var y = 0; y < size.Height; y++) Marshal.Copy(frame.Address + y * frame.RowBytes, pixels, y * stride, stride);

                Flatten(pixels, underlay);
                Blur(pixels, size.Width, size.Height, Sigma);

                for (var y = 0; y < size.Height; y++) Marshal.Copy(pixels, y * stride, frame.Address + y * frame.RowBytes, stride);
            }
            return result;
        }
        catch (Exception)
        {
            // файл не картинка или битый (Skia на мусоре падает чем придётся), нет доступа — значит, фона нет
            result?.Dispose();
            return null;
        }
    }

    /// <summary>Подложить цвет под прозрачное (пиксели BGRA с умноженной альфой): после этого картинка непрозрачна.</summary>
    internal static void Flatten(byte[] bgra, Color underlay)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var rest = 255 - bgra[i + 3];
            if (rest == 0) continue;
            bgra[i] = (byte)(bgra[i] + (underlay.B * rest + 127) / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] + (underlay.G * rest + 127) / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] + (underlay.R * rest + 127) / 255);
            bgra[i + 3] = 255;
        }
    }

    /// <summary>
    /// Гауссово размытие непрозрачной картинки BGRA — по строкам, потом по столбцам. За краем повторяется крайний
    /// пиксель: края не темнеют (размытие с нулями за краем дало бы тёмную кайму). Альфа не трогается.
    /// </summary>
    internal static void Blur(byte[] bgra, int width, int height, double sigma)
    {
        var radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[radius * 2 + 1];
        var sum = 0f;
        for (var k = -radius; k <= radius; k++) sum += kernel[k + radius] = (float)Math.Exp(-k * k / (2 * sigma * sigma));
        for (var k = 0; k < kernel.Length; k++) kernel[k] /= sum;

        var rows = new float[bgra.Length];
        for (var y = 0; y < height; y++)
        {
            var line = y * width;
            for (var x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var i = (line + Math.Clamp(x + k, 0, width - 1)) * 4;
                    var w = kernel[k + radius];
                    b += bgra[i] * w;
                    g += bgra[i + 1] * w;
                    r += bgra[i + 2] * w;
                }
                var o = (line + x) * 4;
                rows[o] = b;
                rows[o + 1] = g;
                rows[o + 2] = r;
            }
        }
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var i = (Math.Clamp(y + k, 0, height - 1) * width + x) * 4;
                    var w = kernel[k + radius];
                    b += rows[i] * w;
                    g += rows[i + 1] * w;
                    r += rows[i + 2] * w;
                }
                var o = (y * width + x) * 4;
                bgra[o] = (byte)Math.Clamp(b + 0.5f, 0, 255);
                bgra[o + 1] = (byte)Math.Clamp(g + 0.5f, 0, 255);
                bgra[o + 2] = (byte)Math.Clamp(r + 0.5f, 0, 255);
            }
    }
}
