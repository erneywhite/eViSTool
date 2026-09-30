using Newtonsoft.Json.Linq;
using Kind = eViSTool.Core.Server.Config.ConfigValueKind;

namespace eViSTool.Core.Server.Config;

/// <summary>
/// Что программа знает о полях serverconfig.json: какие показывать на «Основном» и «Мире», чем их править.
/// Всё, чего в схеме нет, попадает в «Дополнительно» с типом по значению в файле — ничего не теряется.
/// </summary>
public static class ServerConfigSchema
{
    /// <summary>Настройки мира (словарь «ключ → значение»): правятся отдельным списком, а не полем.</summary>
    public const string WorldSettingsPath = "WorldConfig.WorldConfiguration";

    /// <summary>Группа полей, которых нет в схеме.</summary>
    public const string OtherGroup = "other";

    private const string WorldObject = "WorldConfig";

    /// <summary>Поля с осмысленным местом в редакторе — в том порядке, в каком их показывать.</summary>
    public static IReadOnlyList<ConfigFieldSpec> Known { get; } =
    [
        General("identity", "ServerName", Kind.Text) with { Required = true },
        General("identity", "ServerDescription", Kind.Multiline) with { Nullable = true },
        General("identity", "WelcomeMessage", Kind.Multiline) with { Nullable = true },
        General("identity", "ServerUrl", Kind.Text) with { Nullable = true },
        General("identity", "ServerLanguage", Kind.Text) with { Required = true },

        General("network", "Ip", Kind.Text) with { Nullable = true },
        General("network", "Port", Kind.Integer) with { Min = 1, Max = 65535 },
        General("network", "Password", Kind.Text) with { Nullable = true },
        General("network", "Upnp", Kind.Bool),
        General("network", "AdvertiseServer", Kind.Bool),

        General("players", "MaxClients", Kind.Integer) with { Min = 1 },
        General("players", "MaxClientsInQueue", Kind.Integer) with { Min = 0 },
        General("players", "WhitelistMode", Kind.Choice) with { NumericChoice = true, Choices = ChoicesOf("WhitelistMode", "0", "1", "2") },
        General("players", "VerifyPlayerAuth", Kind.Bool),
        General("players", "WarnClientsAfterAfkSeconds", Kind.Integer) with { Min = 0 },
        General("players", "KickClientsAfterAfkSeconds", Kind.Integer) with { Min = 0 },

        General("gameplay", "AllowPvP", Kind.Bool),
        General("gameplay", "AllowFireSpread", Kind.Bool),
        General("gameplay", "AllowFallingBlocks", Kind.Bool),
        General("gameplay", "PassTimeWhenEmpty", Kind.Bool),

        General("startup", "StartupCommands", Kind.Multiline) with { Nullable = true },

        World("world", "WorldConfig.WorldName", Kind.Text) with { Required = true },
        World("world", "WorldConfig.Seed", Kind.Text) with { Nullable = true },
        World("world", "WorldConfig.SaveFileLocation", Kind.Path) with { Required = true },
        World("world", "WorldConfig.PlayStyle", Kind.Choice) with
        {
            Choices = ChoicesOf("WorldConfig.PlayStyle", "surviveandbuild", "exploration", "wildernesssurvival", "creativebuilding", "homosapiens"),
        },
        World("world", "WorldConfig.PlayStyleLangCode", Kind.Text),
        World("world", "WorldConfig.WorldType", Kind.Choice) with { Choices = ChoicesOf("WorldConfig.WorldType", "standard", "superflat") },
        World("world", "WorldConfig.AllowCreativeMode", Kind.Bool),

        World("map", "MapSizeX", Kind.Integer) with { Min = 0 },
        World("map", "MapSizeY", Kind.Integer) with { Min = 0 },
        World("map", "MapSizeZ", Kind.Integer) with { Min = 0 },
        World("map", "WorldConfig.MapSizeY", Kind.Integer) with { Nullable = true },

        Field(ConfigSections.Advanced, "service", "ConfigVersion", Kind.ReadOnly),
        Field(ConfigSections.Advanced, "service", "ServerIdentifier", Kind.ReadOnly),
    ];

    /// <summary>
    /// Не показываются в общих списках: служебное (FileEditWarning, LastLaunchMods), роли (у них своя вкладка)
    /// и сам объект WorldConfig — он разложен по полям.
    /// </summary>
    public static IReadOnlySet<string> Hidden { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "FileEditWarning", "Roles", "DefaultRoleCode", WorldObject, "LastLaunchMods" };

    /// <summary>Привилегии, которые знает сама игра. В роли могут встретиться и другие (от модов) — их не трогаем.</summary>
    public static IReadOnlyList<string> KnownPrivileges { get; } =
    [
        "build", "useblock", "buildblockseverywhere", "useblockseverywhere", "attackplayers", "attackcreatures", "freemove",
        "gamemode", "pickingrange", "chat", "selfkill", "kick", "ban", "whitelist", "setwelcome", "announce", "readlists", "give",
        "areamodify", "setspawn", "controlserver", "tp", "time", "grantrevoke", "root", "commandplayer", "controlplayergroups",
        "manageplayergroups", "manageotherplayergroups", "worldedit",
    ];

    /// <summary>Роли, которые сервер создаёт сам: удалять их нельзя.</summary>
    public static IReadOnlyList<string> StandardRoleCodes { get; } =
        ["suvisitor", "crvisitor", "limitedsuplayer", "limitedcrplayer", "suplayer", "crplayer", "sumod", "crmod", "admin"];

