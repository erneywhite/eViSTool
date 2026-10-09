using System.Text;
using System.Text.RegularExpressions;

namespace eViSTool.Core.Server;

/// <summary>Сервер VS, найденный на этой машине. Script — server.sh, из которого взяты пути (null — нашли без него).</summary>
public sealed record ServerLocation(string GameDir, string DataDir, string? Script);

/// <summary>Пути из шапки server.sh: VSPATH (папка игры) и DATAPATH (данные). null — не указан или не разобрать.</summary>
public sealed record ServerScript(string? GameDir, string? DataDir);

/// <summary>
/// Где на этой машине сервер VS — для агента, запущенного без окна (на Linux окна нет, и пути ему никто не передаст).
/// Папка игры: явный ключ; своя папка (агента положили в папку игры); соседние папки (../server); VSPATH из server.sh
/// поблизости (в своей папке, в соседних, в стандартном месте); стандартное место из server.sh setup.
/// Папка данных: явный ключ; DATAPATH из server.sh этой игры; иначе та, что выбрала бы сама игра без --dataPath.
/// </summary>
public static partial class ServerLocator
{
    public const string ScriptName = "server.sh";

    /// <summary>Куда ставит сервер официальный «server.sh setup».</summary>
    public const string StandardGameDir = "/home/vintagestory/server";

    /// <summary>Стандартные места установки на этой системе (на Windows их нет: сервер там ставят куда угодно).</summary>
    public static IReadOnlyList<string> StandardGameDirs { get; } = OperatingSystem.IsWindows() ? [] : [StandardGameDir];

