using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace eViSTool.Core.Settings;

/// <summary>
/// Перечисление в настройках строкой, как <see cref="StringEnumConverter"/>, но незнакомое значение не бросает
/// исключение. Обычный конвертер на строке из более новой версии бросает <see cref="JsonSerializationException"/>,
/// <see cref="SettingsStore.Load"/> принимает весь файл за битый и начинает с чистых настроек — все профили пропадают.
/// Здесь незнакомое значение (строка, число вне перечисления, объект) превращается в значение свойства по умолчанию:
/// то, что задано инициализатором, а если его нет — в default.
/// </summary>
public sealed class TolerantEnumConverter : StringEnumConverter
{
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var nullable = Nullable.GetUnderlyingType(objectType);
        var enumType = nullable ?? objectType;

        // объект или массив вместо значения: пропустить целиком, иначе чтение файла собьётся на его середине
        if (reader.TokenType is JsonToken.StartObject or JsonToken.StartArray)
        {
            reader.Skip();
            return Fallback(enumType, nullable is not null, existingValue);
        }
        if (reader.TokenType == JsonToken.Null)
            return nullable is not null ? null : Fallback(enumType, false, existingValue);

        object? value;
        try
        {
            value = base.ReadJson(reader, objectType, existingValue, serializer);
        }
        catch (JsonSerializationException)
        {
            return Fallback(enumType, nullable is not null, existingValue);
        }
        // число читается и без имени: 42 стало бы значением вне перечисления
        return value is not null && !Enum.IsDefined(enumType, value) ? Fallback(enumType, nullable is not null, existingValue) : value;
    }

    private static object? Fallback(Type enumType, bool nullable, object? existingValue)
    {
        if (existingValue is not null && existingValue.GetType() == enumType && Enum.IsDefined(enumType, existingValue))
            return existingValue;
        return nullable ? null : Activator.CreateInstance(enumType);
    }
}
