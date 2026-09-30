using eViSTool.Core.Localization;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Profiles;

/// <summary>Что и как клонировать.</summary>
public sealed record CloneOptions
{
    /// <summary>Название нового профиля.</summary>
    public required string Name { get; init; }

    /// <summary>Папка данных нового профиля: не существует или пустая.</summary>
    public required string TargetDir { get; init; }

    /// <summary>false — «тот же мир» (копия сохранения); true — «новый мир» (без сохранений, новый сид).</summary>
    public bool NewWorld { get; init; }

    /// <summary>Название нового мира (для NewWorld); пусто — как название профиля.</summary>
    public string? WorldName { get; init; }

    /// <summary>Копировать Backups/BackupSaves (только для «того же мира»: бэкапы чужого мира новому не нужны).</summary>
    public bool IncludeBackups { get; init; }

    /// <summary>
    /// true — моды общие с исходным профилем: папка Mods не копируется, ModPaths клона ведут в папку модов исходного.
    /// false — у клона своя копия модов.
    /// </summary>
    public bool ShareMods { get; init; }

    /// <summary>Какой конфиг исходной папки станет serverconfig.json клона (в папке их может быть несколько).</summary>
    public string ConfigFile { get; init; } = ProfileCloner.MainConfig;
}

/// <summary>Конфиг сервера в исходной папке: какой мир он запускает.</summary>
public sealed record ServerConfigInfo(string FileName, string? WorldName, string? SaveFile, long SaveBytes)
{
    /// <summary>Сохранение найдено на диске (иначе мир ещё не создан или путь ведёт в никуда).</summary>
    public bool SaveExists => SaveBytes > 0;
}

public sealed record CloneFile(string Source, string Relative, long Size);

public sealed record ClonePlan
{
    public required GameProfile Source { get; init; }
    public required CloneOptions Options { get; init; }
    public required IReadOnlyList<CloneFile> Files { get; init; }

    /// <summary>Папки, которые надо создать (относительные пути) — в том числе пустые: на Mods ссылается конфиг.</summary>
    public IReadOnlyList<string> Directories { get; init; } = [];
    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>Конфиг-источник (null — в исходной папке его нет, сервер создаст свой).</summary>
    public string? ConfigPath { get; init; }

    /// <summary>Куда в клоне будет смотреть SaveFileLocation.</summary>
    public string? TargetSave { get; init; }
}

public sealed record CloneProgress(long DoneBytes, long TotalBytes, string File);

/// <summary>
/// Клонирование серверного профиля в новую папку данных. Мир = профиль: у клона свои настройки модов,
/// данные игроков и своё сохранение; моды — общие с исходным профилем или своя копия.
/// Пути внутри serverconfig.json перенацеливаются на новую папку —
/// в том числе записанные под другим пользователем (…\Administrator\…\VintagestoryData\… на машине с «Администратор»).
/// </summary>
public static class ProfileCloner
{
    public const string MainConfig = "serverconfig.json";

    // не копируются никогда: журналы и кэш сервер создаст заново, а ServerProfiles — это папки других профилей
    private static readonly string[] AlwaysSkipped = ["Logs", "Cache", ServerProfileLayout.ContainerName];
    private const string ModsDir = "Mods";
    private static readonly string[] BackupDirs = ["Backups", "BackupSaves"];
    private const string SavesDir = "Saves";
    private const string SaveExtension = ".vcdbs";

