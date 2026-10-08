using System.Globalization;
using eViSTool.Core.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Server;

/// <summary>Запись белого списка или бана: игрок (UID и имя), кто выдал, за что, до какого времени.</summary>
public sealed record PlayerListEntry(string Uid, string? Name, string? IssuedBy, string? Reason, DateTime? Until)
{
    /// <summary>«Навсегда»: сервер пишет срок на 50 лет вперёд.</summary>
    public bool IsPermanent => Until is { } u && u > DateTime.Now.AddYears(20);
    public bool IsActive(DateTime now) => Until is not { } u || u > now;
}

/// <summary>Игрок, который хоть раз заходил: роль, даты, в белом списке ли, забанен ли (действующий бан).</summary>
public sealed record ServerPlayer(string Uid, string Name, string? Role, DateTime? FirstJoin, DateTime? LastJoin,
    bool Whitelisted, PlayerListEntry? Ban);

/// <summary>Роль сервера: код и имя (из serverconfig.json).</summary>
public sealed record ServerRole(string Code, string Name);

/// <summary>Режим белого списка (WhitelistMode в serverconfig.json): 0 — как по умолчанию у игры, 1 — выключен, 2 — включён.</summary>
public enum WhitelistMode { Default = 0, Off = 1, On = 2 }

/// <summary>Игроки сервера и его списки — всё, что нужно вкладке «Игроки».</summary>
public sealed record ServerPlayersView(IReadOnlyList<ServerPlayer> Players, IReadOnlyList<PlayerListEntry> Whitelist,
    IReadOnlyList<PlayerListEntry> Bans, IReadOnlyList<ServerRole> Roles, string? DefaultRole, WhitelistMode WhitelistMode);

/// <summary>Правка у остановленного сервера (файлы): что поменять. Новых игроков так не добавить — нужен UID, его знает только сервер.</summary>
public sealed record PlayerFileEdit(string Uid, string? Role = null, bool? Whitelisted = null, bool Unban = false, WhitelistMode? Mode = null);

/// <summary>
/// Игроки сервера по файлам его папки данных: Playerdata/playerdata.json (роль, даты), playerswhitelisted.json,
/// playersbanned.json и serverconfig.json (роли, режим белого списка). Работающий сервер держит эти списки в памяти
/// и сам перезаписывает файлы — тогда менять их нужно его командами (<see cref="PlayerCommands"/>); файлы правятся
/// только у остановленного (<see cref="Apply"/>). Незнакомые поля записей сохраняются как есть.
/// </summary>
public sealed class ServerPlayers(string dataDir)
{
    private string PlayerData => Path.Combine(dataDir, "Playerdata", "playerdata.json");
    private string WhitelistFile => Path.Combine(dataDir, "Playerdata", "playerswhitelisted.json");
    private string BansFile => Path.Combine(dataDir, "Playerdata", "playersbanned.json");
    private string ConfigFile => Path.Combine(dataDir, "serverconfig.json");

    /// <summary>Отметка изменения: самое позднее время файлов игроков и serverconfig.json (режим белого списка, роли).</summary>
    public DateTime? ChangedAt()
    {
        DateTime? latest = null;
        foreach (var f in new[] { PlayerData, WhitelistFile, BansFile, ConfigFile }.Where(File.Exists))
        {
            var t = File.GetLastWriteTimeUtc(f);
            if (latest is null || t > latest) latest = t;
        }
        return latest;
    }

    public ServerPlayersView Read(DateTime? nowLocal = null)
    {
        var now = nowLocal ?? DateTime.Now;
        var whitelist = ReadList(WhitelistFile);
        var bans = ReadList(BansFile);
        var config = ReadObject(ConfigFile);
        var roles = (config?["Roles"] as JArray ?? [])
            .OfType<JObject>()
            .Select(r => new ServerRole(r.Value<string>("Code") ?? "", r.Value<string>("Name") ?? r.Value<string>("Code") ?? ""))
            .Where(r => r.Code.Length > 0)
            .ToList();
        var mode = config?["WhitelistMode"]?.Type == JTokenType.Integer && Enum.IsDefined(typeof(WhitelistMode), config.Value<int>("WhitelistMode"))
            ? (WhitelistMode)config.Value<int>("WhitelistMode")
            : WhitelistMode.Default;

        var players = (ReadArray(PlayerData) ?? [])
            .OfType<JObject>()
            .Select(p =>
            {
                var uid = p.Value<string>("PlayerUID") ?? "";
                var ban = bans.FirstOrDefault(b => b.Uid == uid && b.IsActive(now));
                return new ServerPlayer(uid, p.Value<string>("LastKnownPlayername") ?? uid, p.Value<string>("RoleCode"),
                    Date(p["FirstJoinDate"]), Date(p["LastJoinDate"]),
                    whitelist.Any(w => w.Uid == uid && w.IsActive(now)), ban);
            })
            .Where(p => p.Uid.Length > 0)
            .OrderByDescending(p => p.LastJoin ?? DateTime.MinValue)
            .ToList();

        return new ServerPlayersView(players, [.. whitelist.Where(w => w.IsActive(now))], [.. bans.Where(b => b.IsActive(now))],
            roles, config?.Value<string>("DefaultRoleCode"), mode);
    }

