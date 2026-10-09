using eViSTool.Core.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Server;

/// <summary>
/// Настройки агента без окна (Linux): data/agent.json рядом с папкой agents. Пишет его команда «setup», читает агент при
/// запуске; файл для людей — с отступами и понятными именами, его можно править руками. С окном (Windows, --exe и --data)
/// файл не читается: пути даёт окно. Каждое значение берётся из ключа командной строки, без ключа — отсюда, а папки
/// сервера, которых нет и здесь, ищет <see cref="ServerLocator"/>.
/// </summary>
public sealed record AgentConfig
{
    public const string FileName = "agent.json";

    /// <summary>Профиль агента без окна: окно даёт свой id, а здесь он один на папку eViSTool.</summary>
    public const string DefaultProfile = "server";

    /// <summary>Папка игры сервера (в ней VintagestoryServer.dll).</summary>
    public string? GameDir { get; init; }

    /// <summary>Папка данных сервера (--dataPath): мир, моды, serverconfig.json.</summary>
    public string? DataDir { get; init; }

    /// <summary>Профиль: ключ, расписание, удалённый доступ — в agents/&lt;профиль&gt;.*. Пусто — «server».</summary>
    public string? Profile { get; init; }

    /// <summary>Запускать сервер вместе с агентом: агент без окна для того и нужен.</summary>
    public bool StartServer { get; init; } = true;

    /// <summary>Имя сервера для резервных копий и оповещений. Пусто — ServerName из serverconfig.json.</summary>
    public string? ServerName { get; init; }

    /// <summary>Дополнительные аргументы сервера (после --dataPath), по одному в строке списка.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public IReadOnlyList<string> ServerArgs { get; init; } = [];

