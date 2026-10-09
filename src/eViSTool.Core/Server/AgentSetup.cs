using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Platform;

namespace eViSTool.Core.Server;

/// <summary>Насколько важно: всё хорошо; стоит обратить внимание; так сервер работать не будет.</summary>
public enum SetupLevel { Ok, Warning, Problem }

/// <summary>Итог одной проверки команды «setup».</summary>
public sealed record SetupCheck(SetupLevel Level, string Text);

/// <summary>
/// Проверки команды «setup» для агента без окна: от того ли пользователя запущена, читается ли игра, можно ли писать
/// в данные сервера и в папку eViSTool, есть ли .NET для сервера, не запущен ли сервер уже кем-то другим. Каждая
/// проверка — ответ своими словами и, где можно, команда, которая всё поправит.
/// </summary>
public static class AgentSetup
{
    /// <summary>Сколько файлов данных просматривать в поисках чужих: мир с сотнями тысяч файлов — это уже не про права.</summary>
    public const int OwnerScanLimit = 20_000;

    /// <summary>
    /// Команду запустили от root, а данные сервера (или папка eViSTool) принадлежат другому пользователю: файлы, созданные
    /// от root, служба потом не сможет менять, а секреты зашифровались бы ключом root. Возвращает, от чьего имени надо
    /// запускать (имя владельца); null — запускать можно (не root, не Linux или всё и так принадлежит root).
    /// </summary>
    public static string? OwnerToRunAs(string dataDir, string appDir)
    {
        if (OperatingSystem.IsWindows() || !UnixAccount.IsRoot) return null;
        foreach (var dir in new[] { dataDir, appDir })
            if (NearestExisting(dir) is { } existing && UnixAccount.OwnerOf(existing) is { } uid and not 0)
                return UnixAccount.Describe(uid);
        return null;
    }

    /// <summary>Файлы сервера читаются: папка открывается, сборка сервера (и exe на Windows) — тоже.</summary>
    public static SetupCheck CheckGame(string gameDir)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(gameDir).FirstOrDefault();
            string[] files = OperatingSystem.IsWindows() ? [ServerExecutable.DllName, ServerExecutable.ExeName] : [ServerExecutable.DllName];
            foreach (var name in files)
            {
                using var file = File.OpenRead(Path.Combine(gameDir, name));
                _ = file.ReadByte();
            }
            return new SetupCheck(SetupLevel.Ok, Loc.T("setup.okGame"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SetupCheck(SetupLevel.Problem, Loc.T("setup.badGame", Reason(ex), Fix(gameDir)));
        }
    }