    /// <summary>
    /// Правка файлов у остановленного сервера: роль известного игрока, убрать из белого списка или из банов, режим
    /// белого списка. Добавить в белый список можно только того, кто уже заходил (UID известен). Запись — через
    /// временный файл рядом; прежние файлы сервер не держит (он остановлен — проверяет вызывающий).
    /// </summary>
    public void Apply(PlayerFileEdit edit)
    {
        if (edit.Role is { } role)
        {
            var players = ReadArray(PlayerData) ?? throw new InvalidOperationException(Loc.T("players.noData"));
            var p = players.OfType<JObject>().FirstOrDefault(x => x.Value<string>("PlayerUID") == edit.Uid)
                    ?? throw new InvalidOperationException(Loc.T("players.unknown", edit.Uid));
            p["RoleCode"] = role;
            Write(PlayerData, players);
        }
        if (edit.Whitelisted is { } on)
        {
            var list = ReadArray(WhitelistFile) ?? [];
            Remove(list, edit.Uid);
            if (on)
            {
                var name = (ReadArray(PlayerData) ?? []).OfType<JObject>().FirstOrDefault(x => x.Value<string>("PlayerUID") == edit.Uid)
                    ?.Value<string>("LastKnownPlayername") ?? throw new InvalidOperationException(Loc.T("players.unknown", edit.Uid));
                list.Add(new JObject
                {
                    ["PlayerUID"] = edit.Uid,
                    ["PlayerName"] = name,
                    ["UntilDate"] = DateTime.Now.AddYears(50).ToString("o", CultureInfo.InvariantCulture),
                    ["Reason"] = "",
                    ["IssuedByPlayerName"] = "eViSTool",
                });
            }
            Write(WhitelistFile, list);
        }
        if (edit.Unban)
        {
            var list = ReadArray(BansFile) ?? [];
            if (Remove(list, edit.Uid)) Write(BansFile, list);
        }
        if (edit.Mode is { } mode)
        {
            var config = ReadObject(ConfigFile) ?? throw new InvalidOperationException(Loc.T("players.noConfig"));
            config["WhitelistMode"] = (int)mode;
            Write(ConfigFile, config);
        }
    }

    private static bool Remove(JArray list, string uid)
    {
        var gone = list.OfType<JObject>().Where(x => x.Value<string>("PlayerUID") == uid).ToList();
        foreach (var g in gone) g.Remove();
        return gone.Count > 0;
    }

    private static List<PlayerListEntry> ReadList(string file) =>
        [.. (ReadArray(file) ?? []).OfType<JObject>().Select(o => new PlayerListEntry(o.Value<string>("PlayerUID") ?? "",
            o.Value<string>("PlayerName"), o.Value<string>("IssuedByPlayerName"), o.Value<string>("Reason"), Date(o["UntilDate"])))
            .Where(e => e.Uid.Length > 0)];

    /// <summary>Даты в файлах сервера бывают ISO с часовым поясом или в формате его культуры — разбираем снисходительно.</summary>
    private static DateTime? Date(JToken? t) => t?.Type switch
    {
        JTokenType.Date => t.Value<DateTime>().ToLocalTime(),
        JTokenType.String when DateTime.TryParse(t.Value<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d)
            => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime(),
        JTokenType.String when DateTime.TryParse(t.Value<string>(), CultureInfo.CurrentCulture, DateTimeStyles.None, out var d) => d,
        _ => null,
    };

    private static JArray? ReadArray(string file) =>
        File.Exists(file) ? JToken.Parse(File.ReadAllText(file)) as JArray : null;

    private static JObject? ReadObject(string file) =>
        File.Exists(file) ? JToken.Parse(File.ReadAllText(file)) as JObject : null;

    private static void Write(string file, JToken token)
    {
        var tmp = file + ".evistool.tmp";
        File.WriteAllText(tmp, token.ToString(Formatting.Indented));
        File.Move(tmp, file, overwrite: true);
    }
}

/// <summary>
/// Команды сервера для игроков (синтаксис проверен на 1.22.7). Имя игрока сервер сам переводит в UID (через сервер
/// авторизации), поэтому так можно добавлять и тех, кто ещё не заходил.
/// </summary>
public static class PlayerCommands
{
    public static string Role(string player, string role) => $"/player {player} role {role}";
    public static string WhitelistAdd(string player, string? reason = null) => $"/whitelist add {player}{Tail(reason)}";
    public static string WhitelistRemove(string player) => $"/whitelist remove {player}";
    public static string WhitelistMode(bool on) => on ? "/whitelist on" : "/whitelist off";

    /// <summary>Срок — «N minute/hour/day/week/year»; «навсегда» — 100 лет (так сервер и хранит бессрочное).</summary>
    public static string Ban(string player, int amount, string unit, string? reason) =>
        $"/ban {player} {amount} {unit} {(string.IsNullOrWhiteSpace(reason) ? "-" : reason.Trim())}";
    public static string Unban(string player) => $"/unban {player}";
    public static string Kick(string player, string? reason = null) => $"/kick {player}{Tail(reason)}";
    public static string AllowCharSelOnce(string player) => $"/player {player} allowcharselonce";

    /// <summary>
    /// Сообщение в чат одному игроку: личных сообщений у консоли нет, поэтому «объявление в радиусе 2 блоков»,
    /// выполненное от лица самого игрока, — его увидит он (и кто стоит вплотную). Игрок должен быть в игре.
    /// Вложенная команда — со слешем: в справке сервера написано «без /», но так она молча не выполняется (проверено в игре на 1.22.7).
    /// </summary>
    public static string TellNear(string player, string text) => $"/executeas {player} /announcenear 2 {text}";

    private static string Tail(string? s) => string.IsNullOrWhiteSpace(s) ? "" : " " + s.Trim();

    /// <summary>Имя игрока — без пробелов и лишнего (иначе команда разберётся не так).</summary>
    public static bool IsValidName(string name) => name.Length is > 0 and <= 40 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
}