    /// <summary>Язык сообщений агента и объявлений игрокам («ru», «en»). Пусто — язык системы.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// Где лежит agent.json: рядом с папкой agents (при --agents-dir — рядом с ней, так тесты и копии не мешают друг
    /// другу), без неё — в папке данных eViSTool. Обращение к <see cref="AppPaths.Root"/> создаёт эту папку, поэтому
    /// для чтения есть <see cref="Find"/>.
    /// </summary>
    public static string FileFor(string? agentsDir) => agentsDir is null
        ? Path.Combine(AppPaths.Root, FileName)
        : Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(agentsDir)))!, FileName);

    /// <summary>
    /// Существующий agent.json; null — его нет. Ничего не создаёт: команду могли запустить от root, и папка data,
    /// созданная от его имени, осталась бы недоступной службе.
    /// </summary>
    public static string? Find(string? agentsDir)
    {
        if (agentsDir is not null) return File.Exists(FileFor(agentsDir)) ? FileFor(agentsDir) : null;
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "data"), AppPaths.FallbackRoot })
            if (File.Exists(Path.Combine(dir, FileName))) return Path.Combine(dir, FileName);
        return null;
    }

    /// <summary>
    /// Прочитать; null — файла нет, пустой файл — всё по умолчанию. Ошибка в файле — <see cref="InvalidDataException"/>
    /// с понятным текстом: молча взять другие папки хуже, чем сказать, что файл испорчен. Пустые строки — «не задано»,
    /// относительные пути — от папки, где лежит файл.
    /// </summary>
    public static AgentConfig? Load(string file)
    {
        if (!File.Exists(file)) return null;
        AgentConfig config;
        try
        {
            config = JsonConvert.DeserializeObject<AgentConfig>(File.ReadAllText(file)) ?? new();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Loc.T("agent.configBroken", file, ex.Message), ex);
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        string? PathOf(string? value) => NonEmpty(value) is not { } v ? null
            // «~/…», как в оболочке, — от домашней папки: так пишут руками
            : v.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), v[2..])
            : Path.GetFullPath(v, dir);
        var profile = NonEmpty(config.Profile);
        if (profile is not null && (profile.Any(PathRules.IsBadInFileName) || profile is "." or ".."))
            throw new InvalidDataException(Loc.T("agent.configBroken", file, Loc.T("agent.configProfile", profile)));
        return config with
        {
            GameDir = PathOf(config.GameDir),
            DataDir = PathOf(config.DataDir),
            Profile = profile,
            ServerName = NonEmpty(config.ServerName),
            ServerArgs = [.. (config.ServerArgs ?? []).Where(a => a is not null)],
            Language = NonEmpty(config.Language),
        };
    }

    /// <summary>
    /// Записать (команда setup): заданные значения — поверх того, что в файле; всё остальное остаётся как было, со своим
    /// порядком и с полями, которых eViSTool не знает. Поля, которых в файле нет, добавляются со значениями по умолчанию —
    /// чтобы было видно, что можно поправить. Испорченный файл не перезаписывается: исключение, как у <see cref="Load"/>.
    /// </summary>
    public static void Save(string file, AgentConfigUpdate update)
    {
        JObject doc;
        try
        {
            var text = File.Exists(file) ? File.ReadAllText(file) : "";
            doc = string.IsNullOrWhiteSpace(text) ? [] : JObject.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Loc.T("agent.configBroken", file, ex.Message), ex);
        }

        Put(doc, nameof(GameDir), update.GameDir, "");
        Put(doc, nameof(DataDir), update.DataDir, "");
        Put(doc, nameof(Profile), update.Profile, DefaultProfile);
        Put(doc, nameof(StartServer), update.StartServer is { } start ? new JValue(start) : null, new JValue(true));
        Put(doc, nameof(ServerName), update.ServerName, "");
        Put(doc, nameof(ServerArgs), update.ServerArgs is { } args ? new JArray(args) : null, new JArray());
        Put(doc, nameof(Language), null, "");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, doc.ToString(Formatting.Indented) + "\n");
        File.Move(tmp, file, overwrite: true);
    }

    private static void Put(JObject doc, string name, string? value, string fallback) =>
        Put(doc, name, value is null ? null : new JValue(value), new JValue(fallback));

    /// <summary>
    /// value — записать поверх (null — оставить как в файле), fallback — если поля в файле нет. Имя поля — без учёта
    /// регистра: «gameDir», написанное руками, заменяется, а не дублируется.
    /// </summary>
    private static void Put(JObject doc, string name, JToken? value, JToken fallback)
    {
        var existing = doc.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) doc.Add(name, value ?? fallback);
        else if (value is not null) existing.Value = value;
    }

    /// <summary>
    /// Имя для резервных копий агента без окна («&lt;имя&gt;-&lt;время&gt;.vcdbs»): ServerName отсюда, иначе ServerName из
    /// serverconfig.json сервера, иначе «world». Знаки, которых не бывает в именах файлов, — «_» (<see cref="BackupStore.Slug"/>).
    /// </summary>
    public static string BackupNameFor(AgentConfig? config, string dataDir) => BackupStore.Slug(DisplayNameFor(config, dataDir));

    /// <summary>Имя сервера для людей (заголовки оповещений): отсюда, иначе из serverconfig.json; null — нигде нет.</summary>
    public static string? DisplayNameFor(AgentConfig? config, string dataDir) =>
        NonEmpty(config?.ServerName) ?? ServerNameIn(dataDir);

    /// <summary>ServerName из serverconfig.json; null — файла или поля нет, или файл не прочитать.</summary>
    public static string? ServerNameIn(string dataDir)
    {
        try
        {
            var file = Path.Combine(dataDir, "serverconfig.json");
            return File.Exists(file) && JObject.Parse(File.ReadAllText(file))["ServerName"] is { Type: JTokenType.String } name
                ? NonEmpty(name.Value<string>())
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Значение ключа, если задан, иначе из файла (пустая строка — «не задано»).</summary>
    public static string? Pick(string? key, string? fromFile) => NonEmpty(key) ?? NonEmpty(fromFile);

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Что записать в agent.json (<see cref="AgentConfig.Save"/>); null — оставить как в файле.</summary>
public sealed record AgentConfigUpdate(
    string? GameDir = null,
    string? DataDir = null,
    string? Profile = null,
    bool? StartServer = null,
    string? ServerName = null,
    IReadOnlyList<string>? ServerArgs = null);
