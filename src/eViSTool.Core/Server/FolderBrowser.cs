namespace eViSTool.Core.Server;

/// <summary>
/// Папка на машине с сервером для окна выбора папки: где мы, куда вверх, какие подпапки и с чего можно начать (корни).
/// </summary>
public sealed record DirListing(string Path, string? Parent, IReadOnlyList<string> Dirs, IReadOnlyList<string> Roots)
{
    /// <summary>Подпапки прочитать не дали (нет прав) — список пуст не потому, что их нет.</summary>
    public bool Denied { get; init; }
}

/// <summary>
/// Обзор папок для окна на другом компьютере: его диалог выбора папки (например, папки для копий) ходит сюда, потому что
/// показать чужую файловую систему стандартным диалогом Windows нельзя. Только имена папок, без файлов и без записи.
/// </summary>
public static class FolderBrowser
{
    /// <summary>Подпапок в ответе не больше — дальше путь можно вписать руками.</summary>
    public const int MaxDirs = 2000;

    /// <summary>
    /// Подпапки папки path. Её нет — ближайшая существующая папка выше; пусто или ничего не нашлось — start.
    /// </summary>
    public static DirListing List(string? path, string start)
    {
        var dir = Existing(path) ?? Existing(start) ?? Roots().First();
        var info = new DirectoryInfo(dir);
        var dirs = new List<string>();
        var denied = false;
        try
        {
            foreach (var d in info.EnumerateDirectories())
            {
                if (IsHidden(d)) continue;
                dirs.Add(d.Name);
                if (dirs.Count >= MaxDirs) break;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            denied = true;
        }
        dirs.Sort(StringComparer.CurrentCultureIgnoreCase);
        return new DirListing(info.FullName, info.Parent?.FullName, dirs, Roots()) { Denied = denied };
    }

    /// <summary>С чего начать: на Windows — диски, на Linux — корень.</summary>
    public static IReadOnlyList<string> Roots()
    {
        if (!OperatingSystem.IsWindows()) return ["/"];
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
        return drives.Count > 0 ? drives : [System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"];
    }

    /// <summary>Скрытые: на Linux — с точки, на Windows — с признаком «скрытый» или «системный».</summary>
    private static bool IsHidden(DirectoryInfo d) => OperatingSystem.IsWindows()
        ? (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
        : d.Name.StartsWith('.');

    /// <summary>Сама папка или ближайшая существующая выше; относительный и пустой путь — null.</summary>
    private static string? Existing(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path.Trim())) return null;
        for (var d = new DirectoryInfo(path.Trim()); d is not null; d = d.Parent)
            if (d.Exists) return d.FullName;
        return null;
    }
}
