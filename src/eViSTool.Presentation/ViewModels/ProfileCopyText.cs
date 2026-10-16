using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Общее у окон нового клиентского профиля и клона сервера: текст про копию модов, свободное место на диске и вес
/// папок (миров, резервных копий).
/// </summary>
public static class ProfileCopyText
{
    /// <summary>Своя копия модов: откуда соберутся и что не один к одному (дубли, переименования, нет папки).</summary>
    public static string ModsText(IReadOnlyList<string> sources, IReadOnlyList<string> notes) =>
        sources.Count == 0 && notes.Count == 0 ? ""
            : Environment.NewLine + Loc.T("clone.modsFrom", string.Join(", ", sources))
              + string.Concat(notes.Select(n => Environment.NewLine + "• " + n));

    /// <summary>Свободное место на диске папки; не узнать (сетевой путь и т.п.) — null, тогда не мешаем.</summary>
    public static long? FreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Сколько весят файлы в папках (со всеми вложенными); папок, которых нет, и недоступного не считаем.</summary>
    public static long FolderBytes(IEnumerable<string> dirs)
    {
        long sum = 0;
        foreach (var dir in dirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    sum += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return sum;
    }
}
