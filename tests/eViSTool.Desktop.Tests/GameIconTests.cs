using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using eViSTool.Desktop.Controls;
using eViSTool.Desktop.Converters;

namespace eViSTool.Desktop.Tests;

public sealed class GameIconTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evistool-tests", "game-" + Guid.NewGuid().ToString("N"));

    public GameIconTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [AvaloniaFact]
    public void No_game_no_icon()
    {
        Assert.Null(GameIcon.From([null, "", _dir, Path.Combine(_dir, "missing")]));
        Assert.Null(GameIconConverter.Instance.Convert(new[] { _dir }, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Раскладка архива для Linux: exe нет, значок — assets/gameicon.png (у игры 512×512).</summary>
    [AvaloniaFact]
    public void Linux_layout_uses_assets_picture()
    {
        File.WriteAllBytes(Path.Combine(_dir, "Vintagestory.dll"), []);
        Directory.CreateDirectory(Path.Combine(_dir, "assets"));
        var png = new WriteableBitmap(new PixelSize(512, 512), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        png.Save(Path.Combine(_dir, "assets", "gameicon.png"), PngBitmapEncoderOptions.Default);

        var icon = GameIcon.From([null, _dir]);
        Assert.NotNull(icon);
        Assert.Equal(new PixelSize(GameIcon.Size, GameIcon.Size), icon.PixelSize);
        Assert.Same(icon, GameIcon.From([_dir])); // кэш по пути
    }

    [AvaloniaFact]
    public void Broken_picture_is_no_icon()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "assets"));
        File.WriteAllText(Path.Combine(_dir, "assets", "gameicon.png"), "not a png");
        Assert.Null(GameIcon.From([_dir]));
    }

    /// <summary>Windows: значок из exe (подойдёт любой exe со значком — берём Проводник под именем игры).</summary>
    [AvaloniaFact]
    public void Windows_exe_icon_is_64px()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "значок из exe — только на Windows");
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        File.Copy(explorer, Path.Combine(_dir, "Vintagestory.exe"));

        var icon = GameIcon.From([_dir]);
        Assert.NotNull(icon);
        Assert.Equal(new PixelSize(64, 64), icon.PixelSize);

        // значок не пустой: есть непрозрачные пиксели
        var pixels = new byte[64 * 64 * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { icon.CopyPixels(new PixelRect(0, 0, 64, 64), handle.AddrOfPinnedObject(), pixels.Length, 64 * 4); }
        finally { handle.Free(); }
        Assert.Contains(pixels.Where((_, i) => i % 4 == 3), a => a > 0);
    }
}
