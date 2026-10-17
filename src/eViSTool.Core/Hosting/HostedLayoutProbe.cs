using System.Text;
using System.Text.RegularExpressions;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Hosting;

/// <summary>
/// Что прочитано с сервера при поиске раскладки: сама раскладка, serverconfig.json (запись листинга, текст и дерево),
/// начало лога сервера. Скан берёт это отсюда, а не читает второй раз; листинги — там же (<see cref="Listings"/>).
/// </summary>
public sealed record HostedSnapshot(HostedLayout Layout, RemoteEntry? Config, string? ConfigText, JObject? ConfigJson, string? LogHead)
{
    public required HostedListings Listings { get; init; }
}

/// <summary>
/// Где на сервере у хостера что лежит (проект, п. 3.6, с поправками по настоящему G-Portal). Папки модов ищутся так:
/// <list type="number">
/// <item>блок «Will search the following paths for mods» в начале Logs/server-main.log — точный список папок, где сервер
/// искал моды при последнем запуске;</item>
/// <item>закреплённые в профиле папки;</item>
/// <item>ModPaths из serverconfig.json: относительный путь — от папки игры, абсолютный (Linux) — по хвостам пути на FTP,
/// путь Windows пропускается молча (сервер на Linux его не ищет); пустой список — как у игры, Mods в папке игры и данных;</item>
/// <item>видимые Mods в корне, data/, VintagestoryData/.</item>
/// </list>
/// У G-Portal корень FTP — это и папка игры, и папка данных; gportal_mods — моды самого хостера, только для чтения.
/// </summary>
public static class HostedLayoutProbe
{
    public const string ConfigName = ProfileResolver.ServerConfigName;

    /// <summary>Папка модов самого хостера (G-Portal): показываем, но ничего в ней не трогаем и туда не ставим.</summary>
    public const string HosterModsDir = "gportal_mods";

    /// <summary>Сколько читать от начала лога сервера: блок папок модов — в первых строках запуска.</summary>
    public const int LogHeadSize = 64 * 1024;

    private const string ServerDll = "VintagestoryServer.dll";
    private static readonly string[] BaseMods = ["VSSurvivalMod.dll", "VSCreativeMod.dll", "VSEssentials.dll"];
    private static readonly string[] DataCandidates = ["", "data", "vintagestory", "VintagestoryData", "vintage-story/data"];
    private static readonly string[] SubdirDataCandidates = ["", "data", "VintagestoryData"];
    private static readonly string[] ModsCandidates = ["Mods", "data/Mods", "VintagestoryData/Mods"];

    /// <summary>Базовые моды игры во встроенной Mods (VSSurvivalMod.dll и т. п.) — не моды человека.</summary>
    public static bool IsBaseGameFile(string name) => BaseMods.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Папка модов хостера: только для чтения.</summary>
    public static bool IsHosterDir(string dir) => string.Equals(RemotePath.Name(dir), HosterModsDir, StringComparison.OrdinalIgnoreCase);

    public static async Task<HostedLayout> DetectAsync(IRemoteFiles files, HostedConnection settings, CancellationToken ct) =>
        (await ReadAsync(files, settings, ct).ConfigureAwait(false)).Layout;

