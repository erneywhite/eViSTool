using System.Text.RegularExpressions;
using eViSTool.Core.Localization;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Hosting;

/// <summary>Откуда известен путь к миру сервера у хостера — в порядке, в котором источники проверяются.</summary>
public enum HostedWorldFoundBy { Config, Log, Saves }

/// <summary>
/// Мир сервера у хостера. <paramref name="Path"/> — путь на FTP; <paramref name="ServerPath"/> — путь с машины сервера, как
/// его записали serverconfig.json или лог (повторные «/» схлопнуты; у найденного в Saves — null); <paramref name="Entry"/> —
/// запись листинга, null — по этому пути файла нет (сервер ещё не создавал мир). <paramref name="DataDir"/> — папка данных
/// сервера на FTP (у G-Portal — корень).
/// </summary>
public sealed record HostedWorldLocation(string Path, string? ServerPath, HostedWorldFoundBy FoundBy, RemoteEntry? Entry, string DataDir)
{
    public bool Exists => Entry is not null;

    public string Name => RemotePath.Name(Path);
}

/// <summary>Откуда взята копия мира: сам мир (сервер остановлен) или целая копия, которую сделала игра (Backups, BackupSaves).</summary>
public enum HostedWorldOrigin { World, GameBackup }

/// <summary>
/// Что будет скачано: <paramref name="File"/> — запись листинга (по ней же потом проверяется, что файл не менялся);
/// <paramref name="World"/> — где сам мир (у копии игры на работающем сервере может не найтись — null).
/// </summary>
public sealed record HostedWorldSource(HostedWorldOrigin Origin, RemoteEntry File, HostedWorldLocation? World);

/// <summary>Шаг операции с копией мира: скачивание мира, сбор настроек и данных модов, заливка мира на сервер.</summary>
public enum HostedBackupStep { World, Extra, Upload, ExtraUpload }

/// <summary>
/// Ход операции: шаг и сколько байт из общего размера этого шага уже прошло. У <see cref="HostedBackupStep.ExtraUpload"/>
/// (возврат настроек и данных модов на сервер) — сколько файлов из скольких: их десятки мелких, и время уходит на
/// команды FTP, а не на байты.
/// </summary>
public readonly record struct HostedTransfer(HostedBackupStep Step, long Done, long Total);

/// <summary>Что не так с копией мира — окно по нему может подсказать следующий шаг (остановить сервер, /genbackup).</summary>
public enum HostedWorldProblem
{
    /// <summary>Пути к миру нет ни в конфиге, ни в логе, и в Saves миров нет.</summary>
    NotFound,

    /// <summary>Путь известен, а файла по нему нет.</summary>
    Missing,

    /// <summary>В Saves несколько миров, и не понять, какой из них сервера.</summary>
    Ambiguous,

    /// <summary>
    /// Сервер работает, а свежей целой копии игры нет (нет совсем, старая или уже скачана): остановить сервер или
    /// сделать /genbackup.
    /// </summary>
    NeedsStop,

    /// <summary>Мир менялся во время скачивания.</summary>
    Changed,

    /// <summary>Копия игры менялась во время скачивания (сервер её ещё пишет).</summary>
    CopyChanged,

    /// <summary>Файл передался не целиком.</summary>
    Incomplete,

    /// <summary>Файл — не база SQLite, то есть не мир.</summary>
    NotAWorld,

    /// <summary>База мира обрезана: файл короче, чем говорит её заголовок.</summary>
    Damaged,

    /// <summary>Восстановление без подтверждения «сервер остановлен по логу».</summary>
    NotStopped,

    /// <summary>На диске этой машины нет места под копию.</summary>
    NoSpace,

    /// <summary>Архив с настройками и данными модов повреждён.</summary>
    BadArchive,

    /// <summary>Восстановление сорвалось, на сервере остался прежний мир.</summary>
    RestoreUndone,

    /// <summary>Восстановление сорвалось, и прежний мир не вернулся на место: он на сервере как bak, вернёт следующая операция.</summary>
    RestoreStuck,
}

