using System.Runtime.CompilerServices;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Server.Config;

/// <summary>
/// serverconfig.json как дерево JSON: правятся отдельные значения, всё остальное (незнакомые поля, порядок,
/// точность чисел) остаётся как было. Сервер перезаписывает файл при остановке, поэтому сохранять можно только
/// при остановленном сервере — за этим следит вызывающий; <see cref="ChangedOnDisk"/> подскажет, что файл уже другой.
/// </summary>
public sealed class ServerConfigDocument
{
    private const string RolesName = "Roles", DefaultRoleName = "DefaultRoleCode";

    // одна обёртка на один объект роли — чтобы выбранную роль можно было сравнивать по ссылке
    private readonly ConditionalWeakTable<JObject, RoleEntry> _roles = new();
    private JObject _snapshot;
    private (DateTime Written, long Length) _disk;

    private ServerConfigDocument(string path, (DateTime, long) disk, JObject root)
    {
        FilePath = path;
        Root = root;
        _snapshot = (JObject)root.DeepClone();
        _disk = disk;
    }

    /// <summary>Прочитать конфиг. IOException (нет файла, занят) и JsonException (не JSON) — наружу.</summary>
    public static ServerConfigDocument Load(string path)
    {
        // отметку берём до чтения: успеют поменять между ними — увидим это как «изменён на диске»
        var disk = Stamp(path);
        return new ServerConfigDocument(path, disk, ModConfigEditor.Load(path));
    }

    public string FilePath { get; }

    /// <summary>Корень документа. Объект один на всю жизнь документа (и после <see cref="Revert"/>); править можно и напрямую.</summary>
    public JObject Root { get; }

    /// <summary>
    /// Значение по пути ("Port", "WorldConfig.WorldName"). Нет такого поля — null; в поле записан null — токен с типом Null.
    /// Возвращается сам токен документа, не копия.
    /// </summary>
    public JToken? Get(string path) => Find(Root, path);

    /// <summary>
    /// Записать значение (null → JSON-null). Недостающие промежуточные объекты создаются; существующее поле остаётся
    /// на своём месте, новое дописывается в конец.
    /// </summary>
    public void Set(string path, JToken? value)
    {
        var names = path.Split('.');
        var obj = Root;
        foreach (var name in names[..^1])
        {
            if (obj[name] is not JObject next)
            {
                // затирать чужое значение объектом не будем: путь сквозь строку или массив — ошибка вызывающего
                if (obj[name] is { Type: not JTokenType.Null })
                    throw new InvalidOperationException($"'{name}' in '{path}' is not an object.");
                obj[name] = next = new JObject();
            }
            obj = next;
        }
        obj[names[^1]] = value ?? JValue.CreateNull();
    }

    /// <summary>
    /// Документ отличается от загруженного (сохранённого). Сравнение по содержимому: вернул прежнее значение — снова «чисто».
    /// Порядок полей внутри объекта при сравнении не учитывается.
    /// </summary>
    public bool IsDirty => !JToken.DeepEquals(Root, _snapshot);

    /// <summary>Файл на диске изменился (или исчез) с момента Load/Save — например, сервер запустили и остановили.</summary>
    public bool ChangedOnDisk() => Stamp(FilePath) != _disk;

    /// <summary>Записать документ (атомарно, прежний файл остаётся рядом как *.evistool.bak). Пишет и без изменений.</summary>
    public void Save()
    {
        ModConfigEditor.Save(FilePath, Root);
        _snapshot = (JObject)Root.DeepClone();
        _disk = Stamp(FilePath);
    }

    /// <summary>
    /// Отменить все правки: вернуть содержимое к загруженному (сохранённому). Токены и обёртки ролей, полученные до
    /// отмены, больше к документу не относятся — <see cref="Roles"/> нужно взять заново.
    /// </summary>
    public void Revert()
    {
        Root.RemoveAll();
        foreach (var prop in _snapshot.Properties())
            Root.Add(prop.Name, prop.Value.DeepClone());
    }

    // ---- роли

    /// <summary>Роли в порядке файла. Обёртки живые: правка свойства сразу меняет документ.</summary>
    public IReadOnlyList<RoleEntry> Roles =>
        Root[RolesName] is JArray list
            ? [.. list.OfType<JObject>().Select(role => _roles.GetValue(role, static json => new RoleEntry(json)))]
            : [];

