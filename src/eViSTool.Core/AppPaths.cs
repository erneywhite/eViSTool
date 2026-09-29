namespace eViSTool.Core;

/// <summary>
/// Где eViSTool хранит свои данные. Программа портабельная: всё лежит в папке data рядом с exe.
/// Если туда писать нельзя (например, exe в Program Files) — %LOCALAPPDATA%\eViSTool.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> _root = new(Resolve);

    public static string Root => _root.Value;

    /// <summary>Данные рядом с exe (true) или в профиле пользователя (false).</summary>
    public static bool IsPortable => Root.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ModBackups => Path.Combine(Root, "ModBackups");
    public static string Downloads => Path.Combine(Root, "Downloads");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Cache => Path.Combine(Root, "cache");

    /// <summary>Где жили данные до портабельности (для переезда).</summary>
    public static string LegacySettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eViSTool", "settings.json");
    public static string LegacyLocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "eViSTool");

    private static string Resolve()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (CanWrite(portable)) return portable;

        Directory.CreateDirectory(LegacyLocalRoot);
        return LegacyLocalRoot;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(file));
            File.Copy(file, dest);
            File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(file)); // порядок версий в хранилище — по этому времени
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDir(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Однократный переезд из старых мест (AppData) в папку данных: настройки копируются,
    /// хранилище версий модов переносится. Старые файлы настроек не удаляются — на всякий случай.
    /// </summary>
    public static void MigrateLegacy()
    {
        if (!IsPortable) return; // и так живём в LOCALAPPDATA

        try
        {
            if (!File.Exists(SettingsFile) && File.Exists(LegacySettingsFile))
                File.Copy(LegacySettingsFile, SettingsFile);

            var oldBackups = Path.Combine(LegacyLocalRoot, "ModBackups");
            if (Directory.Exists(oldBackups) && !Directory.Exists(ModBackups))
            {
                // Directory.Move не умеет между дисками (exe может лежать на S:), поэтому копия + удаление
                CopyDir(oldBackups, ModBackups);
                Directory.Delete(oldBackups, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // переезд не критичен: в худшем случае начнём с чистых настроек
        }
    }
}