    /// <summary>
    /// Папка данных сервера: в неё можно писать, serverconfig.json можно менять. Папки ещё нет — сервер создаст её сам,
    /// но это и признак опечатки в пути (тогда получится новый пустой мир), поэтому — предупреждение.
    /// </summary>
    public static SetupCheck CheckData(string dataDir)
    {
        try
        {
            if (!Directory.Exists(dataDir))
            {
                var parent = NearestExisting(dataDir);
                if (parent is null) return new SetupCheck(SetupLevel.Problem, Loc.T("setup.badData", dataDir, Fix(dataDir)));
                Probe(parent);
                return new SetupCheck(SetupLevel.Warning, Loc.T("setup.newData", dataDir));
            }
            Probe(dataDir);
            var config = Path.Combine(dataDir, "serverconfig.json");
            if (File.Exists(config))
                using (new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { } // открыть на запись, не меняя
            return new SetupCheck(SetupLevel.Ok, Loc.T("setup.okData"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SetupCheck(SetupLevel.Problem, Loc.T("setup.badData", Reason(ex), Fix(dataDir)));
        }
    }

    /// <summary>
    /// Linux: файлы в данных сервера, которые этот пользователь менять не может (чужие и без записи для группы и всех) —
    /// так бывает, если сервер однажды запустили от root. Сервер на них споткнётся. null — таких нет или проверять незачем
    /// (Windows, root, папки нет).
    /// </summary>
    public static SetupCheck? CheckDataOwners(string dataDir)
    {
        if (OperatingSystem.IsWindows() || UnixAccount.IsRoot || !Directory.Exists(dataDir)) return null;
        var me = UnixAccount.CurrentUid;
        var count = 0;
        (string Path, uint Owner)? example = null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dataDir, "*", options).Prepend(dataDir).Take(OwnerScanLimit))
            {
                if (UnixAccount.Stat(entry) is not { } stat || stat.Uid == me) continue;
                // чужой, но открытый на запись группе или всем — значит, так и задумано
                if ((stat.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) continue;
                count++;
                example ??= (entry, stat.Uid);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return example is { } e
            ? new SetupCheck(SetupLevel.Warning, Loc.T("setup.foreignFiles", count, e.Path, UnixAccount.Describe(e.Owner), Fix(dataDir)))
            : null;
    }

    /// <summary>Папка настроек eViSTool (где agent.json и agents): создаётся, в неё пишется.</summary>
    public static SetupCheck CheckSettingsDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) XdgDirs.CreatePrivate(dir); // как AppPaths: ключи и секреты — только владельцу
            Probe(dir);
            return new SetupCheck(SetupLevel.Ok, Loc.T("setup.okSettings", dir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SetupCheck(SetupLevel.Problem, Loc.T("setup.badSettings", Reason(ex), Fix(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? dir)));
        }
    }

    /// <summary>
    /// Linux: есть ли .NET, который нужен серверу (из его runtimeconfig.json), — тем же поиском, что и при запуске сервера.
    /// null — на Windows: там сервер — exe, и .NET для него ставит сама игра.
    /// </summary>
    public static SetupCheck? CheckDotnet(string gameDir)
    {
        if (OperatingSystem.IsWindows()) return null;
        var need = ServerExecutable.RequiredRuntime(Path.Combine(gameDir, ServerExecutable.DllName));
        var dotnet = ServerExecutable.FindDotnet();
        if (dotnet is null || (need is not null && !ServerExecutable.HasRuntime(dotnet, need)))
            return new SetupCheck(SetupLevel.Problem, Loc.T("srv.dotnetMissing", need?.Version.Major ?? 10));
        return new SetupCheck(SetupLevel.Ok, need is null
            ? Loc.T("setup.okDotnetAny", dotnet)
            : Loc.T("setup.okDotnet", need.Version.ToString(2), dotnet));
    }

    /// <summary>
    /// Серверы из этой папки игры с этими данными, запущенные не агентом этого профиля (ownServerPid — его сервер):
    /// второй сервер на тех же данных агент не запустит. Сервер без --dataPath работает с папкой игры по умолчанию.
    /// </summary>
    public static IReadOnlyList<int> OtherServers(string gameDir, string dataDir, int? ownServerPid) =>
        [.. GameProcess.FindServers(gameDir)
            .Where(s => s.Pid != ownServerPid && SameDir(s.DataPath ?? ServerLocator.DefaultDataDir, dataDir))
            .Select(s => s.Pid)];

    /// <summary>
    /// Чем поправить права: на Linux — отдать папку тому, кто запускает («user:» — и его группе); на Windows — своими
    /// словами, команды там нет.
    /// </summary>
    public static string Fix(string dir) => OperatingSystem.IsWindows()
        ? Loc.T("setup.fixWindows", dir)
        : Loc.T("setup.fixLinux", $"sudo chown -R {Environment.UserName}: {Shell.Quote(dir)}");

    /// <summary>Создать и удалить пустой файл: можно ли писать в папку.</summary>
    private static void Probe(string dir)
    {
        var probe = Path.Combine(dir, $".evistool-setup-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "");
        File.Delete(probe);
    }

    private static string Reason(Exception ex) => ex is UnauthorizedAccessException ? Loc.T("setup.denied", ex.Message) : ex.Message;

    /// <summary>Сама папка или ближайшая существующая над ней; null — нет даже корня.</summary>
    private static string? NearestExisting(string dir)
    {
        for (var d = Path.GetFullPath(dir); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (Directory.Exists(d)) return d;
        return null;
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <summary>Аргументы для командной строки, которую человек скопирует в оболочку (bash).</summary>
public static class Shell
{
    /// <summary>Как есть, если в нём нет особых знаков; иначе — в одинарных кавычках (кавычка внутри — «'\''»).</summary>
    public static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_' or ':' or '=' or ',' or '+' or '@')
            ? arg
            : "'" + arg.Replace("'", "'\\''") + "'";

    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));
}
