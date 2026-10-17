namespace eViSTool.Core.Hosting;

/// <summary>
/// Пути на сервере у хостера: абсолютные, через «/», без «.» и «..», регистр важен (сервер обычно на Linux).
/// Пути приходят из чужих данных (листинг, serverconfig.json, ввод человека), поэтому «..» и управляющие знаки
/// отвергаются: перевод строки в пути FTP стал бы второй командой серверу.
/// </summary>
public static class RemotePath
{
    /// <summary>
    /// «\» → «/», повторные «/» схлопываются, «.» выбрасываются, в начале «/», в конце «/» нет (кроме корня).
    /// «..», нулевой знак и переводы строк — <see cref="InvalidDataException"/>.
    /// </summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Any(c => c is '\0' or '\r' or '\n'))
            throw new InvalidDataException("control character in a remote path");
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") throw new InvalidDataException($"'..' in a remote path: {path}");
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>Путь внутри папки. Ведущий «/» у <paramref name="relative"/> не уводит из <paramref name="dir"/>:
    /// пути в профиле всегда считаются от корня сервера.</summary>
    public static string Combine(string dir, string relative)
    {
        var rel = Normalize(relative);
        var root = Normalize(dir);
        if (rel == "/") return root;
        return root == "/" ? rel : root + rel;
    }

    /// <summary>Папка, где лежит путь; у корня — сам корень.</summary>
    public static string Parent(string path)
    {
        var p = Normalize(path);
        var cut = p.LastIndexOf('/');
        return cut <= 0 ? "/" : p[..cut];
    }

    /// <summary>Последняя часть пути; у корня — пустая строка.</summary>
    public static string Name(string path)
    {
        var p = Normalize(path);
        return p[(p.LastIndexOf('/') + 1)..];
    }

    /// <summary>Путь — сама папка <paramref name="root"/> или внутри неё (с учётом регистра).</summary>
    public static bool IsInside(string root, string path)
    {
        var r = Normalize(root);
        var p = Normalize(path);
        if (r == "/") return true;
        return p == r || p.StartsWith(r + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Путь относительно <paramref name="root"/> — для хранения в профиле (<see cref="HostedConnection"/>): «Mods»,
    /// «data/serverconfig.json»; сам корень — «.». Путь вне корня — <see cref="InvalidDataException"/>.
    /// </summary>
    public static string Relative(string root, string path)
    {
        var r = Normalize(root);
        var p = Normalize(path);
        if (!IsInside(r, p)) throw new InvalidDataException($"{p} is outside {r}");
        var rest = r == "/" ? p[1..] : p[r.Length..].TrimStart('/');
        return rest.Length == 0 ? "." : rest;
    }

    /// <summary>
    /// Имя файла с сервера → имя, годное для файла на этой машине на любой ОС: запрещённые Windows знаки — «_»,
    /// без точек и пробелов в конце, зарезервированные имена (CON, NUL, COM1…) — с «_» впереди.
    /// </summary>
    public static string SafeLocalName(string name)
    {
        var chars = name.Select(c => PathRules.IsBadInFileName(c) ? '_' : c).ToArray();
        var safe = new string(chars).TrimEnd('.', ' ');
        if (safe.Length == 0 || safe.All(c => c == '.')) return "_";
        var stem = safe.Split('.')[0].TrimEnd(' ');
        if (IsReserved(stem)) safe = "_" + safe;
        return safe;
    }

    private static bool IsReserved(string stem) =>
        stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
        || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9');
}
