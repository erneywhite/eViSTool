using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Server;

/// <summary>
/// Подробность записи журнала: что именно поменялось. Kind — код (подпись в окне — на его языке), Args — значения:
/// <list type="bullet">
/// <item><c>field</c> [путь, было, стало], <c>fieldAdded</c> [путь, стало], <c>fieldRemoved</c> [путь, было],
/// <c>secret</c> [путь] — значение скрыто (пароль, токен, адрес вебхука);</item>
/// <item><c>playerRole</c> [игрок, было, стало], <c>playerWhitelist</c> [игрок, on|off], <c>playerUnban</c> [игрок],
/// <c>whitelistMode</c> [было, стало] (Default|Off|On);</item>
/// <item><c>modVersion</c> [мод, версия, прежняя версия или ""].</item>
/// </list>
/// </summary>
public sealed record JournalDetail(string Kind, IReadOnlyList<string> Args);

/// <summary>Подробности для журнала: что поменялось в JSON-файле настроек и в списках игроков.</summary>
public static partial class JournalDetails
{
    /// <summary>Больше подробностей в одну запись не кладём: журнал — не копия файла.</summary>
    public const int Max = 100;

    private const int MaxValue = 60;

    /// <summary>
    /// Что поменялось между двумя JSON (ключи с путём вида «Roles[2].Code»). Значения паролей, токенов и адресов
    /// вебхуков не пишутся — только что они изменены. Не JSON или ничего не поменялось — пусто.
    /// </summary>
    public static IReadOnlyList<JournalDetail> Json(string? before, string? after)
    {
        if (Parse(before) is not { } b || Parse(after) is not { } a) return [];
        var was = new Dictionary<string, string>(StringComparer.Ordinal);
        var now = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        Flatten(b, "", was, order);
        Flatten(a, "", now, order);

        var result = new List<JournalDetail>();
        foreach (var path in order.Distinct())
        {
            var inWas = was.TryGetValue(path, out var old);
            var inNow = now.TryGetValue(path, out var value);
            if (inWas && inNow && old == value) continue;
            if (IsSecret(path)) result.Add(new("secret", [path]));
            else if (inWas && inNow) result.Add(new("field", [path, Short(old!), Short(value!)]));
            else if (inNow) result.Add(new("fieldAdded", [path, Short(value!)]));
            else result.Add(new("fieldRemoved", [path, Short(old!)]));
            if (result.Count >= Max) break;
        }
        return result;
    }

    /// <summary>Правка игроков у остановленного сервера: что изменится относительно списков до неё.</summary>
    public static IReadOnlyList<JournalDetail> Players(ServerPlayersView before, PlayerFileEdit edit)
    {
        var player = before.Players.FirstOrDefault(p => p.Uid == edit.Uid);
        var name = player?.Name
                   ?? before.Whitelist.FirstOrDefault(e => e.Uid == edit.Uid)?.Name
                   ?? before.Bans.FirstOrDefault(e => e.Uid == edit.Uid)?.Name
                   ?? edit.Uid;
        var result = new List<JournalDetail>();
        if (edit.Role is { } role)
        {
            var old = player?.Role ?? before.DefaultRole ?? "";
            if (old != role) result.Add(new("playerRole", [name, old, role]));
        }
        if (edit.Whitelisted is { } on)
        {
            var was = player?.Whitelisted ?? before.Whitelist.Any(e => e.Uid == edit.Uid);
            if (was != on) result.Add(new("playerWhitelist", [name, on ? "on" : "off"]));
        }
        if (edit.Unban && (player?.Ban is not null || before.Bans.Any(e => e.Uid == edit.Uid)))
            result.Add(new("playerUnban", [name]));
        if (edit.Mode is { } mode && mode != before.WhitelistMode)
            result.Add(new("whitelistMode", [before.WhitelistMode.ToString(), mode.ToString()]));
        return result;
    }

    private static JToken? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JToken.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static void Flatten(JToken token, string path, Dictionary<string, string> into, List<string> order)
    {
        switch (token)
        {
            case JObject obj when obj.HasValues:
                foreach (var p in obj.Properties()) Flatten(p.Value, path.Length == 0 ? p.Name : $"{path}.{p.Name}", into, order);
                return;
            case JArray arr when arr.HasValues:
                for (var i = 0; i < arr.Count; i++) Flatten(arr[i], $"{path}[{i}]", into, order);
                return;
        }
        var text = token switch
        {
            JObject => "{}",
            JArray => "[]",
            JValue { Type: JTokenType.String } s => s.Value<string>() ?? "",
            JValue { Type: JTokenType.Null } => "null",
            _ => token.ToString(Formatting.None),
        };
        if (path.Length == 0) path = "$";
        into[path] = text;
        order.Add(path);
    }

    /// <summary>Последнее имя в пути похоже на секрет: пароль, токен, ключ, адрес вебхука.</summary>
    private static bool IsSecret(string path)
    {
        var name = path.Split('.')[^1];
        var bracket = name.IndexOf('[');
        if (bracket >= 0) name = name[..bracket];
        return SecretName().IsMatch(name);
    }

    private static string Short(string value) =>
        value.Length <= MaxValue ? value : value[..(MaxValue - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"pass|token|secret|webhook|apikey|api_key|^key$|url$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();
}
