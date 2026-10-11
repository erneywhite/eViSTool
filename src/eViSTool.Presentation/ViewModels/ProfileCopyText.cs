using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Общее у окон нового клиентского профиля и клона сервера: текст про копию модов и свободное место на диске.</summary>
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
}
