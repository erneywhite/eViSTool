using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace eViSTool.App;

/// <summary>Действия с оболочкой Windows.</summary>
internal static partial class Shell
{
    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void OpenFolder(string dir) => Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });

    /// <summary>
    /// Открывает папку в Проводнике и выделяет файл. "explorer /select" ненадёжен
    /// (часто просто открывает папку), поэтому — через SHOpenFolderAndSelectItems.
    /// </summary>
    public static void ShowInFolder(string path)
    {
        path = Path.GetFullPath(path);
        if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
        {
            OpenFolder(Path.GetDirectoryName(path)!);
            return;
        }
        try
        {
            SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint flags);
}
