using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace eViSTool.App;

/// <summary>
/// Значок Vintage Story — из установленной у пользователя игры (её exe), как и её версия. Сами мы логотип
/// не распространяем: нет игры на этом компьютере — значка нет, окно показывает буквы «VS».
/// </summary>
internal static class GameIcon
{
    private static readonly string[] Exes = ["Vintagestory.exe", "VintagestoryServer.exe"];
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Значок из первой папки, где нашлась игра.</summary>
    public static ImageSource? From(IEnumerable<string?> gameDirs)
    {
        foreach (var dir in gameDirs.Where(d => !string.IsNullOrWhiteSpace(d)))
            foreach (var exe in Exes)
            {
                var path = Path.Combine(dir!, exe);
                if (File.Exists(path) && Load(path) is { } icon) return icon;
            }
        return null;
    }

    private static ImageSource? Load(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        ImageSource? result = null;
        var icons = new IntPtr[1];
        var ids = new uint[1];
        // 64×64 — чётко и на увеличенном масштабе экрана (в окне значок 30×30)
        if (PrivateExtractIcons(path, 0, 64, 64, icons, ids, 1, 0) > 0 && icons[0] != IntPtr.Zero)
        {
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                result = source;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { }
            finally
            {
                DestroyIcon(icons[0]);
            }
        }
        return Cache[path] = result;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
