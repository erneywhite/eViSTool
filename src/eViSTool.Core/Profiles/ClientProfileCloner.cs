using eViSTool.Core.Localization;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Profiles;

/// <summary>Откуда новый клиентский профиль берёт моды.</summary>
public enum ClientModsMode
{
    /// <summary>Из тех же папок, что и исходный профиль: набор один на двоих.</summary>
    Shared,
    /// <summary>Своя папка с копией модов исходного профиля.</summary>
    Copy,
    /// <summary>Своя пустая папка.</summary>
    Empty,
}

public sealed record ClientCloneOptions
{
    public required string Name { get; init; }

    /// <summary>Папка данных нового профиля: не существует или пустая.</summary>
    public required string TargetDir { get; init; }

    public ClientModsMode Mods { get; init; }

    /// <summary>
    /// true — настройки игры, настройки модов (ModConfig), макросы и прочие данные исходного профиля копируются;
    /// false — игра начнёт с настроек по умолчанию, как при первом запуске.
    /// </summary>
    public bool CopySettings { get; init; } = true;

    /// <summary>Копировать миры одиночной игры и карты.</summary>
    public bool CopyWorlds { get; init; }
}

public sealed record ClientClonePlan
{
    public required GameProfile Source { get; init; }
    public required ClientCloneOptions Options { get; init; }
    public required IReadOnlyList<CloneFile> Files { get; init; }

    /// <summary>Папки, которые надо создать (относительные пути), в том числе пустые.</summary>
    public IReadOnlyList<string> Directories { get; init; } = [];
    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>Файл настроек исходного профиля (null — игра с ним ещё не запускалась).</summary>
    public string? SettingsPath { get; init; }

    /// <summary>Как файл настроек называется у этой сборки игры (clientsettings.json или с префиксом).</summary>
    public required string SettingsName { get; init; }

    /// <summary>Папки модов нового профиля — пойдут в его modPaths.</summary>
    public required IReadOnlyList<string> ModDirs { get; init; }

    /// <summary>Моды общие с исходным профилем (ModDirs — его папки), а не своя папка Mods.</summary>
    public bool SharedMods { get; init; }

    /// <summary>Своя копия модов: из каких папок исходного профиля они соберутся.</summary>
    public IReadOnlyList<string> ModSources { get; init; } = [];

    /// <summary>Что при сборке модов пошло не один к одному: дубли, переименования, отсутствующие папки.</summary>
    public IReadOnlyList<string> ModNotes { get; init; } = [];
}

/// <summary>
/// Новый клиентский профиль на основе существующего: своя папка данных (<see cref="ClientProfileLayout"/>), игра
/// запускается с ней через <c>--dataPath</c>. Моды — общие с исходным профилем, своя копия или с нуля; настройки игры —
/// копия или чистые; миры — по желанию. Резервные копии, журналы и кэш не копируются никогда.
/// </summary>
public static class ClientProfileCloner
{
    public const string DefaultSettingsName = "clientsettings.json";
    private const string SinglePlayerConfig = "serverconfig.json";
    private const string ModsDir = "Mods";

    // журналы и кэш игра создаст заново, бэкапы — про прежние миры, ModsByServer — моды, скачанные с серверов (скачаются снова),
    // а ServerProfiles и ClientProfiles — папки других профилей
    private static readonly string[] AlwaysSkipped =
        ["Logs", "Cache", "Backups", "BackupSaves", "ModsByServer", ServerProfileLayout.ContainerName, ClientProfileLayout.ContainerName];

    // миры одиночной игры и всё, что к ним привязано
    private static readonly string[] WorldDirs = ["Saves", "Maps", "Playerdata", "WorldEdit"];

    /// <summary>Папки с мирами в папке данных, какие есть (для подсчёта размера).</summary>
    public static IEnumerable<string> WorldFolders(string dataDir) =>
        WorldDirs.Select(d => Path.Combine(dataDir, d)).Where(Directory.Exists);

