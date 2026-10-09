namespace eViSTool.Core.Platform;

/// <summary>Папки пользователя на Linux по соглашению XDG.</summary>
public static class XdgDirs
{
    /// <summary>Папка данных пользователя: $XDG_DATA_HOME или ~/.local/share; null — домашней папки нет.</summary>
    public static string? DataHome() =>
        DataHome(Environment.GetEnvironmentVariable("XDG_DATA_HOME"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string? DataHome(string? xdgDataHome, string? home)
    {
        // относительный путь в XDG_DATA_HOME спецификация велит не принимать
        if (!string.IsNullOrEmpty(xdgDataHome) && Path.IsPathRooted(xdgDataHome)) return xdgDataHome;
        if (string.IsNullOrEmpty(home) || !Path.IsPathRooted(home)) return null;
        var full = Path.GetFullPath(home);
        // у служебных пользователей домашней папки часто нет (/nonexistent) или это корень диска
        return full != Path.GetPathRoot(full) && Directory.Exists(full) ? Path.Combine(full, ".local", "share") : null;
    }

    /// <summary>Создать папку (и недостающие над ней) с правами 700, как велит XDG: в ней личное пользователя.</summary>
    public static void CreatePrivate(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            return;
        }
        var full = Path.GetFullPath(dir);
        if (Directory.Exists(full)) return;
        // права из CreateDirectory получает только последняя папка, промежуточные — обычные, поэтому сверху вниз
        if (Path.GetDirectoryName(full) is { } parent) CreatePrivate(parent);
        Directory.CreateDirectory(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
