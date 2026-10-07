using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.App.ViewModels;

/// <summary>
/// Поле формы конфига — одно значение JSON. Правка сразу пишется в дерево JSON (<see cref="JToken"/>) и сообщает
/// форме (<c>changed</c>), а форма пересобирает из дерева текст файла. Ключ показываем как есть: так его называют
/// в описаниях модов. Список полей плоский; вложенность — отступом (<see cref="Depth"/>) под заголовком группы.
/// </summary>
public abstract partial class ConfigField(string key, int depth, Action changed) : ObservableObject
{
    public string Key { get; } = key;
    public int Depth { get; } = depth;
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    protected Action Changed { get; } = changed;

    /// <summary>Что не так со значением (не число, сломанный JSON); пусто — в порядке. Пока есть ошибка — дерево не меняется.</summary>
    [ObservableProperty] private string _error = "";
}

/// <summary>Заголовок вложенного объекта.</summary>
public sealed class GroupField(string key, int depth) : ConfigField(key, depth, () => { });

/// <summary>Да/нет — переключатель.</summary>
public sealed partial class BoolField : ConfigField
{
    private readonly JValue _token;

    public BoolField(string key, int depth, JValue token, Action changed) : base(key, depth, changed)
    {
        _token = token;
        _value = token.Value<bool>();
    }

    [ObservableProperty] private bool _value;

    partial void OnValueChanged(bool value)
    {
        _token.Value = value;
        Changed();
    }
}

/// <summary>
/// Число. Целое остаётся целым (в поле «3» — в файле 3), дробное — дробным («1.0» не превратится в «1»).
/// Запятая принимается как точка.
/// </summary>
public sealed partial class NumberField : ConfigField
{
    private readonly JValue _token;
    private readonly bool _integer;

    public NumberField(string key, int depth, JValue token, Action changed) : base(key, depth, changed)
    {
        _token = token;
        _integer = token.Type == JTokenType.Integer;
        _value = token.ToString(Formatting.None);
    }

    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value)
    {
        var s = value.Trim().Replace(',', '.');
        if (_integer && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            _token.Value = l;
        else if (!_integer && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            _token.Value = d;
        else
        {
            Error = Loc.T(_integer ? "mcfg.needInteger" : "mcfg.needNumber");
            Changed();
            return;
        }
        Error = "";
        Changed();
    }
}

/// <summary>Строка. Было null — пустое поле оставляет null.</summary>
public sealed partial class TextField : ConfigField
{
    private readonly JValue _token;
    private readonly bool _wasNull;

    public TextField(string key, int depth, JValue token, Action changed) : base(key, depth, changed)
    {
        _token = token;
        _wasNull = token.Type == JTokenType.Null;
        _value = _wasNull ? "" : token.ToString();
    }

    [ObservableProperty] private string _value;

    public string Hint => _wasNull ? "null" : "";

    partial void OnValueChanged(string value)
    {
        _token.Value = _wasNull && value.Length == 0 ? null : value;
        Changed();
    }
}

/// <summary>
/// Список (или что-то, для чего нет своего поля) — кусочком JSON: «[1, 2, 3]», «["a", "b"]». Проверяется при наборе.
/// </summary>
public sealed partial class JsonField : ConfigField
{
    private JToken _token;

    public JsonField(string key, int depth, JToken token, Action changed) : base(key, depth, changed)
    {
        _token = token;
        // короткие списки — в строку, длинные — как есть, с отступами
        var flat = token.ToString(Formatting.None);
        _value = flat.Length <= 80 ? flat.Replace(",", ", ") : token.ToString(Formatting.Indented);
    }

    [ObservableProperty] private string _value;

    public bool IsMultiline => Value.Contains('\n');

    partial void OnValueChanged(string value)
    {
        JToken fresh;
        try
        {
            fresh = Core.Profiles.ModConfigs.ParseJson(value);
        }
        catch (JsonReaderException)
        {
            Error = Loc.T("mcfg.badJsonValue");
            Changed();
            return;
        }
        _token.Replace(fresh);
        _token = fresh;
        Error = "";
        Changed();
    }
}

/// <summary>Дерево JSON → плоский список полей формы.</summary>
public static class ConfigForm
{
    public static List<ConfigField> Build(JToken root, Action changed)
    {
        var fields = new List<ConfigField>();
        if (root is JObject obj) AddObject(obj, 0, fields, changed);
        else fields.Add(Field(Loc.T("mcfg.rootValue"), 0, root, changed)); // файл — не объект, а список или значение
        return fields;
    }

    private static void AddObject(JObject obj, int depth, List<ConfigField> fields, Action changed)
    {
        foreach (var p in obj.Properties())
        {
            if (p.Value is JObject inner)
            {
                fields.Add(new GroupField(p.Name, depth));
                AddObject(inner, depth + 1, fields, changed);
            }
            else fields.Add(Field(p.Name, depth, p.Value, changed));
        }
    }

    private static ConfigField Field(string key, int depth, JToken token, Action changed) => token switch
    {
        JValue { Type: JTokenType.Boolean } v => new BoolField(key, depth, v, changed),
        JValue { Type: JTokenType.Integer or JTokenType.Float } v => new NumberField(key, depth, v, changed),
        JValue { Type: JTokenType.String or JTokenType.Null or JTokenType.Date or JTokenType.Guid or JTokenType.Uri } v
            => new TextField(key, depth, v, changed),
        _ => new JsonField(key, depth, token, changed),
    };
}
