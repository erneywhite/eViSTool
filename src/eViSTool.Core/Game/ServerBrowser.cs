using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using eViSTool.Core.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Game;

/// <summary>Мод на сервере из общего списка: id и версия.</summary>
public sealed record PublicServerMod(string Id, string Version);

/// <summary>Сервер из общего списка серверов игры (того же, что показывает браузер серверов в самой игре).</summary>
public sealed record PublicServer(string Name, string Address, string? Playstyle, IReadOnlyList<PublicServerMod> Mods, int Players,
    int MaxPlayers, string GameVersion, bool HasPassword, bool Whitelisted, string Description)
{
    public bool HasSlots => MaxPlayers <= 0 || Players < MaxPlayers;
}

/// <summary>
/// Общий список серверов. Адрес — тот же, что у игры профиля (stringSettings.masterserverUrl в её clientsettings.json):
/// окно показывает то же, что браузер серверов в игре. Нет адреса — официальный.
/// </summary>
public static partial class ServerBrowser
{
    public const string OfficialMasterUrl = "https://masterserver.vintagestory.at/api/v1/servers/";

    public static string MasterUrl(string? dataDir)
    {
        try
        {
            var file = Path.Combine(string.IsNullOrWhiteSpace(dataDir) ? GameInstall.DefaultDataDir : dataDir, "clientsettings.json");
            if (File.Exists(file) && JObject.Parse(File.ReadAllText(file))["stringSettings"]?["masterserverUrl"]?.Value<string>() is { Length: > 0 } url
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                return url;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return OfficialMasterUrl;
    }

    /// <summary>Загрузить список. Ошибка — <see cref="InvalidOperationException"/> с понятной причиной.</summary>
    public static async Task<IReadOnlyList<PublicServer>> FetchAsync(HttpClient http, string masterUrl, CancellationToken ct = default)
    {
        var url = masterUrl.TrimEnd('/') + "/list";
        string text;
        try { text = await http.GetStringAsync(url, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(Loc.T("browse.fetchFailed", new Uri(url).Host));
        }
        try { return Parse(text); }
        catch (JsonException)
        {
            throw new InvalidOperationException(Loc.T("browse.badList", new Uri(url).Host));
        }
    }

    public static IReadOnlyList<PublicServer> Parse(string json)
    {
        var root = JToken.Parse(json);
        var data = (root as JObject)?["data"] as JArray ?? root as JArray ?? throw new JsonException("no data");
        var list = new List<PublicServer>(data.Count);
        foreach (var s in data.OfType<JObject>())
        {
            var address = s.Value<string>("serverIP");
            if (string.IsNullOrWhiteSpace(address)) continue;
            var mods = (s["mods"] as JArray ?? []).OfType<JObject>()
                .Select(m => new PublicServerMod(m.Value<string>("id") ?? "", m.Value<string>("version") ?? ""))
                .Where(m => m.Id.Length > 0).ToList();
            list.Add(new PublicServer(
                Plain(s.Value<string>("serverName") ?? address).Trim(), address.Trim(),
                s["playstyle"]?.Type == JTokenType.Object ? s["playstyle"]!.Value<string>("langCode") : null,
                mods, Int(s["players"]), Int(s["maxPlayers"]), s.Value<string>("gameVersion") ?? "",
                s.Value<bool?>("hasPassword") == true, s.Value<bool?>("whitelisted") == true,
                Plain(s.Value<string>("gameDescription") ?? "").Trim()));
        }
        return list;
    }

    // число бывает и строкой: maxPlayers мастер-сервер отдаёт как "16"
    private static int Int(JToken? t) => t?.Type switch
    {
        JTokenType.Integer => t.Value<int>(),
        JTokenType.String when int.TryParse(t.Value<string>(), out var n) => n,
        _ => 0,
    };

    /// <summary>Описания бывают с разметкой (&lt;a&gt;, &lt;font&gt;, &lt;br&gt;) — показываем текстом.</summary>
    public static string Plain(string s)
    {
        var text = WebUtility.HtmlDecode(Tags().Replace(Breaks().Replace(s, "\n"), "")).Replace("\r", "");
        // строки без хвостовых пробелов, подряд не больше одной пустой
        text = string.Join("\n", text.Split('\n').Select(l => l.TrimEnd()));
        return BlankLines().Replace(text, "\n\n");
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<[^>]{1,200}>")]
    private static partial Regex Tags();
}

/// <summary>
/// Запись в избранное игры (clientsettings.json → stringListSettings.multiplayerservers, строки «имя,адрес,пароль»).
/// Только при закрытой игре: открытая перезапишет файл при выходе — это проверяет вызывающий.
/// </summary>
public static class GameFavorites
{
    private static string FileOf(string? dataDir) =>
        Path.Combine(string.IsNullOrWhiteSpace(dataDir) ? GameInstall.DefaultDataDir : dataDir, "clientsettings.json");

    /// <summary>Добавить (такой адрес уже есть — заменить: новое имя и пароль). Запятые в имени игра не поймёт — заменяем.</summary>
    public static void Add(string? dataDir, string name, string address, string? password) =>
        Edit(dataDir, list =>
        {
            Remove(list, address);
            list.Add($"{name.Replace(',', ' ').Trim()},{address.Trim()},{password ?? ""}");
        });

    public static void RemoveAddress(string? dataDir, string address) => Edit(dataDir, list => Remove(list, address));

    /// <summary>Изменить запись (по прежнему адресу) — на том же месте в списке; прежней нет — добавить в конец.</summary>
    public static void Update(string? dataDir, string oldAddress, string name, string address, string? password) =>
        Edit(dataDir, list =>
        {
            var line = $"{name.Replace(',', ' ').Trim()},{address.Trim()},{password ?? ""}";
            // новый адрес мог уже быть в списке отдельной записью — она теперь лишняя
            if (!string.Equals(oldAddress.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase)) Remove(list, address);
            var index = list.ToList().FindIndex(t => t.Type == JTokenType.String && PlayTargets.ParseFavorite(t.Value<string>()!) is { } f
                                                 && string.Equals(f.Address, oldAddress.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index >= 0) list[index] = line;
            else list.Add(line);
        });

    /// <summary>Адрес похож на «хост» или «хост:порт» (без пробелов и запятых: запятая разделяет поля записи).</summary>
    public static bool IsValidAddress(string address)
    {
        address = address.Trim();
        return address.Length is > 0 and <= 255 && !address.Any(c => char.IsWhiteSpace(c) || c == ',');
    }

    private static void Remove(JArray list, string address)
    {
        foreach (var t in list.Where(t => t.Type == JTokenType.String && PlayTargets.ParseFavorite(t.Value<string>()!) is { } f
                                          && string.Equals(f.Address, address.Trim(), StringComparison.OrdinalIgnoreCase)).ToList())
            t.Remove();
    }

    private static void Edit(string? dataDir, Action<JArray> change)
    {
        var file = FileOf(dataDir);
        if (!File.Exists(file)) throw new InvalidOperationException(Loc.T("browse.noSettings"));
        var root = JObject.Parse(File.ReadAllText(file));
        var lists = root["stringListSettings"] as JObject ?? (JObject)(root["stringListSettings"] = new JObject());
        var list = lists["multiplayerservers"] as JArray ?? (JArray)(lists["multiplayerservers"] = new JArray());
        change(list);
        var tmp = file + ".evistool.tmp";
        File.WriteAllText(tmp, root.ToString(Formatting.Indented));
        File.Move(tmp, file, overwrite: true);
    }
}