    /// <summary>Раскладка и всё, что для неё прочитано (конфиг, начало лога).</summary>
    public static async Task<HostedSnapshot> ReadAsync(IRemoteFiles files, HostedConnection settings, CancellationToken ct,
        HostedListings? listings = null)
    {
        var root = Root(settings);
        var ls = listings ?? new HostedListings(files, root);
        var warnings = new List<string>();
        var pinnedGone = false;

        // ---- конфиг: закреплённый или в папках-кандидатах; где он, там и папка данных
        RemoteEntry? config = null;
        if (Pinned(root, settings.ConfigPath) is { } pinnedConfig)
        {
            config = await ls.FileAsync(pinnedConfig, ct).ConfigureAwait(false);
            pinnedGone |= config is null;
        }
        config ??= await FindConfigAsync(ls, root, ct).ConfigureAwait(false);
        var dataDir = config is null ? null : RemotePath.Parent(config.Path);

        // ---- папка игры
        string? gameDir = null;
        if (Pinned(root, settings.GameDir) is { } pinnedGame)
        {
            if (await IsGameDirAsync(ls, pinnedGame, ct).ConfigureAwait(false)) gameDir = pinnedGame;
            else pinnedGone = true;
        }
        gameDir ??= await FindGameDirAsync(ls, root, dataDir, ct).ConfigureAwait(false);

        // ---- текст конфига
        string? text = null;
        JObject? json = null;
        if (config is not null)
        {
            text = await files.ReadTextAsync(config.Path, ct).ConfigureAwait(false);
            try
            {
                json = ModConfigEditor.ParseText(text);
            }
            catch (JsonException ex)
            {
                warnings.Add(Loc.T("profile.configReadFailed", ConfigName, ex.Message));
            }
        }
        else warnings.Add(Loc.T("hosted.noConfig"));

        // ---- начало лога
        var log = await ReadLogHeadAsync(ls, RemotePath.Combine(dataDir ?? root, "Logs/server-main.log"), ct).ConfigureAwait(false);

        // ---- папки модов
        var dirs = new List<string>();
        foreach (var path in LogModPaths(log))
            if (await MatchAsync(ls, path, root, dataDir, gameDir, ct).ConfigureAwait(false) is { } found)
                dirs.Add(found);

        if (dirs.Count == 0 && settings.ModDirs is { Count: > 0 } pinnedDirs)
        {
            var alive = new List<string>();
            foreach (var rel in pinnedDirs)
                if (Pinned(root, rel) is { } dir && await ls.IsDirectoryAsync(dir, ct).ConfigureAwait(false))
                    alive.Add(dir);
            if (alive.Count == pinnedDirs.Count) dirs.AddRange(alive);
            else pinnedGone = true;
        }

        if (dirs.Count == 0 && json is not null)
        {
            var (modPaths, _) = ProfileResolver.ReadServerConfig(json);
            foreach (var path in modPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                // shortcut: путь Windows пропускаем всегда — у известных хостеров сервер на Linux (у G-Portal в шаблоне
                // лежит путь с их сборочной машины). Появится хостер с сервером на Windows — сопоставлять и такие пути
                if (IsWindowsPath(path)) continue;
                var found = IsAbsolute(path)
                    ? await MatchAsync(ls, path, root, dataDir, gameDir, ct).ConfigureAwait(false)
                    : await RelativeAsync(ls, gameDir ?? dataDir ?? root, path, ct).ConfigureAwait(false);
                if (found is null) warnings.Add(Loc.T("hosted.modPathHidden", path));
                else dirs.Add(found);
            }
            // список пуст — игра берёт Mods в своей папке и в папке данных
            if (modPaths.Count == 0)
                foreach (var dir in new[] { gameDir, dataDir }.OfType<string>())
                    if (await ls.IsDirectoryAsync(RemotePath.Combine(dir, "Mods"), ct).ConfigureAwait(false))
                        dirs.Add(RemotePath.Combine(dir, "Mods"));
        }

        if (dirs.Count == 0)
            foreach (var rel in ModsCandidates)
                if (await ls.IsDirectoryAsync(RemotePath.Combine(root, rel), ct).ConfigureAwait(false))
                    dirs.Add(RemotePath.Combine(root, rel));

        // одна папка — один раз: у G-Portal «Mods» и «//home//sid_…//vintageStory//Mods» — одна и та же
        dirs = [.. dirs.Distinct(StringComparer.Ordinal)];

        // ---- куда ставить: закреплённая, иначе <данные>/Mods, иначе первая; в папку хостера — никогда
        var writable = dirs.Where(d => !IsHosterDir(d)).ToList();
        string? install = null;
        if (Pinned(root, settings.InstallDir) is { } pinnedInstall)
        {
            if (writable.Contains(pinnedInstall, StringComparer.Ordinal)) install = pinnedInstall;
            else pinnedGone = true;
        }
        install ??= dataDir is not null && writable.Contains(RemotePath.Combine(dataDir, "Mods"), StringComparer.Ordinal)
            ? RemotePath.Combine(dataDir, "Mods")
            : writable.FirstOrDefault();
        // встроенная Mods игры отдельно от папки данных: хостер может очистить её при обновлении игры. У G-Portal папка
        // данных и есть папка игры — там это обычная папка модов, предупреждать не о чем
        if (install is not null && gameDir is not null && dataDir is not null && gameDir != dataDir
            && install == RemotePath.Combine(gameDir, "Mods"))
            warnings.Add(Loc.T("hosted.builtinInstall", install));

        if (dirs.Count == 0) warnings.Add(Loc.T("hosted.noMods"));
        if (pinnedGone) warnings.Insert(0, Loc.T("hosted.layoutChanged"));

        var layout = new HostedLayout(root, config?.Path, dataDir, gameDir, dirs, install, warnings);
        return new HostedSnapshot(layout, config, text, json, log) { Listings = ls };
    }

