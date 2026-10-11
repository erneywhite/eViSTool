using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Значок Vintage Story — из установленной у пользователя игры, как и её версия. Сами мы логотип не распространяем:
/// нет игры на этом компьютере — значка нет, окно показывает буквы «VS».
/// </summary>
/// <remarks>
/// На Windows значок берётся из exe игры. На Linux exe нет (игра — <c>Vintagestory</c> и <c>Vintagestory.dll</c>),
/// зато в папке assets лежит тот же значок картинкой: gameicon.png (512×512) и gameicon.ico. Папка assets общая для
/// всех систем, поэтому на Windows она — запасной путь, если из exe значок не достался.
/// </remarks>
public static class GameIcon
{
    /// <summary>Сторона значка: 64 px — чётко и на увеличенном масштабе экрана (в окне значок 24–30 px).</summary>
    public const int Size = 64;

    private static readonly string[] Exes = ["Vintagestory.exe", "VintagestoryServer.exe"];
    private static readonly string[] Pictures = [Path.Combine("assets", "gameicon.png"), Path.Combine("assets", "gameicon.ico")];
    private static readonly Dictionary<string, Bitmap?> Cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Значок из первой папки, где нашлась игра; не нашлась — null.</summary>
    public static Bitmap? From(IEnumerable<string?> gameDirs)
    {
        foreach (var dir in gameDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (OperatingSystem.IsWindows())
                foreach (var exe in Exes)
                {
                    var path = Path.Combine(dir, exe);
                    if (File.Exists(path) && Cached(path, FromExe) is { } icon) return icon;
                }
            foreach (var picture in Pictures)
            {
                var path = Path.Combine(dir, picture);
                if (File.Exists(path) && Cached(path, FromPicture) is { } icon) return icon;
            }
        }
        return null;
    }

    private static Bitmap? Cached(string path, Func<string, Bitmap?> load)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
            return Cache[path] = load(path);
        }
    }

    private static Bitmap? FromPicture(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, Size);
        }
        catch (Exception)
        {
            // файл не наш или битый (Skia на мусоре падает чем придётся) — значит, значка нет
            return null;
        }
    }

    // ---- Windows: значок из exe без WPF и WinForms — HICON → пиксели BGRA → Bitmap

    [SupportedOSPlatform("windows")]
    private static Bitmap? FromExe(string path)
    {
        var icons = new IntPtr[1];
        var ids = new uint[1];
        if (PrivateExtractIcons(path, 0, Size, Size, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero) return null;
        try
        {
            return FromHicon(icons[0]);
        }
        finally
        {
            DestroyIcon(icons[0]);
        }
    }

    [SupportedOSPlatform("windows")]
    private static Bitmap? FromHicon(IntPtr hicon)
    {
        if (!GetIconInfo(hicon, out var info)) return null;
        try
        {
            if (info.hbmColor == IntPtr.Zero) return null; // чёрно-белый значок: у игры такого нет
            var bm = new BITMAP();
            if (GetObject(info.hbmColor, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;
            int width = bm.bmWidth, height = bm.bmHeight;

            var pixels = Pixels(info.hbmColor, width, height);
            if (pixels is null) return null;
            // у старых значков без альфа-канала прозрачность — в маске: белое там прозрачно
            if (!HasAlpha(pixels) && Pixels(info.hbmMask, width, height) is { } mask)
                for (var i = 3; i < pixels.Length; i += 4)
                    pixels[i] = mask[i - 1] == 0 ? (byte)255 : (byte)0;

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(),
                    new PixelSize(width, height), new Vector(96, 96), width * 4);
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    /// <summary>Пиксели растра сверху вниз, 32 бита BGRA (маску GDI переводит так же: 0 или FFFFFF).</summary>
    [SupportedOSPlatform("windows")]
    private static byte[]? Pixels(IntPtr bitmap, int width, int height)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // отрицательная высота — строки сверху вниз
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            return GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == height ? pixels : null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static bool HasAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return true;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr gdiObject, int size, ref BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr gdiObject);
}
