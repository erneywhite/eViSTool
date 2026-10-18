using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Tests;

public sealed class GameBackdropImageTests : IDisposable
{
    private static readonly Color Side = Color.FromRgb(0x11, 0x19, 0x13);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-tests", "backdrop-" + Guid.NewGuid().ToString("N"));

    public GameBackdropImageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>PNG заданного размера, залитый одним цветом (BGRA с умноженной альфой).</summary>
    private string Png(string name, int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var frame = bitmap.Lock())
        {
            var row = new byte[width * 4];
            for (var x = 0; x < width; x++) (row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]) = (b, g, r, a);
            for (var y = 0; y < height; y++) Marshal.Copy(row, 0, frame.Address + y * frame.RowBytes, row.Length);
        }
        var path = Path.Combine(_dir, name);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        return path;
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var pixels = new byte[size.Width * size.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, size.Width * 4); }
        finally { handle.Free(); }
        return pixels;
    }

    [AvaloniaFact]
    public void Decoded_small_and_without_dark_edges()
    {
        var path = Png("mainmenu1.png", 960, 540, 40, 160, 200);

        var bitmap = GameBackdropImage.Load(path, Side);
        Assert.NotNull(bitmap);
        Assert.Equal(new PixelSize(GameBackdropImage.Width, 270), bitmap.PixelSize); // пропорции сохранены

        // ровная заливка после размытия остаётся ровной до самого края: за краем повторяется крайний пиксель, а не чёрное
        var pixels = Pixels(bitmap);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.InRange(pixels[i], 39, 41);
            Assert.InRange(pixels[i + 1], 159, 161);
            Assert.InRange(pixels[i + 2], 199, 201);
            Assert.Equal(255, pixels[i + 3]);
        }

        Assert.Same(bitmap, GameBackdropImage.Load(path, Side)); // кэш по пути: второй раз файл не читается
    }

    [AvaloniaFact]
    public async Task Transparent_picture_gets_the_side_colour_underneath()
    {
        var path = Png("mainmenu-halloween.png", 640, 360, 0, 0, 0, 0);

        var bitmap = await GameBackdropImage.LoadAsync(path, Side);
        Assert.NotNull(bitmap);
        Assert.Equal(new PixelSize(GameBackdropImage.Width, 270), bitmap.PixelSize);
        var pixels = Pixels(bitmap);
        Assert.Equal(new byte[] { Side.B, Side.G, Side.R, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { Side.B, Side.G, Side.R, 255 }, pixels[^4..]);
    }

    [AvaloniaFact]
    public void Broken_or_missing_file_is_no_picture()
    {
        var broken = Path.Combine(_dir, "mainmenu2.png");
        File.WriteAllText(broken, "not a png");
        Assert.Null(GameBackdropImage.Load(broken, Side));
        Assert.Null(GameBackdropImage.Load(Path.Combine(_dir, "mainmenu3.png"), Side));
    }

    [Fact]
    public void Blur_keeps_edges_and_softens_the_border_between_colours()
    {
        // слева чёрное, справа белое: у краёв картинки цвета прежние, на границе — переход
        const int width = 40, height = 6;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var v = x < width / 2 ? (byte)0 : (byte)255;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (v, v, v, 255);
            }

        GameBackdropImage.Blur(pixels, width, height, GameBackdropImage.Sigma);

        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            Assert.Equal(0, pixels[row]);                       // левый край
            Assert.Equal(255, pixels[row + (width - 1) * 4]);   // правый край не потемнел
            Assert.InRange(pixels[row + (width / 2 - 1) * 4], 20, 127);  // у границы — уже не чёрное
            Assert.InRange(pixels[row + width / 2 * 4], 128, 235);       // и уже не белое
            Assert.Equal(255, pixels[row + 3]);
        }
    }

    [Fact]
    public void Flatten_puts_colour_under_transparency()
    {
        // полупрозрачный белый (умноженная альфа: 128,128,128,128) над цветом панели
        byte[] pixels = [128, 128, 128, 128, 10, 20, 30, 255];
        GameBackdropImage.Flatten(pixels, Side);
        Assert.Equal(new byte[] { (byte)(128 + (Side.B * 127 + 127) / 255), (byte)(128 + (Side.G * 127 + 127) / 255), (byte)(128 + (Side.R * 127 + 127) / 255), 255, 10, 20, 30, 255 }, pixels);
    }
}