    /// <summary>Корень сервера на FTP из настроек («» и null — «/»).</summary>
    public static string Root(HostedConnection settings) =>
        RemotePath.Normalize(string.IsNullOrWhiteSpace(settings.Root) ? "/" : settings.Root);

    private static string? Pinned(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        try
        {
            return RemotePath.Combine(root, relative);
        }
        catch (InvalidDataException)
        {
            return null; // в настройках мусор («..») — ищем заново
        }
    }

    private static async Task<RemoteEntry?> FindConfigAsync(HostedListings ls, string root, CancellationToken ct)
    {
        foreach (var rel in DataCandidates)
            if (await ls.FileAsync(RemotePath.Combine(RemotePath.Combine(root, rel.Length == 0 ? "." : rel), ConfigName), ct).ConfigureAwait(false) is { } hit)
                return hit;
        foreach (var dir in (await ls.ListAsync(root, ct).ConfigureAwait(false) ?? []).Where(e => e.IsDirectory))
            foreach (var rel in SubdirDataCandidates)
                if (await ls.FileAsync(RemotePath.Combine(rel.Length == 0 ? dir.Path : RemotePath.Combine(dir.Path, rel), ConfigName), ct).ConfigureAwait(false) is { } hit)
                    return hit;
        return null;
    }

    private static async Task<bool> IsGameDirAsync(HostedListings ls, string dir, CancellationToken ct) =>
        await ls.FileAsync(RemotePath.Combine(dir, ServerDll), ct).ConfigureAwait(false) is not null
        || await ls.FileAsync(RemotePath.Combine(dir, "Mods/VSSurvivalMod.dll"), ct).ConfigureAwait(false) is not null;

    private static async Task<string?> FindGameDirAsync(HostedListings ls, string root, string? dataDir, CancellationToken ct)
    {
        foreach (var dir in new[] { root, dataDir }.OfType<string>().Distinct())
            if (await IsGameDirAsync(ls, dir, ct).ConfigureAwait(false)) return dir;
        // в подпапках корня — только по VintagestoryServer.dll: листать ещё и их Mods — лишние запросы
        foreach (var dir in (await ls.ListAsync(root, ct).ConfigureAwait(false) ?? []).Where(e => e.IsDirectory))
            if (await ls.FileAsync(RemotePath.Combine(dir.Path, ServerDll), ct).ConfigureAwait(false) is not null)
                return dir.Path;
        return null;
    }

