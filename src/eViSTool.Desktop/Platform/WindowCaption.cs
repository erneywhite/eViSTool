using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace eViSTool.Desktop.Platform;

/// <summary>Тёмный заголовок окна в цвет боковой колонки (Windows 11; на Windows 10 — просто тёмный). На Linux рамку рисует оконный менеджер.</summary>
internal static class WindowCaption
{
    public static void Dark(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        int dark = 1, caption = 0x001D2319 /* #19231D в формате 0x00BBGGRR */, text = 0x00DDE9E7;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
