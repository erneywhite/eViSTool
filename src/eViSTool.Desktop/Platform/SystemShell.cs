using System.Diagnostics;
using System.Runtime.Versioning;
using eViSTool.Core.Platform;

namespace eViSTool.Desktop.Platform;

/// <summary>
/// Действия с оболочкой системы: открыть ссылку или папку, показать файл в папке, удалить в корзину. На Windows —
/// Проводник и Корзина (как в окне на WPF), на Linux — xdg-open, D-Bus FileManager1 и корзина freedesktop.
/// </summary>
internal static class SystemShell
{
    public static void OpenUrl(string url) => Open(url);

    public static void OpenFolder(string dir) => Open(dir);

    private static void Open(string target)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        else
            Run("xdg-open", target);
    }

    /// <summary>Открыть папку и выделить в ней файл.</summary>
    public static void ShowInFolder(string path)
    {
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            WindowsExplorer.ShowInFolder(path);
            return;
        }
        // файловые менеджеры GNOME, KDE, XFCE понимают FileManager1.ShowItems; нет такого — просто папку
        var uri = "file://" + FreedesktopTrash.Escape(path);
        if (!Run("gdbus", "call", "--session", "--dest", "org.freedesktop.FileManager1", "--object-path", "/org/freedesktop/FileManager1",
                "--method", "org.freedesktop.FileManager1.ShowItems", $"['{uri}']", ""))
            Open(Path.GetDirectoryName(path)!);
    }

    /// <summary>В корзину (файл или папку).</summary>
    public static void MoveToTrash(string path)
    {
        if (OperatingSystem.IsWindows()) WindowsRecycleBin.Send(path);
        else FreedesktopTrash.Send(path);
    }

    /// <summary>
    /// Открыть Корзину системы. Менеджер файлов не ждём: окно Корзины живёт, пока его не закроют. Не запустился —
    /// в журнал ошибок, окно программы не страдает.
    /// </summary>
    public static void OpenTrash()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true })?.Dispose(); // как в окне на WPF
            else
            {
                try { Start("gio", "open", "trash:///"); }
                catch (System.ComponentModel.Win32Exception) { Start("xdg-open", "trash:///"); } // нет gio — xdg-open
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>Запустить и не ждать (Linux). Нет такой программы — Win32Exception.</summary>
    private static void Start(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi); // Dispose отпускает только описатель, сам процесс живёт дальше
    }

    /// <summary>Запустить и дождаться (до 5 с); false — не запустилось или вышло с ошибкой.</summary>
    private static bool Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(5000)) return true; // xdg-open может ждать сам; считаем, что открылось
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static class WindowsRecycleBin
    {
        public static void Send(string path)
        {
            const Microsoft.VisualBasic.FileIO.UIOption ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
            if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, ui, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, ui, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }

    /// <summary>
    /// Открывает папку в Проводнике и выделяет файл. «explorer /select» и SHOpenFolderAndSelectItems ломаются, если окна
    /// Проводника перехватывает утилита вкладок: папка открывается, выделение теряется. Поэтому открываем папку, ждём её
    /// окно в Shell.Application.Windows() и выделяем файл уже в нём (как в окне на WPF).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static class WindowsExplorer
    {
        public static void ShowInFolder(string path)
        {
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);
            var thread = new Thread(() =>
            {
                try
                {
                    var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                    var before = FolderWindows(shell, dir).Count;
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                    for (var i = 0; i < 30; i++)
                    {
                        Thread.Sleep(100);
                        var windows = FolderWindows(shell, dir);
                        if (windows.Count <= before && i < 15) continue;
                        if (windows.Count == 0) continue;
                        dynamic doc = windows[^1];
                        var item = doc.Folder.ParseName(name);
                        if (item is null) return;
                        doc.SelectItem(item, 1 | 4 | 8 | 16); // выделить, снять прочие, прокрутить, фокус
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

        private static List<object> FolderWindows(dynamic shell, string dir)
        {
            var result = new List<object>();
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    dynamic doc = w.Document;
                    string p = doc.Folder.Self.Path;
                    if (string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) result.Add(doc);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // окна без Folder
                }
            }
            return result;
        }
    }
}