    /// <summary>Роль, которую получает новый игрок.</summary>
    public string? DefaultRoleCode
    {
        get => Root[DefaultRoleName] is JValue { Value: string code } ? code : null;
        set
        {
            if (DefaultRoleCode != value) Root[DefaultRoleName] = value is null ? JValue.CreateNull() : new JValue(value);
        }
    }

    /// <summary>
    /// Копия роли в конец списка: всё как у исходной (привилегии, лимиты, незнакомые поля), но со своим кодом.
    /// Имя — исходное, если не задано своё. Код не может быть пустым или совпадать с кодом другой роли (регистр не важен) —
    /// иначе InvalidOperationException с понятным текстом.
    /// </summary>
    public RoleEntry DuplicateRole(RoleEntry source, string newCode, string? newName = null)
    {
        var code = newCode?.Trim() ?? "";
        if (code.Length == 0) throw new InvalidOperationException(Loc.T("cfgerr.roleCodeEmpty"));
        if (Roles.Any(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(Loc.T("cfgerr.roleCodeTaken", code));

        var json = (JObject)source.Json.DeepClone();
        if (Root[RolesName] is not JArray list) Root[RolesName] = list = new JArray();
        list.Add(json);

        var copy = _roles.GetValue(json, static j => new RoleEntry(j));
        copy.Code = code;
        if (!string.IsNullOrWhiteSpace(newName)) copy.Name = newName.Trim();
        return copy;
    }

    /// <summary>Удалить роль. false — если она стандартная, назначена ролью по умолчанию или её нет в документе.</summary>
    public bool RemoveRole(RoleEntry role)
    {
        if (role.IsStandard || string.Equals(role.Code, DefaultRoleCode, StringComparison.OrdinalIgnoreCase)) return false;
        if (Root[RolesName] is not JArray list || role.Json.Parent != list) return false;
        role.Json.Remove();
        return true;
    }

    // ---- настройки мира

    /// <summary>
    /// Настройки мира (WorldConfig.WorldConfiguration) в порядке файла. Значения — текстом, как записаны
    /// (true/false строчными, числа с точкой). Ключи различаются регистром, как и в игре.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> WorldSettings =>
        Get(ServerConfigSchema.WorldSettingsPath) is JObject settings
            ? [.. settings.Properties().Select(p => KeyValuePair.Create(p.Name, ConfigValueCodec.ScalarText(p.Value)))]
            : [];

    /// <summary>
    /// Заменить настройки мира — в порядке переданного списка, значениями-строками. Ключ, повторённый дважды,
    /// остаётся один (на первом месте, с последним значением); пустые ключи пропускаются.
    /// Значение, которое не меняли, остаётся в файле тем же токеном, каким было (вдруг там число или bool, вписанные руками).
    /// Пустой список: был объект — останется пустой объект; не было (null или поля нет) — вернётся как было.
    /// </summary>
    public void SetWorldSettings(IEnumerable<KeyValuePair<string, string>> settings)
    {
        const string path = ServerConfigSchema.WorldSettingsPath;
        var current = Get(path) as JObject;
        var result = new JObject();
        foreach (var (rawKey, rawValue) in settings)
        {
            var key = rawKey?.Trim() ?? "";
            if (key.Length == 0) continue;
            var value = rawValue ?? "";
            var kept = current?.Property(key, StringComparison.Ordinal)?.Value;
            result[key] = kept is not null && ConfigValueCodec.ScalarText(kept) == value ? kept.DeepClone() : new JValue(value);
        }

        var before = Find(_snapshot, path);
        if (result.Count > 0 || before is JObject)
            Set(path, result);
        else if (before is not null)
            Set(path, before.DeepClone());
        else
            Get(path)?.Parent?.Remove(); // поля не было — убираем и его свойство
    }

    private static JToken? Find(JObject root, string path)
    {
        JToken? current = root;
        foreach (var name in path.Split('.'))
            if (current is not JObject obj || !obj.TryGetValue(name, out current))
                return null;
        return current;
    }

    /// <summary>Время записи и длина файла; нет файла — пустая отметка.</summary>
    private static (DateTime, long) Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
    }
}
