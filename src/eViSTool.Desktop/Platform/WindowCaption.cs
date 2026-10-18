using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace eViSTool.Desktop.Platform;

/// <summary>Тёмный заголовок окна в цвет боковой колонки (Windows 11; на Windows 10 — просто тёмный). На Linux рамку рисует оконный менеджер.</summary>
internal static class WindowCaption
{
    public static void Dark(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        // цвета заголовка и его текста — токены темы; без темы остаётся просто тёмный заголовок
        if (Token(window, "Forest.SideColor") is { } side)
        {
            var caption = ColorRef(side);
            DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        }
        if (Token(window, "Forest.TextColor") is { } fore)
        {
            var text = ColorRef(fore);
            DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
        }
    }

    private static Color? Token(Window window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out var value) && value is Color color ? color : null;

    /// <summary>Цвет в формате COLORREF Windows: 0x00BBGGRR.</summary>
    internal static int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
