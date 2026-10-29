using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace eViSTool.Core.Diagnostics;

/// <summary>
/// Вид ошибки: текст без того, что меняется от раза к разу (имена в кавычках, числа, хеши). Четыре ошибки «Shape '…'
/// was not found» про разные shape — один вид; ошибка с другим текстом — новый.
/// </summary>
public static partial class ModErrorKind
{
    /// <summary>Длиннее вид не нужен: различие видно в начале, а хвостом бывают длинные списки.</summary>
    private const int MaxLength = 200;

    public static string Of(string message)
    {
        var text = Quoted().Replace(message, m => m.Value[0] + "*" + m.Value[0]);
        text = Hex().Replace(text, "#");
        text = Digits().Replace(text, "#");
        text = Spaces().Replace(text, " ").Trim();
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }

    [GeneratedRegex("""'[^'\r\n]*'|"[^"\r\n]*"|«[^»\r\n]*»""")]
    private static partial Regex Quoted();

    // uid игрока, guid, адреса: шестнадцатеричное с цифрами, от 6 знаков
    [GeneratedRegex(@"\b(?=[0-9a-fA-F-]*\d)[0-9a-fA-F][0-9a-fA-F-]{5,}\b")]
    private static partial Regex Hex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// «Игнорировать» в окне «Ошибки модов»: какие виды ошибок игрок видел и решил не замечать — у каждого профиля свои
/// (data\mod-errors\&lt;профиль&gt;.ignored.json). Мод в игноре, пока все его ошибки за запуск — из запомненных видов;
/// появился новый вид — мод снова в счёте и в «!». Ошибки без признаков мода (код самой игры) — под ключом
/// <see cref="Game"/>.
/// </summary>
public sealed class ModErrorIgnores
{
    /// <summary>Ключ ошибок без признаков мода.</summary>
    public const string Game = "";

    /// <summary>modid (или <see cref="Game"/>) → запомненные виды ошибок.</summary>
    [JsonProperty]
    private Dictionary<string, HashSet<string>> Kinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Kinds.Count == 0;

    /// <summary>Все эти виды уже в игноре у ключа; пустой список — нечего сравнивать, не в игноре.</summary>
    public bool Has(string key, IReadOnlyList<string> kinds) =>
        kinds.Count > 0 && Kinds.TryGetValue(key, out var known) && kinds.All(known.Contains);

    public bool Has(ModErrorLine line) => Has(line.ModId, line.KindList);

    public bool HasGame(ModErrorReport report) => report.Unattributed > 0 && Has(Game, report.UnattributedKindList);

    public void Add(string key, IEnumerable<string> kinds)
    {
        if (!Kinds.TryGetValue(key, out var known)) Kinds[key] = known = new HashSet<string>(StringComparer.Ordinal);
        known.UnionWith(kinds);
    }

    public void Remove(string key) => Kinds.Remove(key);

    /// <summary>Сколько ошибок отчёта не в игноре — от этого «!» у профиля.</summary>
    public int Active(ModErrorReport report) =>
        report.Mods.Where(m => !Has(m)).Sum(m => m.Count) + (HasGame(report) ? 0 : report.Unattributed);

    /// <summary>Сколько модов отчёта не в игноре.</summary>
    public int ActiveMods(ModErrorReport report) => report.Mods.Count(m => !Has(m));

    public static string FileFor(string profileId) => Path.Combine(AppPaths.Root, "mod-errors", profileId + ".ignored.json");

    public void Save(string profileId)
    {
        var path = FileFor(profileId);
        if (IsEmpty)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }

    /// <summary>Игнор профиля; файла нет или он испорчен — пустой.</summary>
    public static ModErrorIgnores Load(string profileId)
    {
        try
        {
            var path = FileFor(profileId);
            if (File.Exists(path) && JsonConvert.DeserializeObject<ModErrorIgnores>(File.ReadAllText(path)) is { } loaded)
            {
                loaded.Kinds = new Dictionary<string, HashSet<string>>(loaded.Kinds, StringComparer.OrdinalIgnoreCase);
                return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new ModErrorIgnores();
    }
}
