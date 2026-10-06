using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Mods;

/// <summary>Содержимое modinfo.json (только то, что нам нужно).</summary>
public sealed record ModInfo
{
    /// <summary>modid в нижнем регистре — для сравнения с модбазой.</summary>
    public string ModId { get; init; } = "";

    /// <summary>modid как написан в modinfo.json — так его пишет игра в списках выключенных.</summary>
    public string OriginalModId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Version { get; init; }
    public string? Type { get; init; }
    public string? Side { get; init; }
    public string? Description { get; init; }
    public string? Website { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = [];

    /// <summary>modid → требуемая версия ("" или "*" — любая). Базовая игра ("game", "survival", "creative") тоже здесь.</summary>
    public IReadOnlyDictionary<string, string> Dependencies { get; init; } = new Dictionary<string, string>();

    private static readonly HashSet<string> BaseGameIds = new(StringComparer.OrdinalIgnoreCase) { "game", "survival", "creative" };

    public static bool IsBaseGame(string modId) => BaseGameIds.Contains(modId);

    /// <summary>
    /// Минимальная версия игры, которую мод требует в dependencies ("game", а также "survival"/"creative" — их версия
    /// совпадает с версией игры); самое строгое из требований. null — не указана ("" или "*").
    /// </summary>
    public Versioning.ModVersion? RequiredGame =>
        Dependencies.Where(d => IsBaseGame(d.Key))
                    .Select(d => Versioning.ModVersion.ParseOrNull(d.Value))
                    .Where(v => v is not null)
                    .Max();

    /// <summary>
    /// Мод требует игру новее, чем у профиля: игра его не загрузит («Unable to resolve some mod dependencies»),
    /// а сервер с ним не пустит игрока со старым клиентом. Возвращает требуемую версию, иначе null.
    /// Версия игры профиля неизвестна — тоже null: сравнить не с чем.
    /// </summary>
    public Versioning.ModVersion? NeedsNewerGame(Versioning.ModVersion? game) =>
        game is not null && RequiredGame is { } need && need.CompareTo(game) > 0 ? need : null;

    /// <summary>
    /// Разбор modinfo.json. Используем Newtonsoft, как сама игра: он прощает комментарии,
    /// ключи без кавычек и хвостовые запятые. Имена полей — без учёта регистра
    /// (авторы пишут и "modid", и "ModID", и "mod_id").
    /// </summary>
    public static ModInfo Parse(string json)
    {
        var o = JObject.Parse(json);

        string? Str(params string[] names)
        {
            foreach (var n in names)
            {
                var t = o.GetValue(n, StringComparison.OrdinalIgnoreCase);
                if (t is not null && t.Type != JTokenType.Null) return t.ToString();
            }
            return null;
        }

        var name = Str("name") ?? "";
        var modId = Str("modid", "mod_id");
        if (string.IsNullOrWhiteSpace(modId)) modId = ModIdFromName(name);

        var authors = o.GetValue("authors", StringComparison.OrdinalIgnoreCase) switch
        {
            JArray arr => arr.Where(t => t.Type != JTokenType.Null).Select(t => t.ToString()).ToList(),
            JValue v when v.Type == JTokenType.String => [v.ToString()],
            _ => new List<string>()
        };

        var deps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o.GetValue("dependencies", StringComparison.OrdinalIgnoreCase) is JObject depObj)
        {
            foreach (var p in depObj.Properties())
                deps[p.Name] = p.Value.Type == JTokenType.Null ? "" : p.Value.ToString();
        }

        return new ModInfo
        {
            ModId = modId.Trim().ToLowerInvariant(),
            OriginalModId = modId.Trim(),
            Name = name,
            Version = Str("version"),
            Type = Str("type"),
            Side = Str("side"),
            Description = Str("description"),
            Website = Str("website"),
            Authors = authors,
            Dependencies = deps,
        };
    }

    /// <summary>Так игра выводит modid, если автор его не указал: только буквы и цифры из имени, в нижнем регистре.</summary>
    public static string ModIdFromName(string name) =>
        new string(name.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
}