    /// <summary>Начало лога (лог по FTP бывает большим — целиком не качаем). Нет лога или он не читается — null.</summary>
    private static async Task<string?> ReadLogHeadAsync(HostedListings ls, string path, CancellationToken ct)
    {
        if (await ls.FileAsync(path, ct).ConfigureAwait(false) is not { } log) return null;
        try
        {
            byte[] bytes;
            if (log.Size <= ZipProbe.WholeFileLimit)
            {
                using var buffer = new MemoryStream();
                await ls.Files.DownloadAsync(log.Path, buffer, null, ct).ConfigureAwait(false);
                bytes = buffer.ToArray();
            }
            // shortcut: у G-Portal передача, оборванная раньше конца файла, роняет соединение — адаптер переподключается.
            // Если это станет заметно (большие логи, каждый скан), запоминать разбор по размеру и времени лога
            else bytes = await ls.Files.ReadRangeAsync(log.Path, 0, LogHeadSize, ct).ConfigureAwait(false);
            var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, LogHeadSize));
            return head;
        }
        catch (RemoteFilesException ex) when (ex.Kind is RemoteFailure.NotFound or RemoteFailure.Denied or RemoteFailure.RangeUnsupported)
        {
            return null; // лог — подсказка, без него раскладка найдётся по конфигу
        }
    }

    private static readonly Regex LogPathLine = new(@"^[^\[]*\[[A-Za-z]+\]\s{2,}(?<p>[^\s\[].*?)\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Пути из блока «Will search the following paths for mods:» в логе сервера — строки с отступом сразу после него.
    /// Блока нет — пусто.
    /// </summary>
    public static IReadOnlyList<string> LogModPaths(string? log)
    {
        if (string.IsNullOrEmpty(log)) return [];
        var lines = log.Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains("Will search the following paths for mods", StringComparison.Ordinal));
        if (start < 0) return [];
        var paths = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var m = LogPathLine.Match(lines[i].TrimEnd('\r'));
            if (!m.Success) break;
            paths.Add(m.Groups["p"].Value);
        }
        return paths;
    }

    /// <summary>Путь Windows: с буквой диска или с обратными слэшами.</summary>
    public static bool IsWindowsPath(string path)
    {
        path = path.Trim();
        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' || path.Contains('\\');
    }

    private static bool IsAbsolute(string path) => path.TrimStart().StartsWith('/') || path.TrimStart().StartsWith('~');

    private static async Task<string?> RelativeAsync(HostedListings ls, string baseDir, string relative, CancellationToken ct)
    {
        string path;
        try
        {
            path = RemotePath.Combine(baseDir, relative.Trim());
        }
        catch (InvalidDataException)
        {
            return null;
        }
        if (await ls.IsDirectoryAsync(path, ct).ConfigureAwait(false)) return path;
        return await ls.DirectoryIgnoreCaseAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Абсолютный путь с машины сервера («/home/container/data/Mods», «~/vintageStory/Mods», «//home//sid_1//x//Mods») →
    /// папка на FTP: хвосты пути от длинного к короткому, сначала внутри папки данных, потом игры, потом от корня;
    /// сначала с учётом регистра, потом без. Не нашлось — null.
    /// </summary>
    public static async Task<string?> MatchAsync(HostedListings ls, string serverPath, string root, string? dataDir, string? gameDir,
        CancellationToken ct)
    {
        var parts = serverPath.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p is not ("~" or ".")).ToList();
        if (parts.Count > 0 && parts[0].Length == 2 && parts[0][1] == ':') parts.RemoveAt(0); // буква диска
        if (parts.Count == 0 || parts.Contains("..") || parts.Any(p => p.Any(c => c is '\0' or '\r' or '\n'))) return null;

        var bases = new[] { dataDir, gameDir, root }.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (var ignoreCase in new[] { false, true })
            for (var k = parts.Count; k >= 1; k--)
            {
                var tail = string.Join('/', parts.Skip(parts.Count - k));
                foreach (var dir in bases)
                {
                    var candidate = RemotePath.Combine(dir, tail);
                    if (!ignoreCase)
                    {
                        if (await ls.IsDirectoryAsync(candidate, ct).ConfigureAwait(false)) return candidate;
                    }
                    else if (await ls.DirectoryIgnoreCaseAsync(candidate, ct).ConfigureAwait(false) is { } real) return real;
                }
            }
        return null;
    }
}