    /// <summary>Что будет скопировано. Бросает InvalidOperationException с понятным текстом, если создать профиль нельзя.</summary>
    public static ClientClonePlan Plan(GameProfile source, ClientCloneOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Name)) throw new InvalidOperationException(Loc.T("clone.errName"));
        if (string.IsNullOrWhiteSpace(options.TargetDir)) throw new InvalidOperationException(Loc.T("clone.errNoTarget"));

        var to = Full(options.TargetDir);
        // исходной папки может и не быть (игра ещё не запускалась) — тогда копировать нечего, профиль просто пустой
        var from = !string.IsNullOrWhiteSpace(source.DataDir) && Directory.Exists(source.DataDir) ? Full(source.DataDir) : null;
        // внутрь исходной нельзя — кроме её контейнера ClientProfiles: он при копировании пропускается
        if (from is not null && (ProfileCloner.IsSameOrInside(from, to)
                                 || (ProfileCloner.IsSameOrInside(to, from) && !ClientProfileLayout.IsInsideContainerOf(to, from))))
            throw new InvalidOperationException(Loc.T("clone.errNested"));
        if (File.Exists(to) || (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()))
            throw new InvalidOperationException(Loc.T("clone.errNotEmpty", to));

        var resolved = ProfileResolver.Resolve(source);
        var files = new List<CloneFile>();
        var dirs = new List<string>();

        if (from is not null)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (AlwaysSkipped.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, ModsDir, StringComparison.OrdinalIgnoreCase)) continue; // моды — отдельно, ниже
                    var wanted = WorldDirs.Contains(name, StringComparer.OrdinalIgnoreCase) ? options.CopyWorlds : options.CopySettings;
                    if (!wanted) continue;
                    AddTree(files, dirs, entry, Path.GetRelativePath(from, entry));
                }
                else if (options.CopySettings && IsSettingsFile(name)
                         // файл настроек игры и конфиг одиночной игры пишем сами — с папками модов нового профиля
                         && !SamePath(entry, resolved.ConfigPath) && !string.Equals(name, SinglePlayerConfig, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(new CloneFile(entry, name, new FileInfo(entry).Length));
                }
            }
        }

        // общие моды — только если у исходного профиля есть откуда их брать
        var shared = options.Mods == ClientModsMode.Shared && resolved.ModDirs.Count > 0;
        CloneModsPlan? mods = null;
        if (!shared)
        {
            dirs.Add(ModsDir);
            if (options.Mods == ClientModsMode.Copy)
            {
                // моды исходного профиля могут лежать в нескольких папках (и не в его папке данных) — собираем в одну,
                // совпадения имён не теряются (см. CloneMods)
                mods = CloneMods.Collect(resolved.ModDirs, ModsDir);
                files.AddRange(mods.Files);
                dirs.AddRange(mods.Directories);
            }
        }

        return new ClientClonePlan
        {
            Source = source,
            Options = options,
            Files = files,
            Directories = dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SettingsPath = resolved.ConfigPath,
            SettingsName = resolved.ConfigPath is { } path ? Path.GetFileName(path) : DefaultSettingsName,
            ModDirs = shared ? resolved.ModDirs : [Path.Combine(to, ModsDir)],
            ModSources = mods?.Sources ?? [],
            ModNotes = mods?.Notes ?? [],
            SharedMods = shared,
        };
    }

    /// <summary>Скопировать по плану и вернуть новый профиль. При отмене или ошибке в папке назначения ничего не трогается.</summary>
    public static async Task<GameProfile> ApplyAsync(ClientClonePlan plan, IProgress<CloneProgress>? progress = null, CancellationToken ct = default)
    {
        var to = Full(plan.Options.TargetDir);
        // во временной папке рядом, на место — переименованием; сбой убирает только её (см. ProfileCloner.StageAsync)
        await ProfileCloner.StageAsync(to, async stage =>
        {
            await ProfileCloner.CopyAsync(plan.Files, plan.Directories, stage, progress, ct).ConfigureAwait(false);
            WriteSettings(plan, to, stage);
        }).ConfigureAwait(false);

        return new GameProfile
        {
            Name = plan.Options.Name.Trim(),
            Kind = ProfileKind.Client,
            GameDir = plan.Source.GameDir,
            DataDir = to,
            PinnedMods = new(plan.Source.PinnedMods, StringComparer.OrdinalIgnoreCase),
            BlockedVersions = plan.Source.BlockedVersions.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Настройки нового профиля. Игра ищет моды только в папках из modPaths, поэтому список пишем сами: встроенная
    /// папка игры («Mods») остаётся, дальше — папки модов нового профиля.
    /// </summary>
    private static void WriteSettings(ClientClonePlan plan, string to, string writeTo)
    {
        var settings = Path.Combine(writeTo, plan.SettingsName); // пути внутри — на итоговую папку to
        if (plan.Options.CopySettings && plan.SettingsPath is not null)
        {
            var root = ModConfigEditor.Load(plan.SettingsPath);
            if (root["stringListSettings"] is not JObject lists) root["stringListSettings"] = lists = new JObject();
            lists["modPaths"] = ModPaths(lists["modPaths"], plan);
            ModConfigEditor.Save(settings, root);
        }
        else if (plan.SharedMods)
        {
            // чистые настройки, но моды общие: игре хватает файла с одним списком папок — остальное она заполнит
            // значениями по умолчанию при первом запуске (проверено на 1.22.7). Свою папку Mods она найдёт и без файла.
            ModConfigEditor.Save(settings, new JObject { ["stringListSettings"] = new JObject { ["modPaths"] = ModPaths(null, plan) } });
        }

        // конфиг одиночной игры: те же папки модов, путь к миру — в новую папку
        var from = string.IsNullOrWhiteSpace(plan.Source.DataDir) ? null : Full(plan.Source.DataDir);
        if (!plan.Options.CopySettings || from is null || !File.Exists(Path.Combine(from, SinglePlayerConfig))) return;
        var single = ModConfigEditor.Load(Path.Combine(from, SinglePlayerConfig));
        if (single["ModPaths"] is JArray) single["ModPaths"] = ModPaths(single["ModPaths"], plan);
        if (single["WorldConfig"] is JObject world && world["SaveFileLocation"] is JValue { Type: JTokenType.String } save)
            world["SaveFileLocation"] = ProfileCloner.RebasePath(save.ToString(), from, to);
        ModConfigEditor.Save(Path.Combine(writeTo, SinglePlayerConfig), single);
    }

    /// <summary>Относительные записи (встроенная папка игры) — как были, вместо остальных — папки модов нового профиля.</summary>
    private static JArray ModPaths(JToken? current, ClientClonePlan plan)
    {
        var relative = (current as JArray)?.Where(t => t.Type == JTokenType.String).Select(t => t.ToString())
            .Where(p => p.Length > 0 && !Path.IsPathRooted(Environment.ExpandEnvironmentVariables(p))).ToList() ?? [];
        if (relative.Count == 0) relative.Add(ModsDir);
        return new JArray(relative.Concat(plan.ModDirs).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Файлы настроек в корне папки данных: json игры и модов. Архивы, «.bkp» и прочее постороннее не берём.</summary>
    private static bool IsSettingsFile(string name) => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static void AddTree(List<CloneFile> files, List<string> dirs, string dir, string relative)
    {
        dirs.Add(relative);
        dirs.AddRange(Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).Select(d => Path.Combine(relative, Path.GetRelativePath(dir, d))));
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            files.Add(new CloneFile(f, Path.Combine(relative, Path.GetRelativePath(dir, f)), new FileInfo(f).Length));
    }

    private static bool SamePath(string a, string? b) => b is not null && string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    private static string Full(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}
