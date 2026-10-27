using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>«Разложить по категориям» по тегам ModDB (dialog.categories).</summary>
internal static class CategoryProposals
{
    [SnapshotDialog("categories")]
    private static void Open(MainViewModel main) => AppWindows.Current.ConfirmCategories(
    [
        new("Библиотеки", ["XLib (fork)", "Common Lib (forked)", "Overhaul Lib", "JSON Patches Lib", "VSImGui", "CarryOn Lib"]),
        new("Животные и существа", ["Bovinae", "Caninae", "Capreolinae", "Cervinae", "Elephantidae", "Felinae", "Pantherinae"]),
        new("Карта", ["GiMap Core", "GiMap Caves & Ores", "Egocarib Auto Map Markers", "Waypoint Together Reborn"]),
        new("Еда и фермерство", ["Farming Revamped", "Bushmeat Stew", "Better Firepit"]),
    ]);
}
