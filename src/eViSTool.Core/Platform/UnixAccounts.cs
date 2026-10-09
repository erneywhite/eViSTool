using System.ComponentModel;
using System.Diagnostics;

namespace eViSTool.Core.Platform;

/// <summary>Пользователь Linux из базы учётных записей (getent passwd) и имя его основной группы.</summary>
public sealed record UnixUser(string Name, int Uid, int Gid, string Home, string Group);

/// <summary>
/// Учётные записи и права на Linux — для службы systemd: от кого запускать агента и хватит ли этому пользователю прав.
/// Всё через обычные программы системы (getent, stat, find, runuser, test): заводить свои привязки к libc ради одной
/// редкой команды незачем, а у программ поведение то же, что увидит сам человек в терминале. Разбор и выбор
/// пользователя — чистые функции, их можно проверить на любой системе.
/// </summary>
public static class UnixAccounts
{
    /// <summary>Пользователь по имени; null — такого нет (или getent недоступен).</summary>
    public static UnixUser? Find(string name)
    {
        var (code, output, _) = Run("getent", "passwd", name);
        if (code != 0 || ParsePasswd(output.Trim()) is not { } user) return null;
        // имя группы — для Group= и подсказки chown; не нашлась — systemd поймёт и номер
        var (gcode, group, _) = Run("getent", "group", user.Gid.ToString());
        var groupName = gcode == 0 ? group.Split(':')[0].Trim() : "";
        return user with { Group = groupName.Length > 0 ? groupName : user.Gid.ToString() };
    }

    /// <summary>Строка passwd «имя:x:uid:gid:описание:домашняя папка:оболочка»; группа — пока номером.</summary>
    public static UnixUser? ParsePasswd(string line)
    {
        var parts = line.Split(':');
        if (parts.Length < 7 || parts[0].Length == 0 || !int.TryParse(parts[2], out var uid) || !int.TryParse(parts[3], out var gid))
            return null;
        return new UnixUser(parts[0], uid, gid, parts[5], parts[3]);
    }

    /// <summary>Чья папка или файл (имя владельца); null — не узнать (нет такого пути).</summary>
    public static string? OwnerOf(string path)
    {
        var (code, output, _) = Run("stat", "-c", "%U", "--", path);
        var owner = output.Trim();
        return code == 0 && owner.Length > 0 ? owner : null;
    }

    /// <summary>
    /// Первое, что внутри папки (и она сама) принадлежит не этому пользователю: путь и владелец; null — всё его или
    /// папки нет. Частый случай — data, созданная запуском eViSTool от root: агенту от другого пользователя туда не писать.
    /// </summary>
    public static (string Path, string Owner)? ForeignEntry(string dir, string user)
    {
        if (!Directory.Exists(dir)) return null;
        var (_, output, _) = Run("find", dir, "!", "-user", user, "-printf", "%u\t%p\n", "-quit");
        var line = output.Split('\n', 2)[0];
        var tab = line.IndexOf('\t');
        return tab > 0 ? (line[(tab + 1)..], line[..tab]) : null;
    }

    /// <summary>
    /// Может ли пользователь читать (r), писать (w) или запускать (x) путь — проверяет сама система, от имени этого
    /// пользователя (runuser), со всеми папками по пути, группами и ACL. От root — про любого; от обычного
    /// пользователя — только про себя, про другого null (не узнать).
    /// </summary>
    public static bool? CanAccess(string user, string path, char access)
    {
        if (access is not ('r' or 'w' or 'x')) throw new ArgumentOutOfRangeException(nameof(access));
        var test = $"-{access}";
        if (user == System.Environment.UserName) return Run("test", test, path).Code == 0;
        if (!System.Environment.IsPrivilegedProcess) return null;
        var (code, _, error) = Run("runuser", "-u", user, "--", "test", test, path);
        // runuser нет или он сам не смог сменить пользователя — не знаем (у test код только 0 или 1)
        return code is 0 or 1 && !error.Contains("runuser", StringComparison.Ordinal) ? code == 0 : null;
    }

    /// <summary>
    /// От кого запускать агента в службе: названный явно; иначе — владелец первой из папок (данные сервера, папка
    /// eViSTool), если это не root. Всё принадлежит root — null: молча запускать сервер от root нельзя.
    /// </summary>
    public static string? PickServiceUser(string? explicitUser, params string?[] owners)
    {
        if (!string.IsNullOrWhiteSpace(explicitUser)) return explicitUser.Trim();
        return owners.FirstOrDefault(o => !string.IsNullOrEmpty(o) && o != "root");
    }

    /// <summary>Запустить программу и дождаться: код выхода и что она написала. Нет такой программы — код -1.</summary>
    public static (int Code, string Output, string Error) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // своё stdin программе не отдаём: systemctl и прочие не должны ничего спрашивать
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return (p.ExitCode, output.Result, error.Result);
        }
        catch (Win32Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
