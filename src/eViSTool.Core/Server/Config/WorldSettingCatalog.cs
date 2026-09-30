namespace eViSTool.Core.Server.Config;

/// <summary>Чем правится значение настройки мира.</summary>
public enum WorldSettingType { Text, Bool, Number, Choice }

/// <summary>
/// Настройка мира, которую знает базовая игра (ключ словаря WorldConfig.WorldConfiguration).
/// Values — допустимые значения для Choice (для Bool — "true"/"false"), Default — значение по умолчанию.
/// </summary>
public sealed record WorldSettingInfo(string Key, WorldSettingType Type, IReadOnlyList<string> Values, string? Default,
    string Category, bool OnlyAtWorldCreation)
{
    public string LabelKey => "ws." + Key;

    /// <summary>Подсказка; в словаре её может не быть.</summary>
    public string HintKey => LabelKey + ".hint";

    public string CategoryKey => "ws.category." + Category;

    /// <summary>Подпись значения Choice; в словаре её может не быть — тогда показывать само значение.</summary>
    public string ValueLabelKey(string value) => $"ws.{Key}.value.{value}";
}

/// <summary>Известные настройки мира. ЗАГЛУШКА: список наполняется по данным исследования игры.</summary>
public static class WorldSettingCatalog
{
    public static IReadOnlyList<WorldSettingInfo> All { get; } = [];

    /// <summary>Настройка по ключу; игра сравнивает ключи без учёта регистра.</summary>
    public static WorldSettingInfo? Find(string key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
}
