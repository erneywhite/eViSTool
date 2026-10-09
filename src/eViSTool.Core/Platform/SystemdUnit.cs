using System.Text;
using System.Text.RegularExpressions;

namespace eViSTool.Core.Platform;

/// <summary>
/// Служба systemd для агента без окна (Linux): юнит /etc/systemd/system/&lt;имя&gt;.service. Здесь только текст юнита по
/// параметрам, его можно проверить на любой системе; ставит службу команда агента «service install».
/// <para>
/// Как служба останавливается: SIGTERM получает только агент (KillMode=mixed), он сам останавливает сервер через /stop,
/// и мир сохраняется; что осталось после таймаута, добивает SIGKILL. Если агент упал или его убили (kill -9), systemd
/// сразу убивает и сервер, без сохранения (ExecStop при падении главного процесса не вызывается — проверено), и через
/// RestartSec поднимает агента заново, а тот — сервер. Осиротевший сервер новому агенту не мешает: его уже нет.
/// </para>
/// </summary>
public sealed partial record SystemdUnit
{
    public const string DefaultName = "evistool";
    public const string UnitDir = "/etc/systemd/system";

    /// <summary>С этим кодом агент выходит после самообновления: systemd запускает его заново, уже новую версию.</summary>
    public const int SelfUpdateExitCode = 75;

    /// <summary>Переменная окружения службы: агент видит, что работает под systemd, и знает имя своей службы.</summary>
    public const string ServiceVariable = "EVISTOOL_SERVICE";

    /// <summary>Первая строка юнита. По ней uninstall узнаёт юнит eViSTool и не тронет чужой с тем же именем.</summary>
    public const string Marker = "# eViSTool agent service";

    /// <summary>Имя службы без «.service».</summary>
    public required string Name { get; init; }

    /// <summary>Агент: абсолютный путь к его файлу.</summary>
    public required string ExecPath { get; init; }

    /// <summary>Ключи агента (--start и те, что дали при установке).</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Папка агента: рядом с ним его data.</summary>
    public required string WorkingDirectory { get; init; }

    public required string User { get; init; }
    public required string Group { get; init; }

    /// <summary>Папка данных сервера — для описания службы (у нескольких служб видно, какая чья).</summary>
    public string? DataDir { get; init; }

    /// <summary>Куда агенту и серверу нужно писать: папка eViSTool, данные сервера. Лежит что-то из них в /usr или /etc —
    /// ProtectSystem не ставим, он сделал бы эти папки доступными только для чтения.</summary>
    public IReadOnlyList<string> WritablePaths { get; init; } = [];

    /// <summary>Дополнительные переменные окружения (имя → значение).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Сколько systemd ждёт остановки, прежде чем добить SIGKILL. Агент сам ждёт сервер после /stop до 3 минут, потом
    /// шлёт SIGTERM и ждёт ещё минуту (ServerHost.StopTimeout и CtrlCTimeout) — systemd не должен оборвать это раньше.
    /// </summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Путь к юниту службы.</summary>
    public static string PathFor(string name, string dir = UnitDir) => $"{dir.TrimEnd('/')}/{name}.service";

