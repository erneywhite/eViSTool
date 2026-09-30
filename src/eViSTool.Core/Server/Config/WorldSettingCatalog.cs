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

/// <summary>
/// Настройки мира базовой игры (данные — в WorldSettingCatalog.Data.cs). Сервер берёт из словаря только ключи,
/// объявленные модами как настройки мира, остальные молча пропускает: ключ, которого здесь нет, — либо от стороннего мода,
/// либо опечатка. Сам словарь читается один раз, при создании мира; в готовом мире настройки меняет команда /worldconfig.
/// </summary>
public static partial class WorldSettingCatalog
{
    /// <summary>Все настройки: по категориям, внутри категории — в порядке игры.</summary>
    public static IReadOnlyList<WorldSettingInfo> All { get; } = CreateAll();

    /// <summary>Категории в порядке показа (<see cref="WorldSettingInfo.Category"/>).</summary>
    public static IReadOnlyList<string> Categories { get; } = CreateCategories();

    private static readonly Dictionary<string, WorldSettingInfo> ByKey = All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Presets = CreatePresets();

    private static readonly IReadOnlyDictionary<string, string> NoPreset = new Dictionary<string, string>();

    /// <summary>Настройка по ключу; игра сравнивает ключи без учёта регистра.</summary>
    public static WorldSettingInfo? Find(string key) => key is null ? null : ByKey.GetValueOrDefault(key);

    /// <summary>
    /// Значения, которые задаёт стиль игры (WorldConfig.PlayStyle) поверх умолчаний: ключ → значение, ключи без учёта
    /// регистра. Среди них бывают ключи, которых нет в каталоге. Незнакомый стиль (от мода) — пустой словарь.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PresetDefaults(string? playStyle) =>
        playStyle is not null && Presets.TryGetValue(playStyle, out var preset) ? preset : NoPreset;

    /// <summary>
    /// Значение, с которым мир будет создан, если настройку не задавать: то, что ставит стиль игры, иначе умолчание
    /// самой настройки. null — про такой ключ ничего не известно.
    /// </summary>
    public static string? DefaultFor(string key, string? playStyle) =>
        key is not null && PresetDefaults(playStyle).TryGetValue(key, out var value) ? value : Find(key!)?.Default;

    private static WorldSettingInfo Choice(string key, string category, string @default, string[] values, bool once = false) =>
        new(key, WorldSettingType.Choice, values, @default, category, once);

    private static WorldSettingInfo Bool(string key, string category, bool @default, bool once = false) =>
        new(key, WorldSettingType.Bool, ["true", "false"], @default ? "true" : "false", category, once);

    private static IReadOnlyDictionary<string, string> Preset(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
}