    private static readonly HashSet<string> KnownPaths = Known.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Поля для редактора: известные из схемы, которые есть в документе (в порядке схемы), затем все прочие поля
    /// верхнего уровня и WorldConfig.* (кроме <see cref="Hidden"/> и <see cref="WorldSettingsPath"/>) — в порядке файла,
    /// в раздел «Дополнительно», с типом по значению в JSON.
    /// Описание подстраивается под то, что лежит в файле: незнакомое значение Choice добавляется к вариантам;
    /// null делает поле Nullable, а "" и [] — наоборот (пустой ввод возвращает ту же «пустоту», что была);
    /// если тип значения не тот, что ждёт схема (Port записан строкой), поле правится по правилам автотипа.
    /// </summary>
    public static IReadOnlyList<ConfigFieldSpec> FieldsFor(ServerConfigDocument doc)
    {
        var fields = new List<ConfigFieldSpec>();
        foreach (var spec in Known)
            if (doc.Get(spec.Path) is { } token)
                fields.Add(Adapt(spec, token));

        foreach (var prop in doc.Root.Properties())
        {
            if (prop.Name == WorldObject && prop.Value is JObject world)
                foreach (var inner in world.Properties())
                    AddOther(WorldObject + "." + inner.Name, inner);
            else
                AddOther(prop.Name, prop);
        }

        return fields;

        void AddOther(string path, JProperty prop)
        {
            if (KnownPaths.Contains(path) || Hidden.Contains(path) || path == WorldSettingsPath) return;
            // имя с точкой путём не адресуется — такое поле не показываем (в файле оно остаётся как было)
            if (prop.Name.Contains('.')) return;
            fields.Add(Auto(Field(ConfigSections.Advanced, OtherGroup, path, Kind.Json) with { Known = false }, prop.Value));
        }
    }

    /// <summary>Тип поля вне схемы — по значению в JSON.</summary>
    private static Kind KindOf(JToken token) => token.Type switch
    {
        JTokenType.Boolean => Kind.Bool,
        // число, не влезающее в long, целым полем не править — только как сырое значение
        JTokenType.Integer => token is JValue { Value: long } ? Kind.Integer : Kind.Json,
        JTokenType.Float => Kind.Decimal,
        JTokenType.String => Kind.Text,
        JTokenType.Array when IsStringList(token) => Kind.StringList,
        _ => Kind.Json,
    };

    private static bool IsStringList(JToken token) => token is JArray list && list.All(t => t.Type == JTokenType.String);

    private static ConfigFieldSpec Auto(ConfigFieldSpec spec, JToken token)
    {
        var auto = new ConfigFieldSpec { Path = spec.Path, Section = spec.Section, Group = spec.Group, Known = spec.Known, Kind = KindOf(token) };
        // целые поля сервера — int, и кодек дальше int не пускает; но то, что уже лежит в файле, запрещать не будем
        return auto.Kind == Kind.Integer && (long)token is < int.MinValue or > int.MaxValue
            ? auto with { Min = long.MinValue, Max = long.MaxValue }
            : auto;
    }

    private static ConfigFieldSpec Adapt(ConfigFieldSpec spec, JToken token)
    {
        if (spec.Kind is Kind.ReadOnly or Kind.Json) return spec;

        if (token.Type == JTokenType.Null)
            return spec.Kind switch
            {
                // переключателю null показать нечем
                Kind.Bool => Auto(spec, token),
                // пустой вариант — чтобы null было чем выбрать
                Kind.Choice => spec with { Nullable = true, Choices = [new ConfigChoice("", ""), .. spec.Choices] },
                _ => spec.Nullable ? spec : spec with { Nullable = true },
            };

        switch (spec.Kind)
        {
            case Kind.Text or Kind.Multiline or Kind.Path when token.Type == JTokenType.String:
                return spec.Nullable && ((string)token!).Length == 0 ? spec with { Nullable = false } : spec;

            case Kind.Integer when token.Type == JTokenType.Integer:
            case Kind.Decimal when token.Type == JTokenType.Float:
            case Kind.Bool when token.Type == JTokenType.Boolean:
                return spec;

            case Kind.Choice when token.Type == (spec.NumericChoice ? JTokenType.Integer : JTokenType.String):
            {
                var value = ConfigValueCodec.ToText(token, spec);
                return spec.Choices.Any(c => c.Value == value) ? spec : spec with { Choices = [.. spec.Choices, new ConfigChoice(value, value)] };
            }

            case Kind.StringList when IsStringList(token):
                return spec.Nullable && !token.HasValues ? spec with { Nullable = false } : spec;

            default:
                return Auto(spec, token);
        }
    }

    private static ConfigFieldSpec General(string group, string path, Kind kind) => Field(ConfigSections.General, group, path, kind);
    private static ConfigFieldSpec World(string group, string path, Kind kind) => Field(ConfigSections.World, group, path, kind);

    private static ConfigFieldSpec Field(string section, string group, string path, Kind kind) =>
        new() { Path = path, Section = section, Group = group, Kind = kind, Known = true };

    /// <summary>Подписи вариантов — ключи «cfg.&lt;Path&gt;.choice.&lt;Value&gt;».</summary>
    private static IReadOnlyList<ConfigChoice> ChoicesOf(string path, params string[] values) =>
        [.. values.Select(v => new ConfigChoice(v, $"cfg.{path}.choice.{v}"))];
}
