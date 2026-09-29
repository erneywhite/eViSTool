using System.Diagnostics;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace eViSTool.App;

/// <summary>Действия с оболочкой Windows.</summary>
internal static class Shell
{
    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void OpenFolder(string dir) => Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });

    /// <summary>Удаление в Корзину (файл или папка распакованного мода).</summary>
    public static void MoveToRecycleBin(string path)
    {
        const UIOption ui = UIOption.OnlyErrorDialogs;
        if (Directory.Exists(path))
            FileSystem.DeleteDirectory(path, ui, RecycleOption.SendToRecycleBin);
        else
            FileSystem.DeleteFile(path, ui, RecycleOption.SendToRecycleBin);
    }

    /// <summary>
    /// Открывает папку в Проводнике и выделяет файл.
    /// "explorer /select" и SHOpenFolderAndSelectItems ломаются, если окна Проводника перехватывает
    /// утилита вкладок (ExplorerTabUtility и т. п.): папка открывается, выделение теряется.
    /// Поэтому открываем папку, ждём её окно/вкладку в Shell.Application.Windows() и выделяем файл уже в ней.
    /// </summary>
    public static void ShowInFolder(string path)
    {
        path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);

        // COM оболочки требует STA, а ждать окно на UI-потоке нельзя
        var thread = new Thread(() =>
        {
            try
            {
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                var before = FolderWindows(shell, dir).Count;
                OpenFolder(dir);

                for (var i = 0; i < 30; i++)
                {
                    Thread.Sleep(100);
                    var windows = FolderWindows(shell, dir);
                    if (windows.Count <= before && i < 15) continue; // ждём новое окно, потом согласны на любое
                    if (windows.Count == 0) continue;

                    dynamic doc = windows[^1];
                    var item = doc.Folder.ParseName(name);
                    if (item is null) return;
                    // 1 — выделить, 4 — снять прочие, 8 — прокрутить к нему, 16 — фокус
                    doc.SelectItem(item, 1 | 4 | 8 | 16);
                    return;
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // папка уже открыта — выделение не критично
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Документы открытых окон/вкладок Проводника, показывающих папку <paramref name="dir"/>.</summary>
    private static List<object> FolderWindows(dynamic shell, string dir)
    {
        var result = new List<object>();
        foreach (dynamic w in shell.Windows())
        {
            try
            {
                dynamic doc = w.Document;
                string p = doc.Folder.Self.Path;
                if (string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    result.Add(doc);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // окна Internet Explorer и прочие — без Folder
            }
        }
        return result;
    }
}