    /// <summary>Конфиги сервера в папке данных (serverconfig.json первым).</summary>
    public static IReadOnlyList<ServerConfigInfo> FindConfigs(string dataDir)
    {
        if (!Directory.Exists(dataDir)) return [];
        return Directory.EnumerateFiles(dataDir, "serverconfig*.json")
            .OrderBy(f => !string.Equals(Path.GetFileName(f), MainConfig, StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                try
                {
                    var world = ModConfigEditor.Load(f)["WorldConfig"] as JObject;
                    var save = ResolveSave(world?["SaveFileLocation"]?.ToString(), dataDir);
                    return new ServerConfigInfo(Path.GetFileName(f), world?["WorldName"]?.ToString(), save,
                        save is not null && File.Exists(save) ? new FileInfo(save).Length : 0);
                }
                catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException)
                {
                    return new ServerConfigInfo(Path.GetFileName(f), null, null, 0);
                }
            })
            .ToList();
    }

    /// <summary>Папка для клона: «…\VintagestoryData\ServerProfiles\duo» (см. <see cref="ServerProfileLayout"/>).</summary>
    public static string SuggestTargetDir(string sourceDir, string name) => ServerProfileLayout.SuggestDir(sourceDir, name);

    /// <summary>Что будет скопировано. Бросает InvalidOperationException с понятным текстом, если клонировать нельзя.</summary>
    public static ClonePlan Plan(GameProfile source, CloneOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Name)) throw new InvalidOperationException(Loc.T("clone.errName"));
        if (string.IsNullOrWhiteSpace(source.DataDir) || !Directory.Exists(source.DataDir))
            throw new InvalidOperationException(Loc.T("clone.errNoSource", source.DataDir));
        if (string.IsNullOrWhiteSpace(options.TargetDir)) throw new InvalidOperationException(Loc.T("clone.errNoTarget"));

        var from = Path.GetFullPath(source.DataDir).TrimEnd('\\', '/');
        var to = Path.GetFullPath(options.TargetDir).TrimEnd('\\', '/');
        // внутрь исходной нельзя — кроме её контейнера ServerProfiles: он при копировании пропускается
        if (IsSameOrInside(from, to) || (IsSameOrInside(to, from) && !ServerProfileLayout.IsInsideContainerOf(to, from)))
            throw new InvalidOperationException(Loc.T("clone.errNested"));
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            throw new InvalidOperationException(Loc.T("clone.errNotEmpty", to));
        if (File.Exists(to)) throw new InvalidOperationException(Loc.T("clone.errNotEmpty", to));

        var configPath = Path.Combine(from, options.ConfigFile);
        if (!File.Exists(configPath)) configPath = null;
        var sourceSave = configPath is null ? null
            : ResolveSave((ModConfigEditor.Load(configPath)["WorldConfig"] as JObject)?["SaveFileLocation"]?.ToString(), from);

        var files = new List<CloneFile>();
        var dirs = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(from))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (AlwaysSkipped.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (BackupDirs.Contains(name, StringComparer.OrdinalIgnoreCase) && (options.NewWorld || !options.IncludeBackups)) continue;
                if (options.ShareMods && string.Equals(name, ModsDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, SavesDir, StringComparison.OrdinalIgnoreCase))
                {
                    if (!options.NewWorld) AddSaves(files, entry, from, sourceSave);
                    continue;
                }
                AddTree(files, entry, from);
                dirs.Add(name);
                dirs.AddRange(Directory.EnumerateDirectories(entry, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(from, d)));
            }
            else
            {
                // у клона один мир и один конфиг: serverconfig*.json (и их .bak) не копируем, выбранный запишем сами
                if (name.StartsWith("serverconfig", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(new CloneFile(entry, name, new FileInfo(entry).Length));
            }
        }

        // сохранение лежит вне папки данных — забираем его к себе, иначе два профиля делили бы один файл
        string? targetSave;
        if (options.NewWorld)
            targetSave = Path.Combine(to, SavesDir, "default" + SaveExtension);
        else if (sourceSave is null)
            targetSave = null;
        else if (IsSameOrInside(sourceSave, from))
            targetSave = Path.Combine(to, Path.GetRelativePath(from, sourceSave));
        else
        {
            targetSave = Path.Combine(to, SavesDir, Path.GetFileName(sourceSave));
            foreach (var part in SaveParts(sourceSave))
                files.Add(new CloneFile(part, Path.Combine(SavesDir, Path.GetFileName(part)), new FileInfo(part).Length));
        }

        return new ClonePlan { Source = source, Options = options, Files = files, Directories = dirs, ConfigPath = configPath, TargetSave = targetSave };
    }

    /// <summary>Скопировать по плану и вернуть новый профиль. При отмене или ошибке недокопированная папка удаляется.</summary>
    public static async Task<GameProfile> ApplyAsync(ClonePlan plan, IProgress<CloneProgress>? progress = null, CancellationToken ct = default)
    {
        var from = Path.GetFullPath(plan.Source.DataDir!).TrimEnd('\\', '/');
        var to = Path.GetFullPath(plan.Options.TargetDir).TrimEnd('\\', '/');
        var total = plan.TotalBytes;
        long done = 0;

        try
        {
            Directory.CreateDirectory(to);
            foreach (var dir in plan.Directories) Directory.CreateDirectory(Path.Combine(to, dir));
            var buffer = new byte[1 << 20];
            foreach (var file in plan.Files)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(to, file.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                progress?.Report(new CloneProgress(done, total, file.Relative));

                // ReadWrite: остановленный сервер файлы не держит, но антивирус или проводник — могут
                await using (var src = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, useAsync: true))
                await using (var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
                {
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        done += read;
                        progress?.Report(new CloneProgress(done, total, file.Relative));
                    }
                }
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file.Source));
            }

            if (plan.ConfigPath is not null) WriteConfig(plan, from, to);
        }
        catch
        {
            // папка была пустой или не существовала — внутри только то, что успели скопировать мы
            try { if (Directory.Exists(to)) Directory.Delete(to, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }

        return new GameProfile
        {
            Name = plan.Options.Name.Trim(),
            Kind = plan.Source.Kind,
            GameDir = plan.Source.GameDir,
            DataDir = to,
            PinnedMods = new(plan.Source.PinnedMods, StringComparer.OrdinalIgnoreCase),
            BlockedVersions = plan.Source.BlockedVersions.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>serverconfig.json клона: пути — на новую папку; для нового мира — свой файл сохранения, пустой сид, своё имя.</summary>
    private static void WriteConfig(ClonePlan plan, string from, string to)
    {
        var root = ModConfigEditor.Load(plan.ConfigPath!);

        if (root["ModPaths"] is JArray paths)
        {
            for (var i = 0; i < paths.Count; i++)
                if (paths[i].Type == JTokenType.String)
                    paths[i] = plan.Options.ShareMods ? RealPath(paths[i].ToString(), from) : RebasePath(paths[i].ToString(), from, to);

            // общие моды: папка модов исходного профиля должна быть в списке, даже если конфиг на неё не ссылался
            var shared = Path.Combine(from, ModsDir);
            if (plan.Options.ShareMods && Directory.Exists(shared)
                && !paths.Any(p => p.Type == JTokenType.String && Path.IsPathRooted(p.ToString()) && IsSameOrInside(p.ToString(), shared) && IsSameOrInside(shared, p.ToString())))
                paths.Add(shared);
        }

        if (root["WorldConfig"] is JObject world)
        {
            if (plan.TargetSave is not null) world["SaveFileLocation"] = plan.TargetSave;
            if (plan.Options.NewWorld)
            {
                world["Seed"] = ""; // пусто — сервер выберет случайный
                world["WorldName"] = string.IsNullOrWhiteSpace(plan.Options.WorldName) ? plan.Options.Name.Trim() : plan.Options.WorldName.Trim();
            }
        }

        ModConfigEditor.Save(Path.Combine(to, MainConfig), root);
    }

    /// <summary>
    /// Путь внутри исходной папки данных → тот же путь в новой. Относительные и посторонние пути не меняются.
    /// Понимает и путь, записанный под другим пользователем: «хвост» после имени папки данных.
    /// </summary>
    public static string RebasePath(string path, string sourceDir, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return path;
        var rel = RelativeToData(path, sourceDir);
        return rel is null ? path : rel.Length == 0 ? targetDir : Path.Combine(targetDir, rel);
    }

    /// <summary>
    /// Путь внутри исходной папки данных — таким, какой он на этой машине (записанный под другим пользователем
    /// превращается в настоящий). Относительные и посторонние пути не меняются.
    /// </summary>
    private static string RealPath(string path, string sourceDir)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return path;
        var rel = RelativeToData(path, sourceDir);
        return rel is null ? path : rel.Length == 0 ? sourceDir : Path.Combine(sourceDir, rel);
    }

    /// <summary>Путь относительно папки данных (null — путь не про неё).</summary>
    private static string? RelativeToData(string path, string sourceDir)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        var from = Path.GetFullPath(sourceDir).TrimEnd('\\', '/');
        if (IsSameOrInside(full, from)) return full.Length == from.Length ? "" : full[(from.Length + 1)..];

        // …\Administrator\AppData\Roaming\VintagestoryData\Saves\x — та же папка данных, другой пользователь
        // (существующий посторонний путь — например, общая папка модов — не трогаем)
        if (Directory.Exists(full) || File.Exists(full)) return null;
        var folder = Path.GetFileName(from);
        var parts = full.Split('\\', '/');
        var at = Array.FindLastIndex(parts, p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        return at < 0 ? null : string.Join(Path.DirectorySeparatorChar, parts[(at + 1)..]);
    }

    /// <summary>Сохранение из конфига — там, где оно реально лежит (с учётом «чужого» пользователя в пути).</summary>
    private static string? ResolveSave(string? saveFileLocation, string dataDir)
    {
        if (string.IsNullOrWhiteSpace(saveFileLocation)) return null;
        if (!Path.IsPathRooted(saveFileLocation)) return Path.GetFullPath(Path.Combine(dataDir, saveFileLocation));
        if (File.Exists(saveFileLocation)) return Path.GetFullPath(saveFileLocation);
        return RelativeToData(saveFileLocation, dataDir) is { Length: > 0 } rel
            ? Path.Combine(Path.GetFullPath(dataDir).TrimEnd('\\', '/'), rel)
            : Path.GetFullPath(saveFileLocation);
    }

    private static void AddTree(List<CloneFile> files, string dir, string root)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            files.Add(new CloneFile(f, Path.GetRelativePath(root, f), new FileInfo(f).Length));
    }

    /// <summary>
    /// Saves для «того же мира»: только сохранение выбранного конфига. Чужие миры (другие .vcdbs и папки с ними)
    /// остаются в исходной папке; всё прочее (данные модов рядом с сохранениями) копируется.
    /// Нет конфига — неизвестно, какой мир нужен: копируем всё.
    /// </summary>
    private static void AddSaves(List<CloneFile> files, string savesDir, string root, string? save)
    {
        if (save is null)
        {
            AddTree(files, savesDir, root);
            return;
        }

        var mine = SaveParts(save).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(savesDir))
            if (!IsSaveFile(f) || mine.Contains(Path.GetFullPath(f)))
                files.Add(new CloneFile(f, Path.GetRelativePath(root, f), new FileInfo(f).Length));

        foreach (var d in Directory.EnumerateDirectories(savesDir))
        {
            var hasWorlds = Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any(IsSaveFile);
            if (!hasWorlds) { AddTree(files, d, root); continue; }
            if (!IsSameOrInside(save, d)) continue; // папка другого мира
            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                if (!IsSaveFile(f) || mine.Contains(Path.GetFullPath(f)))
                    files.Add(new CloneFile(f, Path.GetRelativePath(root, f), new FileInfo(f).Length));
        }
    }

    /// <summary>Файл сохранения и его спутники SQLite (-shm, -wal), какие есть.</summary>
    private static IEnumerable<string> SaveParts(string save) =>
        new[] { save, save + "-shm", save + "-wal" }.Where(File.Exists).Select(Path.GetFullPath);

    private static bool IsSaveFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(SaveExtension, StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(SaveExtension + "-shm", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(SaveExtension + "-wal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrInside(string path, string dir)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\', '/');
        var d = Path.GetFullPath(dir).TrimEnd('\\', '/');
        return p.Equals(d, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
