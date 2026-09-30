using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Kind = eViSTool.Core.Server.Config.ConfigValueKind;

namespace eViSTool.Core.Server.Config;

/// <summary>
/// Значение поля ↔ текст в поле ввода. Списки и JSON отдаются с переводами строк «\n»; на входе принимаются и «\r\n».
/// «Пусто» в JSON — это токен с типом Null (не C#-null): таким его и возвращает разбор.
/// </summary>
public static class ConfigValueCodec
{
    private static readonly object?[] NoArgs = [];

    /// <summary>Текст для показа: StringList — по строке на элемент, Json — отформатированный JSON, null — пусто (для Json — «null»).</summary>
    public static string ToText(JToken? token, ConfigFieldSpec spec)
    {
        if (token is null || token.Type == JTokenType.Null) return spec.Kind == Kind.Json ? "null" : "";
        return spec.Kind switch
        {
            Kind.Json => Lf(token.ToString(Formatting.Indented)),
            Kind.StringList when token is JArray list => string.Join('\n', list.Select(ScalarText)),
            _ => ScalarText(token),
        };
    }

    /// <summary>
    /// Разобрать ввод. false + errorKey (ключ локализации «cfgerr.*») + errorArgs — если ввод не годится.
    /// Поле ReadOnly не разбирается никогда (cfgerr.readOnly) — для него есть вариант с исходным значением.
    /// </summary>
    public static bool TryParse(string? text, ConfigFieldSpec spec, out JToken? token, out string? errorKey, out object?[] errorArgs)
    {
        (token, errorKey, errorArgs) = Parse(text ?? "", spec);
        return errorKey is null;
    }

    /// <summary>
    /// То же, но с исходным значением поля: пока текст не тронут, возвращается исходный токен как был —
    /// даже если обычный разбор дал бы другое (null вместо "", элемент списка с пробелами) или ошибку.
    /// ReadOnly всегда возвращает исходное.
    /// </summary>
    public static bool TryParse(string? text, ConfigFieldSpec spec, JToken? original, out JToken? token, out string? errorKey, out object?[] errorArgs)
    {
        if (spec.Kind != Kind.ReadOnly && Lf(text ?? "") != Lf(ToText(original, spec)))
            return TryParse(text, spec, out token, out errorKey, out errorArgs);

        token = original?.DeepClone() ?? JValue.CreateNull();
        errorKey = null;
        errorArgs = NoArgs;
        return true;
    }

    /// <summary>Скаляр как записан: строка — как есть, bool — «true»/«false», числа — с точкой; объект или массив — сжатым JSON.</summary>
    internal static string ScalarText(JToken token) => token switch
    {
        JValue { Value: string s } => s,
        JValue { Value: bool b } => b ? "true" : "false",
        JValue { Value: IFormattable f } => f.ToString(null, CultureInfo.InvariantCulture),
        JValue v => v.Value?.ToString() ?? "",
        _ => token.ToString(Formatting.None),
    };

    private static (JToken? Token, string? ErrorKey, object?[] ErrorArgs) Parse(string text, ConfigFieldSpec spec)
    {
        switch (spec.Kind)
        {
            case Kind.ReadOnly:
                return Error("cfgerr.readOnly");

            case Kind.Text or Kind.Multiline or Kind.Path:
            {
                // поле ввода WPF вставляет «\r\n», сервер сам пишет «\n»; у пути пробелы по краям — всегда опечатка
                var value = spec.Kind switch { Kind.Multiline => Lf(text), Kind.Path => text.Trim(), _ => text };
                if (value.Length == 0 && spec.Nullable) return Ok(JValue.CreateNull());
                return spec.Required && string.IsNullOrWhiteSpace(value) ? Error("cfgerr.required") : Ok(new JValue(value));
            }

            case Kind.StringList:
            {
                var list = new JArray();
                foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                    list.Add(line);
                return list.Count == 0 && spec.Nullable ? Ok(JValue.CreateNull()) : Ok(list);
            }

            case Kind.Json:
                if (string.IsNullOrWhiteSpace(text)) return Ok(JValue.CreateNull());
                try { return Ok(ParseJson(text)); }
                catch (JsonException ex) { return Error("cfgerr.badJson", ex.Message); }

            case Kind.Choice:
                // вариант — ровно как в списке (в том числе пустой, если он там есть)
                if (text.Length == 0 && spec.Nullable) return Ok(JValue.CreateNull());
                if (spec.Choices.All(c => c.Value != text)) return Error("cfgerr.notChoice");
                if (!spec.NumericChoice) return Ok(new JValue(text));
                return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
                    ? Ok(new JValue(code))
                    : Error("cfgerr.notChoice");
        }

        // числа и переключатель: пробелы не значат ничего, пусто — null либо ошибка
        var input = text.Trim();
        if (input.Length == 0) return spec.Nullable ? Ok(JValue.CreateNull()) : Error("cfgerr.required");

        switch (spec.Kind)
        {
            case Kind.Integer:
                if (!long.TryParse(input, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                    return Error("cfgerr.notInteger");
                // все целые поля сервера — int: число больше он не прочитает и конфиг не загрузит
                return OutOfRange(whole, spec.Min ?? int.MinValue, spec.Max ?? int.MaxValue, spec) ?? Ok(new JValue(whole));

            case Kind.Decimal:
                if (!TryDecimal(input, out var number)) return Error("cfgerr.notNumber");
                // decimal пишется как есть и всегда с дробной частью («5» → 5.0): дробное поле остаётся дробным
                return OutOfRange(number, spec.Min, spec.Max, spec) ?? Ok(new JValue(number));

            default: // Bool
                if (input.Equals("true", StringComparison.OrdinalIgnoreCase)) return Ok(new JValue(true));
                if (input.Equals("false", StringComparison.OrdinalIgnoreCase)) return Ok(new JValue(false));
                return Error("cfgerr.notChoice");
        }
    }

    private static (JToken?, string?, object?[])? OutOfRange(decimal value, decimal? min, decimal? max, ConfigFieldSpec spec)
    {
        if ((min is null || value >= min) && (max is null || value <= max)) return null;
        if (spec.Min is not null && spec.Max is not null) return Error("cfgerr.range", spec.Min, spec.Max);
        return value < min ? Error("cfgerr.min", min) : Error("cfgerr.max", max);
    }

    /// <summary>Дробное число с точкой или запятой — как бы ни была настроена система.</summary>
    private static bool TryDecimal(string text, out decimal value)
    {
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        return decimal.TryParse(text, style, CultureInfo.InvariantCulture, out value)
               || decimal.TryParse(text, style, CultureInfo.CurrentCulture, out value)
               || decimal.TryParse(text.Replace(',', '.'), style, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Одно JSON-значение, прочитанное как читается весь конфиг: строки не становятся датами, дроби — decimal.</summary>
    private static JToken ParseJson(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal,
        };
        var token = JToken.ReadFrom(reader);
        while (reader.Read()) { } // лишнее после значения — ошибка читателя
        return token;
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n");

    private static (JToken?, string?, object?[]) Ok(JToken token) => (token, null, NoArgs);

    private static (JToken?, string?, object?[]) Error(string key, params object?[] args) => (null, key, args);
}