/// <summary>
/// Ошибка операции с копией мира сервера у хостера — с текстом для окна и причиной (<see cref="Problem"/>). Наследник
/// <see cref="IOException"/>: окно ловит IOException у всех операций с хостером.
/// </summary>
public sealed class HostedWorldException(HostedWorldProblem problem, string message, Exception? inner = null) : IOException(message, inner)
{
    public HostedWorldProblem Problem { get; } = problem;
}

/// <summary>
/// Где на сервере у хостера мир и его копии, сделанные игрой. Путь к миру — по порядку: WorldConfig.SaveFileLocation из
/// serverconfig.json (путь Windows из шаблона хостера пропускается), строка «Savegame &lt;путь&gt; loaded» в начале лога,
/// единственный *.vcdbs в Saves папки данных. Путь с машины сервера («//home//sid_…//vintageStory//Saves//x.vcdbs»)
/// переводится в путь на FTP так же, как папки модов (<see cref="HostedLayoutProbe.MatchAsync"/>): по хвостам пути папки.
/// </summary>
internal static partial class HostedWorldLocator
{
    public const string Extension = ".vcdbs";
    public const string SavesDir = "Saves";

    /// <summary>Папки данных, куда игра кладёт целые копии мира: /genbackup и копии перед обновлением игры.</summary>
    public static readonly string[] GameCopyDirs = ["Backups", "BackupSaves"];

    /// <summary>Имя файла мира (или его копии).</summary>
    public static bool IsWorldName(string name) => name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static async Task<HostedWorldLocation> LocateAsync(HostedSnapshot snapshot, CancellationToken ct)
    {
        var layout = snapshot.Layout;
        var ls = snapshot.Listings;
        var dataDir = layout.DataDir ?? layout.Root;

        if (ConfigPath(snapshot.ConfigJson) is { } fromConfig
            && await MatchAsync(ls, fromConfig, layout, dataDir, ct).ConfigureAwait(false) is { } byConfig)
            return byConfig with { FoundBy = HostedWorldFoundBy.Config };

        // shortcut: из лога — только текущий server-main.log (его начало уже прочитано раскладкой); лога нет (сервер как раз
        // запускается) — Logs/Archive не смотрим. Переделать, если путь из конфига и Saves перестанут выручать
        if (LogPath(snapshot.LogHead) is { } fromLog
            && await MatchAsync(ls, fromLog, layout, dataDir, ct).ConfigureAwait(false) is { } byLog)
            return byLog with { FoundBy = HostedWorldFoundBy.Log };

        var saves = await DirAsync(ls, dataDir, SavesDir, ct).ConfigureAwait(false);
        var worlds = saves is null
            ? []
            : (await ls.ListAsync(saves, ct).ConfigureAwait(false) ?? []).Where(e => !e.IsDirectory && IsWorldName(e.Name)).ToList();
        return worlds.Count switch
        {
            1 => new HostedWorldLocation(worlds[0].Path, null, HostedWorldFoundBy.Saves, worlds[0], dataDir),
            0 => throw new HostedWorldException(HostedWorldProblem.NotFound, Loc.T("hosted.worldNotFound")),
            _ => throw new HostedWorldException(HostedWorldProblem.Ambiguous,
                Loc.T("hosted.worldMany", string.Join(", ", worlds.Select(w => w.Name).Order(StringComparer.Ordinal)))),
        };
    }

