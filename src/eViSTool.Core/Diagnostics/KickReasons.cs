using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Diagnostics;

/// <summary>
/// Причины отключения «сервер выгнал или забанил»: это не вылет, даже если в логе есть ошибки модов. Игра пишет причину
/// на языке игрока, поэтому шаблоны берутся из её lang-файлов: английский и язык из clientsettings.json профиля
/// (stringSettings.language).
/// Файлов нет или не читаются — встроенные английский и русский.
/// </summary>
public sealed class KickReasons
{
    /// <summary>Ключи переводов игры: выгнали (с причиной и без), забанили (в игре и при входе).</summary>
    private static readonly string[] Keys =
    [
        "You've been kicked by {0}",
        "You've been kicked by {0}, reason: {1}",
        "cmdban-youvebeenbanned",
        "banned-until-reason",
    ];

    private static readonly ConcurrentDictionary<string, KickReasons> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Regex> _patterns;

    private KickReasons(IEnumerable<string> templates) =>
        _patterns = [.. templates.Distinct().Select(ToRegex)];

    /// <summary>Английский и русский — тексты из lang игры 1.22.</summary>
    public static KickReasons BuiltIn { get; } = new(
    [
        "You've been kicked by {0}", "You've been kicked by {0}, reason: {1}", "You've been banned by {0}{1}",
        "You've been banned from this server by {0} until {1}.\nReason: {2}",
        "Вас выгнал {0}", "Вас выгнал {0}, причина: {1}", "Вы были забанены {0}{1}",
        "Вы были забанены на этом сервере на {0} до {1}.\nПричина: {2}",
    ]);

    /// <param name="gameDir">Папка игры профиля (там assets/game/lang).</param>
    /// <param name="dataDir">Папка данных профиля (clientsettings.json — выбранный язык игры).</param>
    public static KickReasons For(string? gameDir, string? dataDir)
    {
        var lang = Language(dataDir);
        if (string.IsNullOrEmpty(gameDir)) return BuiltIn;
        return Cache.GetOrAdd(gameDir + "|" + lang, _ =>
        {
            var dir = Path.Combine(gameDir, "assets", "game", "lang");
            var templates = new List<string>();
            foreach (var code in new[] { "en", lang }.Distinct())
                templates.AddRange(Read(Path.Combine(dir, code + ".json")));
            // в lang-файлах игры ничего не нашлось — хотя бы встроенные
            return templates.Count == 0 ? BuiltIn : new KickReasons(templates);
        });
    }

    public bool Matches(string reason) => _patterns.Any(p => p.IsMatch(reason));

    private static string Language(string? dataDir)
    {
        try
        {
            var file = dataDir is null ? null : Path.Combine(dataDir, "clientsettings.json");
            if (file is not null && File.Exists(file)
                && JObject.Parse(File.ReadAllText(file)) is var settings
                && (settings["stringSettings"]?["language"] ?? settings["language"])?.Value<string>() is { Length: > 0 and < 12 } code
                && code.All(c => char.IsLetterOrDigit(c) || c == '-'))
                return code;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return "en";
    }

    /// <summary>Нужные строки lang-файла. Файл читается потоком: он большой, бывают комментарии и повторы ключей.</summary>
    private static List<string> Read(string file)
    {
        var found = new List<string>();
        try
        {
            if (!File.Exists(file)) return found;
            using var reader = new JsonTextReader(new StreamReader(file));
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.PropertyName || !Keys.Contains((string?)reader.Value)) continue;
                if (reader.Read() && reader.TokenType == JsonToken.String && reader.Value is string { Length: > 0 } text) found.Add(text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return found;
    }

    /// <summary>«Вас выгнал {0}, причина: {1}» → ^Вас выгнал .*, причина: .*$</summary>
    private static Regex ToRegex(string template) =>
        new("^" + Regex.Replace(Regex.Escape(template), @"\\\{\d+}", ".*") + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
}