    private static StringComparison PathCompare =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Папка игры сервера: в ней VintagestoryServer.dll (на Windows — и VintagestoryServer.exe).</summary>
    public static bool IsGameDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "VintagestoryServer.dll"))
        && (!OperatingSystem.IsWindows() || File.Exists(Path.Combine(dir, "VintagestoryServer.exe")));

    /// <summary>
    /// Папка данных, которую выбирает сама игра без --dataPath (GamePaths в VintagestoryAPI): ApplicationData +
    /// VintagestoryData. На Linux это ~/.config/VintagestoryData (или $XDG_CONFIG_HOME/VintagestoryData) того, от чьего
    /// имени работает сервер, на Windows — %APPDATA%\VintagestoryData.
    /// </summary>
    public static string DefaultDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "VintagestoryData");

    /// <summary>
    /// Найти сервер. game и data — из ключей (null — искать); ownDir — папка агента; tried — куда заглядывали, для
    /// сообщения «не нашёл»; standardDirs — для тестов (по умолчанию <see cref="StandardGameDirs"/>).
    /// </summary>
    public static ServerLocation? Locate(string? game, string? data, string ownDir, ICollection<string>? tried = null,
        IReadOnlyList<string>? standardDirs = null)
    {
        standardDirs = [.. (standardDirs ?? StandardGameDirs).Select(Full)];
        ownDir = Full(ownDir);
        var siblings = game is null ? Siblings(ownDir) : [];

        // server.sh поблизости — по нему находится игра (VSPATH) и её данные (DATAPATH)
        var scripts = new List<(string Path, ServerScript Script)>();
        var places = new List<string>();
        if (game is not null) places.Add(Full(game));
        places.Add(ownDir);
        places.AddRange(siblings);
        places.AddRange(standardDirs);
        foreach (var dir in places)
            if (ReadScript(dir) is { } found && !scripts.Any(s => SamePath(s.Path, found.Path)))
                scripts.Add(found);

        // кандидаты в папку игры — по порядку; при явном ключе — только он
        var candidates = new List<(string Dir, string? Script)>();
        if (game is not null)
        {
            candidates.Add((Full(game), null));
        }
        else
        {
            candidates.Add((ownDir, null));
            candidates.AddRange(siblings.Select(d => (d, (string?)null)));
            candidates.AddRange(scripts.Where(s => s.Script.GameDir is not null).Select(s => (Full(s.Script.GameDir!), (string?)s.Path)));
            candidates.AddRange(standardDirs.Select(d => (d, (string?)null)));
        }

        // в «где искал» — без перечня всех соседей: их может быть много, хватит «<родитель>/*»
        if (tried is not null)
        {
            tried.Add(candidates[0].Dir);
            if (siblings.Count > 0) tried.Add(Path.Combine(Path.GetDirectoryName(ownDir)!, "*"));
            foreach (var (dir, _) in candidates.Skip(1 + siblings.Count))
                if (!tried.Any(t => SamePath(t, dir))) tried.Add(dir);
        }

        foreach (var (dir, viaScript) in candidates)
        {
            if (!IsGameDir(dir)) continue;
            if (data is not null) return new ServerLocation(dir, Full(data), viaScript);
            // данные — из server.sh этой игры: того, чей VSPATH указывает на неё, или лежащего в её папке без VSPATH
            var own = scripts.FirstOrDefault(s => s.Script.DataDir is not null && s.Script.GameDir is { } g && SamePath(Full(g), dir));
            if (own.Path is null)
                own = scripts.FirstOrDefault(s => s.Script.DataDir is not null && s.Script.GameDir is null
                                                  && SamePath(Path.GetDirectoryName(s.Path)!, dir));
            return own.Path is not null
                ? new ServerLocation(dir, Full(own.Script.DataDir!), own.Path)
                : new ServerLocation(dir, DefaultDataDir, viaScript);
        }
        return null;
    }

    /// <summary>
    /// Шапка server.sh → VSPATH и DATAPATH. Берётся первое присваивание с начала строки (внутри функций скрипт меняет
    /// переменные сам — это не настройки). Значение — как в bash: в одинарных кавычках как есть, в двойных — с подстановкой
    /// переменных из той же шапки, без кавычек — до пробела. Подстановка, которую не вычислить (команда, неизвестная
    /// переменная, «${2:-…}»), — null: лучше не найти, чем найти не то. Пути — только полные.
    /// </summary>
    public static ServerScript ParseScript(string text)
    {
        var vars = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var m = Assignment().Match(raw.TrimEnd('\r'));
            if (!m.Success || vars.ContainsKey(m.Groups["name"].Value)) continue;
            vars[m.Groups["name"].Value] = ShellValue(m.Groups["value"].Value, vars);
        }
        return new ServerScript(Rooted(vars.GetValueOrDefault("VSPATH")), Rooted(vars.GetValueOrDefault("DATAPATH")));
    }

    [GeneratedRegex(@"^(?:export\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)=(?<value>.*)$")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^\$(?:\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}|(?<name>[A-Za-z_][A-Za-z0-9_]*))")]
    private static partial Regex Variable();

    private static string? Rooted(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) ? path : null;

    /// <summary>Значение присваивания bash (часть после «=»): кавычки, «\», $VAR и ${VAR}. Не вычислить — null.</summary>
    private static string? ShellValue(string value, IReadOnlyDictionary<string, string?> vars)
    {
        var sb = new StringBuilder();
        var quoted = false; // внутри двойных кавычек
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!quoted && (c is ' ' or '\t' or ';')) break; // дальше — команда или комментарий
            switch (c)
            {
                case '\'' when !quoted:
                    var end = value.IndexOf('\'', i + 1);
                    if (end < 0) return null;
                    sb.Append(value, i + 1, end - i - 1);
                    i = end;
                    break;
                case '"':
                    quoted = !quoted;
                    break;
                case '\\':
                    if (i + 1 >= value.Length) return null;
                    // в двойных кавычках «\» что-то значит только перед $ ` " \ — иначе остаётся как есть
                    if (quoted && value[i + 1] is not ('$' or '`' or '"' or '\\')) sb.Append(c);
                    else sb.Append(value[++i]);
                    break;
                case '$':
                    var m = Variable().Match(value[i..]);
                    if (!m.Success || vars.GetValueOrDefault(m.Groups["name"].Value) is not { } known) return null;
                    sb.Append(known);
                    i += m.Length - 1;
                    break;
                case '`':
                    return null;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return quoted ? null : sb.ToString();
    }

    /// <summary>server.sh в папке → его пути (нет файла или не прочитать — null).</summary>
    private static (string Path, ServerScript Script)? ReadScript(string dir)
    {
        var path = Path.Combine(dir, ScriptName);
        try
        {
            return File.Exists(path) ? (path, ParseScript(File.ReadAllText(path))) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Соседние папки (в той же родительской) — по имени.</summary>
    private static List<string> Siblings(string dir)
    {
        if (Path.GetDirectoryName(dir) is not { Length: > 0 } parent) return [];
        try
        {
            return [.. Directory.GetDirectories(parent).Select(Full).Where(d => !SamePath(d, dir)).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string a, string b) => string.Equals(Full(a), Full(b), PathCompare);
}
