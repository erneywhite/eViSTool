using Newtonsoft.Json.Linq;
using Kind = eViSTool.Core.Server.Config.ConfigValueKind;

namespace eViSTool.Core.Server.Config;

/// <summary>
/// Что программа знает о полях serverconfig.json (игра 1.22.7): в каком разделе и группе показывать, чем править,
/// какие значения допустимы. Всё, чего в схеме нет (поля модов и будущих версий), попадает в «Дополнительно»
/// с типом по значению в файле — ничего не теряется.
/// </summary>
public static class ServerConfigSchema
{
    /// <summary>Настройки мира (словарь «ключ → значение»): правятся отдельным списком, а не полем.</summary>
    public const string WorldSettingsPath = "WorldConfig.WorldConfiguration";

    /// <summary>Группа полей, которых нет в схеме (и тех известных, что не подошли ни к одной другой).</summary>
    public const string OtherGroup = "other";

    private const string WorldObject = "WorldConfig";

    /// <summary>
    /// Поля с осмысленным местом в редакторе — в том порядке, в каком их показывать: разделы и группы идут подряд,
    /// внутри группы важное выше. Группа «other» — последняя: за ней встают поля вне схемы.
    /// </summary>
    public static IReadOnlyList<ConfigFieldSpec> Known { get; } =
    [
        // ==== «Основное»
        General("identity", "ServerName", Kind.Text) with { Required = true },
        General("identity", "ServerDescription", Kind.Multiline) with { Nullable = true },
        // null здесь роняет сервер при входе игрока
        General("identity", "WelcomeMessage", Kind.Multiline) with { NeverNull = true },
        General("identity", "ServerUrl", Kind.Text) with { Nullable = true },
        General("identity", "ServerLanguage", Kind.Choice) with
        {
            // языки игры, в порядке её списка
            Choices = ChoicesOf("ServerLanguage",
                "en", "ar", "be", "bg", "cs", "da", "nl", "fi", "fr", "de", "hu", "eo", "is", "it", "ja", "ko", "lt", "no", "pl",
                "pt-pt", "pt-br", "ru", "sr", "es-es", "es-419", "sk", "sv-se", "th", "tr", "uk", "vi", "zh-cn", "zh-tw"),
        },

        // «все адреса» — именно null; что сервер делает с "", неизвестно
        General("network", "Ip", Kind.Text) with { Nullable = true, EmptyIsNull = true },
        General("network", "Port", Kind.Integer) with { Min = 1, Max = 65535 },
        General("network", "Password", Kind.Text) with { Nullable = true },
        General("network", "Upnp", Kind.Bool),
        General("network", "AdvertiseServer", Kind.Bool),

        General("players", "MaxClients", Kind.Integer) with { Min = 1 },
        General("players", "MaxClientsInQueue", Kind.Integer) with { Min = 0 },
        // 0 — по умолчанию (на выделенном сервере — включён), 1 — выключен, 2 — включён
        General("players", "WhitelistMode", Kind.Choice) with { NumericChoice = true, Choices = ChoicesOf("WhitelistMode", "0", "1", "2") },
        General("players", "VerifyPlayerAuth", Kind.Bool),
        General("players", "WarnClientsAfterAfkSeconds", Kind.Integer) with { Min = 0 },
        General("players", "KickClientsAfterAfkSeconds", Kind.Integer) with { Min = 0 },

        General("gameplay", "AllowPvP", Kind.Bool),
        General("gameplay", "PassTimeWhenEmpty", Kind.Bool),

        General("startup", "StartupCommands", Kind.Multiline) with { Nullable = true },

        // ==== «Мир». Почти всё сервер читает один раз — когда создаёт мир; файл мира — при каждом запуске
        World("world", "WorldConfig.SaveFileLocation", Kind.Path) with { Required = true },
        AtCreation(World("world", "WorldConfig.WorldName", Kind.Text)) with { Required = true },
        AtCreation(World("world", "WorldConfig.Seed", Kind.Text)) with { Nullable = true },
        AtCreation(World("world", "WorldConfig.PlayStyle", Kind.Choice)) with
        {
            Choices = ChoicesOf("WorldConfig.PlayStyle", "surviveandbuild", "exploration", "wildernesssurvival", "homosapiens", "creativebuilding"),
        },
        // код названия стиля: список открытый (моды добавляют свои), поэтому текст, а не выбор
        AtCreation(World("world", "WorldConfig.PlayStyleLangCode", Kind.Text)),
        AtCreation(World("world", "WorldConfig.WorldType", Kind.Choice)) with { Choices = ChoicesOf("WorldConfig.WorldType", "standard", "superflat") },
        // сервер 1.22.7 это поле, похоже, не читает
        AtCreation(World("world", "WorldConfig.AllowCreativeMode", Kind.Bool)) with { Legacy = true },

        // больше игра не берёт: ширину и длину обрезает до 67108864, высоту — до 16384
        AtCreation(World("map", "MapSizeX", Kind.Integer)) with { Min = 0, Max = 67108864 },
        AtCreation(World("map", "MapSizeZ", Kind.Integer)) with { Min = 0, Max = 67108864 },
        AtCreation(World("map", "MapSizeY", Kind.Integer)) with { Min = 0, Max = 16384 },
        AtCreation(World("map", "WorldConfig.MapSizeY", Kind.Integer)) with { Nullable = true, Min = 0, Max = 16384 },

        // ==== «Дополнительно»
        Advanced("network", "ClientConnectionTimeout", Kind.Integer) with { Min = 1 },
        Advanced("network", "CompressPackets", Kind.Bool),
        Advanced("network", "UpnpInfiniteLifetime", Kind.Bool),
        Advanced("network", "MasterserverUrl", Kind.Text) with { Nullable = true },

        Advanced("performance", "MaxChunkRadius", Kind.Integer) with { Min = 1 },
        Advanced("performance", "TickTime", Kind.Decimal) with { Min = 1 },
        Advanced("performance", "SpawnCapPlayerScaling", Kind.Decimal) with { Min = 0 },
        Advanced("performance", "BlockTickChunkRange", Kind.Integer) with { Min = 0 },
        Advanced("performance", "RandomBlockTicksPerChunk", Kind.Integer) with { Min = 0 },
        Advanced("performance", "BlockTickInterval", Kind.Integer) with { Min = 1 },
        Advanced("performance", "MaxMainThreadBlockTicks", Kind.Integer) with { Min = 0 },

        // null в списке папок роняет сервер при загрузке модов
        Advanced("mods", "ModPaths", Kind.StringList) with { NeverNull = true },
        Advanced("mods", "WorldConfig.DisabledMods", Kind.StringList) with { Nullable = true },
        // пустые списки сама игра пишет как null
        Advanced("mods", "ModIdBlackList", Kind.StringList) with { Nullable = true, EmptyIsNull = true },
        Advanced("mods", "ModIdWhiteList", Kind.StringList) with { Nullable = true, EmptyIsNull = true },
        Advanced("mods", "ModDbUrl", Kind.Text),
        Advanced("mods", "DisableModSafetyCheck", Kind.Bool),
        Advanced("mods", "DisableAutoRemap", Kind.Bool),

        // 0 — выключена, 1 — базовая, 2 — строгая (пока работает как базовая)
        Advanced("antiabuse", "AntiAbuse", Kind.Choice) with { NumericChoice = true, Choices = ChoicesOf("AntiAbuse", "0", "1", "2") },
        Advanced("antiabuse", "AntiAbuseTriggerOnBlockBreakCount", Kind.Integer) with { Min = 1 },
        Advanced("antiabuse", "AntiAbuseTriggerOnDurationMs", Kind.Integer) with { Min = 1 },
        Advanced("antiabuse", "AntiAbuseBufferSize", Kind.Integer) with { Min = 1 },
        Advanced("antiabuse", "AntiAbuseBlockBurstAbuseBanDays", Kind.Decimal) with { Min = 0 },
        Advanced("antiabuse", "ChatRateLimitMs", Kind.Integer) with { Min = 0 },
        Advanced("antiabuse", "LoginFloodProtection", Kind.Bool),
        Advanced("antiabuse", "TemporaryIpBlockList", Kind.Bool),

        Advanced("safety", "CorruptionProtection", Kind.Bool),
        // ошибка игры: порог считается в 32 битах, и от 2048 МБ защита молча отключается
        Advanced("safety", "DieBelowDiskSpaceMb", Kind.Integer) with { Min = 0, Max = 2047 },
        Advanced("safety", "DieAboveMemoryUsageMb", Kind.Integer) with { Min = 1 },
        Advanced("safety", "DieAboveErrorCount", Kind.Integer) with { Min = 1 },
        Advanced("safety", "WorldConfig.RepairMode", Kind.Bool),
        Advanced("safety", "AnalyzeMode", Kind.Bool),
        Advanced("safety", "RegenerateCorruptChunks", Kind.Bool),

        Advanced("logging", "LogBlockBreakPlace", Kind.Bool),
        // в игре это uint
        Advanced("logging", "LogFileSplitAfterLine", Kind.Integer) with { Min = 0, Max = uint.MaxValue },

        Advanced("hosting", "HostedMode", Kind.Bool),
        Advanced("hosting", "HostedModeAllowMods", Kind.Bool),
        Advanced("hosting", "VhIdentifier", Kind.Text) with { Nullable = true },

        // с 1.21 огнём и падающими блоками управляют настройки мира; остальное читается только при переносе старых конфигов
        Advanced("legacy", "AllowFireSpread", Kind.Bool) with { Legacy = true },
        Advanced("legacy", "AllowFallingBlocks", Kind.Bool) with { Legacy = true },
        Advanced("legacy", "OnlyWhitelisted", Kind.Bool) with { Legacy = true },
        Advanced("legacy", "DefaultSpawn", Kind.Json) with { Legacy = true },

        // сервер ведёт эти поля сам и перезаписывает при запуске
        Advanced("service", "ConfigVersion", Kind.ReadOnly),
        Advanced("service", "ServerIdentifier", Kind.ReadOnly),
        Advanced("service", "NextPlayerGroupUid", Kind.ReadOnly),
        Advanced("service", "LastLaunchPlaystyle", Kind.ReadOnly),
        Advanced("service", "RepairMode", Kind.ReadOnly), // копия WorldConfig.RepairMode

        Advanced(OtherGroup, "MaxOwnedGroupChannelsPerUser", Kind.Integer) with { Min = 0 },
        // в коде 1.22.7 нигде не читается
        Advanced(OtherGroup, "GroupChatHistorySize", Kind.Integer) with { Min = 0, Legacy = true },
        AtCreation(Advanced(OtherGroup, "WorldConfig.CreatedByPlayerName", Kind.Text)) with { Nullable = true },
        Advanced(OtherGroup, "EntityDebugMode", Kind.Bool),
        Advanced(OtherGroup, "SkipEveryChunkRow", Kind.Integer) with { Min = 0 },
        Advanced(OtherGroup, "SkipEveryChunkRowWidth", Kind.Integer) with { Min = 0 },
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

    /// <summary>Группы раздела в порядке показа (как идут в <see cref="Known"/>); у «Дополнительно» последняя — «other».</summary>
    public static IReadOnlyList<string> GroupsOf(string section) =>
        [.. Known.Where(f => f.Section == section).Select(f => f.Group).Distinct()];

    /// <summary>
    /// Поля для редактора: известные из схемы, которые есть в документе (в порядке схемы), затем все прочие поля
    /// верхнего уровня и WorldConfig.* (кроме <see cref="Hidden"/> и <see cref="WorldSettingsPath"/>) — в порядке файла,
    /// в раздел «Дополнительно», с типом по значению в JSON.
    /// Описание подстраивается под то, что лежит в файле: незнакомое значение Choice добавляется к вариантам;
    /// null делает поле Nullable, а "" и [] — наоборот (пустой ввод возвращает ту же «пустоту», что была) — кроме полей
    /// с <see cref="ConfigFieldSpec.EmptyIsNull"/> и <see cref="ConfigFieldSpec.NeverNull"/>, у них «пустота» одна;
    /// если тип значения не тот, что ждёт схема (Port записан строкой), поле правится по правилам автотипа.
    /// Значение вне границ схемы поле не меняет: пока его не трогают, оно сохраняется как было
    /// (<see cref="ConfigValueCodec.TryParse(string?, ConfigFieldSpec, JToken?, out JToken?, out string?, out object?[])"/>).
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

    /// <summary>
    /// Поле по правилам автотипа: от схемы остаются место, подпись и пометки «только при создании мира» / «устарело»;
    /// границы, варианты и правила пустого ввода к значению другого типа не подходят.
    /// </summary>
    private static ConfigFieldSpec Auto(ConfigFieldSpec spec, JToken token)
    {
        var auto = new ConfigFieldSpec
        {
            Path = spec.Path, Section = spec.Section, Group = spec.Group, Known = spec.Known, Kind = KindOf(token),
            WorldCreationOnly = spec.WorldCreationOnly, Legacy = spec.Legacy,
        };
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
                // NeverNull: null в файле — ошибка, возвращать его пустым вводом незачем
                _ => spec.Nullable || spec.NeverNull ? spec : spec with { Nullable = true },
            };

        switch (spec.Kind)
        {
            case Kind.Text or Kind.Multiline or Kind.Path when token.Type == JTokenType.String:
                return spec.Nullable && !spec.EmptyIsNull && ((string)token!).Length == 0 ? spec with { Nullable = false } : spec;

            case Kind.Integer when token.Type == JTokenType.Integer:
            // целое в дробном поле (TickTime: 30) — всё равно дробное поле
            case Kind.Decimal when token.Type is JTokenType.Float or JTokenType.Integer:
            case Kind.Bool when token.Type == JTokenType.Boolean:
                return spec;

            case Kind.Choice when token.Type == (spec.NumericChoice ? JTokenType.Integer : JTokenType.String):
            {
                var value = ConfigValueCodec.ToText(token, spec);
                return spec.Choices.Any(c => c.Value == value) ? spec : spec with { Choices = [.. spec.Choices, new ConfigChoice(value, value)] };
            }

            case Kind.StringList when IsStringList(token):
                return spec.Nullable && !spec.EmptyIsNull && !token.HasValues ? spec with { Nullable = false } : spec;

            default:
                return Auto(spec, token);
        }
    }

    private static ConfigFieldSpec General(string group, string path, Kind kind) => Field(ConfigSections.General, group, path, kind);
    private static ConfigFieldSpec World(string group, string path, Kind kind) => Field(ConfigSections.World, group, path, kind);
    private static ConfigFieldSpec Advanced(string group, string path, Kind kind) => Field(ConfigSections.Advanced, group, path, kind);

    /// <summary>Сервер читает поле только при создании мира.</summary>
    private static ConfigFieldSpec AtCreation(ConfigFieldSpec spec) => spec with { WorldCreationOnly = true };

    private static ConfigFieldSpec Field(string section, string group, string path, Kind kind) =>
        new() { Path = path, Section = section, Group = group, Kind = kind, Known = true };

    /// <summary>Подписи вариантов — ключи «cfg.&lt;Path&gt;.choice.&lt;Value&gt;».</summary>
    private static IReadOnlyList<ConfigChoice> ChoicesOf(string path, params string[] values) =>
        [.. values.Select(v => new ConfigChoice(v, $"cfg.{path}.choice.{v}"))];
}