    /// <summary>
    /// Самая свежая (по времени файла в листинге) целая копия мира в Backups и BackupSaves папки данных; нет — null.
    /// Время у всех записей из одного вида листинга (MLSD — UTC, LIST — время сервера), так что сравнивать их можно.
    /// </summary>
    public static async Task<RemoteEntry?> FreshestGameCopyAsync(HostedListings ls, string dataDir, CancellationToken ct)
    {
        var found = new List<RemoteEntry>();
        foreach (var name in GameCopyDirs)
            if (await DirAsync(ls, dataDir, name, ct).ConfigureAwait(false) is { } dir)
                found.AddRange((await ls.ListAsync(dir, ct).ConfigureAwait(false) ?? []).Where(e => !e.IsDirectory && IsWorldName(e.Name) && e.Size > 0));
        return found.OrderByDescending(e => e.Modified ?? DateTime.MinValue).ThenByDescending(e => e.Name, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Папка <paramref name="name"/> внутри <paramref name="parent"/>: точное имя, потом без учёта регистра; нет — null.</summary>
    public static async Task<string?> DirAsync(HostedListings ls, string parent, string name, CancellationToken ct)
    {
        var path = RemotePath.Combine(parent, name);
        if (await ls.IsDirectoryAsync(path, ct).ConfigureAwait(false)) return path;
        return await ls.DirectoryIgnoreCaseAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>WorldConfig.SaveFileLocation из serverconfig.json; нет или пусто — null.</summary>
    public static string? ConfigPath(JObject? config) =>
        (config?["WorldConfig"] as JObject)?["SaveFileLocation"] is JValue { Type: JTokenType.String } value
        && value.Value<string>() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    [GeneratedRegex(@"Savegame\s+(?<p>\S.*?\.vcdbs)\s+loaded", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SavegameLine();

    /// <summary>Путь из строки «Savegame &lt;путь&gt; loaded» начала лога; нет — null.</summary>
    public static string? LogPath(string? log) =>
        string.IsNullOrEmpty(log) ? null : SavegameLine().Match(log) is { Success: true } m ? m.Groups["p"].Value : null;

    [GeneratedRegex("/{2,}")]
    private static partial Regex RepeatedSlashes();

    /// <summary>
    /// Путь с машины сервера → мир на FTP: папка — по хвостам пути (как папки модов), имя файла — только точное. Путь
    /// Windows, путь не к .vcdbs, мусор («..», управляющие знаки) и папка, которой не видно, — null.
    /// Папка нашлась, а файла в ней нет — место мира с <see cref="HostedWorldLocation.Entry"/> = null.
    /// </summary>
    private static async Task<HostedWorldLocation?> MatchAsync(HostedListings ls, string serverPath, HostedLayout layout, string dataDir,
        CancellationToken ct)
    {
        var trimmed = serverPath.Trim();
        // shortcut: путь Windows пропускаем всегда — у известных хостеров сервер на Linux (у G-Portal в шаблоне до первого
        // запуска лежит путь с их сборочной машины). Появится хостер с сервером на Windows — сопоставлять и такие пути
        if (HostedLayoutProbe.IsWindowsPath(trimmed) || trimmed.Any(c => c is '\0' or '\r' or '\n')) return null;
        var display = RepeatedSlashes().Replace(trimmed, "/");

        string? dir;
        string name;
        if (trimmed.StartsWith('/') || trimmed.StartsWith('~'))
        {
            var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p is not ("~" or ".")).ToList();
            if (parts.Count == 0 || parts.Contains("..")) return null;
            name = parts[^1];
            dir = parts.Count == 1
                ? dataDir
                : await HostedLayoutProbe.MatchAsync(ls, "/" + string.Join('/', parts.Take(parts.Count - 1)), layout.Root, layout.DataDir,
                    layout.GameDir, ct).ConfigureAwait(false);
        }
        else
        {
            // относительный путь — от папки данных, как его понимает сервер
            string full;
            try
            {
                full = RemotePath.Combine(dataDir, trimmed);
            }
            catch (InvalidDataException)
            {
                return null;
            }
            name = RemotePath.Name(full);
            var parent = RemotePath.Parent(full);
            dir = await ls.IsDirectoryAsync(parent, ct).ConfigureAwait(false) ? parent : await ls.DirectoryIgnoreCaseAsync(parent, ct).ConfigureAwait(false);
        }
        if (dir is null || !IsWorldName(name)) return null;

        // имя — только точное: сервер на Linux откроет ровно его, а файл, отличный регистром, — не его мир (копия сохранила
        // бы не тот файл, а восстановление записало бы туда, откуда сервер не читает)
        var list = await ls.ListAsync(dir, ct).ConfigureAwait(false) ?? [];
        var hit = list.FirstOrDefault(e => !e.IsDirectory && e.Name == name);
        return new HostedWorldLocation(hit?.Path ?? RemotePath.Combine(dir, name), display, HostedWorldFoundBy.Config, hit, dataDir);
    }
}
