using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Server.Config;

/// <summary>
/// Роль из serverconfig.json — обёртка над её JSON-объектом: правка свойства сразу пишет в документ.
/// Полей, которых обёртка не знает (DefaultSpawn, RuntimePrivileges, добавленные модами), она не касается.
/// Нет поля в JSON — читается значение по умолчанию (0, "", false); запись значения, которое и так читается,
/// ничего не меняет — документ не становится «грязным» оттого, что поле ввода вернуло то же самое.
/// </summary>
public sealed class RoleEntry
{
    private const string PrivilegesName = "Privileges", MinSizeName = "LandClaimMinSize";

    public RoleEntry(JObject json) => Json = json;

    public JObject Json { get; }

    public string Code { get => Text("Code"); set => SetText("Code", value); }
    public string Name { get => Text("Name"); set => SetText("Name", value); }
    public string Description { get => Text("Description"); set => SetText("Description", value); }

    /// <summary>Название цвета, как его пишет сервер: "White", "LightGreen"…</summary>
    public string Color { get => Text("Color"); set => SetText("Color", value); }

    public int PrivilegeLevel { get => Int(Json["PrivilegeLevel"]); set => SetInt(Json, "PrivilegeLevel", value); }

    /// <summary>Режим игры числом, как в файле.</summary>
    public int DefaultGameMode { get => Int(Json["DefaultGameMode"]); set => SetInt(Json, "DefaultGameMode", value); }

    public int LandClaimAllowance { get => Int(Json["LandClaimAllowance"]); set => SetInt(Json, "LandClaimAllowance", value); }
    public int LandClaimMaxAreas { get => Int(Json["LandClaimMaxAreas"]); set => SetInt(Json, "LandClaimMaxAreas", value); }

    public int LandClaimMinX { get => MinSize("X"); set => SetMinSize("X", value); }
    public int LandClaimMinY { get => MinSize("Y"); set => SetMinSize("Y", value); }
    public int LandClaimMinZ { get => MinSize("Z"); set => SetMinSize("Z", value); }

    public bool AutoGrant
    {
        get => Json["AutoGrant"] is JValue { Value: true };
        set { if (AutoGrant != value) Json["AutoGrant"] = value; }
    }

    /// <summary>Привилегии как в файле — в том же порядке, вместе с незнакомыми программе.</summary>
    public IReadOnlyList<string> Privileges =>
        Json[PrivilegesName] is JArray list ? [.. list.Where(t => t.Type == JTokenType.String).Select(t => (string)t!)] : [];

    /// <summary>Выдать (в конец списка, если ещё нет) или отобрать привилегию. Остальные и их порядок не трогаются.</summary>
    public void SetPrivilege(string code, bool granted)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var list = Json[PrivilegesName] as JArray;
        var present = list?.Where(t => t.Type == JTokenType.String && (string)t! == code).ToList() ?? [];
        if (!granted)
        {
            present.ForEach(t => t.Remove());
            return;
        }

        if (present.Count > 0) return;
        if (list is null) Json[PrivilegesName] = list = new JArray();
        list.Add(code);
    }

    /// <summary>Роль из тех, что сервер создаёт сам.</summary>
    public bool IsStandard => ServerConfigSchema.StandardRoleCodes.Contains(Code, StringComparer.OrdinalIgnoreCase);

    private string Text(string name) => Json[name] is JValue { Value: string s } ? s : "";

    private void SetText(string name, string? value)
    {
        value ??= "";
        if (Text(name) != value) Json[name] = value;
    }

    private static int Int(JToken? token) =>
        token is JValue { Value: long n } ? (int)Math.Clamp(n, int.MinValue, int.MaxValue) : 0;

    private static void SetInt(JObject obj, string name, int value)
    {
        if (Int(obj[name]) != value) obj[name] = value;
    }

    private int MinSize(string axis) => Json[MinSizeName] is JObject size ? Int(size[axis]) : 0;

    private void SetMinSize(string axis, int value)
    {
        if (MinSize(axis) == value) return;
        // LandClaimMinSize может быть null или отсутствовать — заводим объект целиком
        if (Json[MinSizeName] is not JObject size)
            Json[MinSizeName] = size = new JObject { ["X"] = 0, ["Y"] = 0, ["Z"] = 0 };
        size[axis] = value;
    }
}
