namespace eViSTool.Core.Mods;

/// <summary>
/// Категория мода по тегам ModDB. Теги там свободные (около двухсот, от «Library» до «Unicorns»), поэтому не «первый
/// тег», а темы: каждая тема — свои теги. Темы проверяются по порядку, первая подошедшая и даёт категорию: библиотека
/// с тегом «Utility» — в библиотеки, а общие «QoL» и «Tweak» — последними. Не подошла ни одна — без предложения.
/// </summary>
public static class ModCategorySuggest
{
    /// <summary>Темы: ключ подписи категории (Lang) и теги ModDB, по которым мод в неё попадает.</summary>
    public static readonly IReadOnlyList<(string Key, string[] Tags)> Themes =
    [
        ("cat.libraries", ["Library", "Code", "Harmony Patching"]),
        ("cat.translations", ["Translation", "Language", "Korean", "Spanish"]),
        ("cat.map", ["Map", "Mapping", "Location", "Tracking"]),
        ("cat.worldgen", ["Worldgen", "Terrain", "Ore Gen", "Ore Generation", "Oregen", "Oreshape", "Cave Gen", "Structures", "Biodiversity", "Flora", "Foliage", "Ocean"]),
        ("cat.creatures", ["Animals", "Creatures", "Taming", "Pets", "Mounts", "Mount", "Horses", "Genetics", "Bosses", "Mob-Spawns", "Mob drops", "NPC", "NPCs", "Villagers", "Traders"]),
        ("cat.food", ["Food", "Farming", "Nutrition", "Nutrients", "Foraging", "Fruit Trees", "Drink", "Alcohol", "Firepit", "Oven"]),
        ("cat.crafting", ["Crafting", "Smithing", "Woodworking", "Metal", "Mining", "Panning", "Tools", "Ancient Tools", "Glass", "Resin", "Scrapping"]),
        ("cat.tech", ["Technology", "Automation", "Mechanic", "mechanics", "Chutes"]),
        ("cat.building", ["Building", "Construction", "Decoration", "Furniture", "Chisel", "Storage", "Veneer"]),
        ("cat.combat", ["Combat", "Weapons", "Armor", "Ranged", "Shields"]),
        ("cat.clothing", ["Clothing", "Cosmetics", "Fashion", "Playermodel"]),
        ("cat.travel", ["Exploration", "Travel & Exploration", "Transportation", "Boat", "Vehicle", "Teleport", "Airship", "Flight"]),
        ("cat.graphics", ["Graphics", "Shaders", "Visual", "Texture Pack", "Sound", "Audio", "Music", "Soundtrack", "Camera"]),
        ("cat.server", ["Server-Specific", "Claims", "Chat", "Economy"]),
        ("cat.interface", ["UI", "HUD", "User interface", "User Experience", "QoL", "Utility", "Ergonomic", "Accessibility", "Statistics"]),
        ("cat.fixes", ["Fix", "Hotfix", "Patch", "Compatibility", "Optimization", "Tweak"]),
    ];

    /// <summary>
    /// Ключ темы для мода с такими тегами; ни одна не подошла — null. Без подходящих тегов мод с «lib» в modid — в
    /// библиотеки: библиотеки часто без тегов вовсе (JSON Patches lib, CommonLib-Forked, Player Model lib).
    /// </summary>
    public static string? ThemeFor(IEnumerable<string?> tags, string? modId = null)
    {
        var set = new HashSet<string>(tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()), StringComparer.OrdinalIgnoreCase);
        var theme = set.Count == 0 ? null : Themes.FirstOrDefault(theme => theme.Tags.Any(set.Contains)).Key;
        return theme ?? (modId?.Contains("lib", StringComparison.OrdinalIgnoreCase) == true ? Themes[0].Key : null);
    }
}