    /// <summary>Имя службы из ключа --name: пусто — по умолчанию, «.service» на конце можно. Неподходящее — null.</summary>
    public static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DefaultName;
        name = name.Trim();
        if (name.EndsWith(".service", StringComparison.Ordinal)) name = name[..^".service".Length];
        // systemd допускает и «@» (шаблоны) и «\»-экранирование — нам они ни к чему
        return name.Length is > 0 and <= 200 && NameRule().IsMatch(name) ? name : null;
    }

    /// <summary>Юнит создан eViSTool: первая строка — <see cref="Marker"/>.</summary>
    public static bool IsOurs(string unitText) => unitText.StartsWith(Marker, StringComparison.Ordinal);

    public string Render()
    {
        var protectSystem = !WritablePaths.Any(IsSystemPath);
        var stop = (int)Math.Ceiling(StopTimeout.TotalSeconds);
        var sb = new StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');

        L(Marker);
        L("# Written by \"eViSTool.Agent service install\", removed by \"eViSTool.Agent service uninstall\".");
        L("# The agent keeps the Vintage Story server running; eViSTool on Windows manages it remotely.");
        L("[Unit]");
        L("Description=" + Escape("eViSTool agent for the Vintage Story server" + (DataDir is null ? "" : $" ({DataDir})")));
        L("Documentation=https://github.com/erneywhite/eViSTool/blob/main/docs/linux.md");
        L("After=network-online.target");
        L("Wants=network-online.target");
        L("# five failed starts in five minutes (wrong folders, no permissions) - stop trying instead of spinning forever");
        L("StartLimitIntervalSec=300");
        L("StartLimitBurst=5");
        L();
        L("[Service]");
        L("Type=simple");
        L("User=" + Escape(User));
        L("Group=" + Escape(Group));
        L("WorkingDirectory=" + Escape(WorkingDirectory));
        L("ExecStart=" + CommandLine([ExecPath, .. Args]));
        L($"Environment={ServiceVariable}={Name}");
        foreach (var (key, value) in Environment) L("Environment=" + QuoteArg($"{key}={value}", variables: false));
        L("# SIGTERM goes to the agent only: it stops the server with /stop and the world is saved");
        L("KillMode=mixed");
        L($"TimeoutStopSec={stop}");
        L("Restart=on-failure");
        L("RestartSec=5");
        L($"# {SelfUpdateExitCode}: the agent has updated itself and exits so that systemd starts the new version (not a failure)");
        L($"SuccessExitStatus={SelfUpdateExitCode}");
        L($"RestartForceExitStatus={SelfUpdateExitCode}");
        L("NoNewPrivileges=true");
        L("PrivateTmp=true");
        if (protectSystem) L("ProtectSystem=full");
        L("ProtectKernelTunables=true");
        L("ProtectKernelModules=true");
        L("ProtectControlGroups=true");
        L("RestrictSUIDSGID=true");
        L("LockPersonality=true");
        L();
        L("[Install]");
        L("WantedBy=multi-user.target");
        return sb.ToString();
    }

    /// <summary>Командная строка для ExecStart: каждый аргумент — отдельным словом, в кавычках, если нужно.</summary>
    public static string CommandLine(IEnumerable<string> args) => string.Join(' ', args.Select(a => QuoteArg(a)));

    /// <summary>
    /// Одно слово командной строки юнита. Простое (буквы, цифры, «/.-_:=+,@») — как есть; остальное — в двойных
    /// кавычках с экранированием «\» и «"». Спецификаторы «%» и переменные «$» systemd раскрывает и в кавычках —
    /// их удваиваем, чтобы путь с ними дошёл до агента буквально. В Environment= переменные не раскрываются
    /// (variables: false), там «$» — обычный знак.
    /// </summary>
    public static string QuoteArg(string arg, bool variables = true)
    {
        if (arg.Length > 0 && SimpleWord().IsMatch(arg)) return arg;
        var sb = new StringBuilder("\"");
        foreach (var c in arg)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '%': sb.Append("%%"); break;
                case '$' when variables: sb.Append("$$"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                case < ' ': sb.Append($"\\x{(int)c:x2}"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Значение строки юнита (WorkingDirectory, User, Description): кавычки там не снимаются, раскрываются
    /// только спецификаторы — «%» удваиваем.</summary>
    public static string Escape(string value) => value.Replace("%", "%%");

    private static bool IsSystemPath(string path) =>
        new[] { "/usr", "/etc", "/boot", "/efi" }.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));

    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]+$")]
    private static partial Regex NameRule();

    [GeneratedRegex(@"^[A-Za-z0-9_/.:=+,@\-]+$")]
    private static partial Regex SimpleWord();
}
