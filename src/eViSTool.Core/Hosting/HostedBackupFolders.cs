using eViSTool.Core.Profiles;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Имена папок копий мира по умолчанию (HostedBackups/&lt;имя&gt;): каждому серверу у хостера один раз назначается своя, по
/// имени профиля. Назначает окно при загрузке настроек и при добавлении профиля — копия по расписанию может сработать
/// раньше, чем кто-то откроет вкладку «Копии».
/// </summary>
public static class HostedBackupFolders
{
    private const int MaxLength = 60;

    private static readonly string[] Reserved =
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).Select(i => "COM" + i), .. Enumerable.Range(1, 9).Select(i => "LPT" + i)];

    /// <summary>
    /// Имя профиля как имя папки: знаки, которых нет в именах на Windows и Linux, заменены на «_», завершающие точки и
    /// пробелы убраны, зарезервированные имена Windows (CON, NUL, COM1…) получают «_» в конце, пустое — «Server».
    /// </summary>
    public static string Sanitize(string? profileName)
    {
        var text = string.Concat((profileName ?? "").Trim().Select(c => PathRules.IsBadInFileName(c) ? '_' : c));
        if (text.Length > MaxLength) text = text[..MaxLength];
        text = text.TrimEnd('.', ' ');
        if (text.Length == 0) return "Server";
        var stem = text.Split('.')[0].TrimEnd(' ');
        return Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase) ? text + "_" : text;
    }

    /// <summary>Первое свободное имя: «Имя», «Имя (2)», «Имя (3)»… Занятость — без учёта регистра.</summary>
    public static string Unique(string name, ISet<string> taken)
    {
        var candidate = name;
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{name} ({n})";
        return candidate;
    }

    /// <summary>
    /// Назначает папку каждому серверу у хостера, у которого её нет, она не годится в имя папки или уже стоит у профиля выше
    /// в списке (клон копирует настройки вместе с ней). Занятыми считаются папки профилей и всё, что уже лежит в
    /// <paramref name="backupsRoot"/>. Назначенное не меняется: переименование профиля папку не трогает, иначе копии
    /// «потерялись» бы. Папку по id из прошлых версий переименовывает в новое имя; не вышло (занята, открыта) —
    /// профиль остаётся на ней. Возвращает, поменялось ли что-то в настройках профилей.
    /// </summary>
    public static bool Assign(IReadOnlyList<GameProfile> profiles, string backupsRoot)
    {
        var hosted = profiles.Where(p => p.Hosted is not null).ToList();
        if (hosted.Count == 0) return false;

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in hosted)
            if (PathRules.FileIn(backupsRoot, p.Hosted!.BackupFolder) is not null) taken.Add(p.Hosted.BackupFolder!);
        try
        {
            if (Directory.Exists(backupsRoot))
                foreach (var dir in Directory.EnumerateDirectories(backupsRoot)) taken.Add(Path.GetFileName(dir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* папку не прочли — учтём только профили */ }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var p in hosted)
        {
            var c = p.Hosted!;
            if (PathRules.FileIn(backupsRoot, c.BackupFolder) is not null && seen.Add(c.BackupFolder!)) continue;

            var name = Unique(Sanitize(p.Name), taken);
            var legacy = PathRules.FileIn(backupsRoot, p.Id);
            if (string.IsNullOrEmpty(c.BackupFolder) && legacy is not null && Directory.Exists(legacy))
            {
                try
                {
                    Directory.Move(legacy, Path.Combine(backupsRoot, name));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    name = p.Id; // старое место продолжает работать
                }
            }
            c.BackupFolder = name;
            taken.Add(name);
            seen.Add(name);
            changed = true;
        }
        return changed;
    }
}
