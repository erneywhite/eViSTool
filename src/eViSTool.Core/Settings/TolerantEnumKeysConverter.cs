using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Settings;

/// <summary>
/// Словарь с ключами-перечислением: ключ, которого эта версия не знает (его записала более новая), пропускается.
/// Ключи словаря Newtonsoft разбирает мимо <see cref="TolerantEnumConverter"/>, и незнакомый ключ ронял чтение всего
/// файла. Запись — как обычно, именами.
/// </summary>
public sealed class TolerantEnumKeysConverter<TEnum, TValue> : JsonConverter<Dictionary<TEnum, TValue>>
    where TEnum : struct, Enum
{
    public override Dictionary<TEnum, TValue>? ReadJson(JsonReader reader, Type objectType, Dictionary<TEnum, TValue>? existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        var result = new Dictionary<TEnum, TValue>();
        if (reader.TokenType == JsonToken.Null) return result;
        if (reader.TokenType != JsonToken.StartObject)
        {
            reader.Skip();
            return result;
        }
        foreach (var prop in JObject.Load(reader).Properties())
        {
            // только имя: число в ключе («42») стало бы значением вне перечисления
            if (!Enum.TryParse<TEnum>(prop.Name, ignoreCase: true, out var key) || !Enum.IsDefined(key)
                || int.TryParse(prop.Name, out _)) continue;
            try
            {
                if (prop.Value.ToObject<TValue>(serializer) is { } value) result[key] = value;
            }
            catch (JsonException)
            {
                // значение не того вида — пропустить этот ключ, а не весь файл
            }
        }
        return result;
    }

    public override void WriteJson(JsonWriter writer, Dictionary<TEnum, TValue>? value, JsonSerializer serializer)
    {
        writer.WriteStartObject();
        foreach (var (key, item) in value ?? [])
        {
            writer.WritePropertyName(key.ToString());
            serializer.Serialize(writer, item);
        }
        writer.WriteEndObject();
    }
}
