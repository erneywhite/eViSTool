using eViSTool.Core.Diagnostics;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Ошибки модов»: три шумных мода, один из них не нашёлся среди установленных — его не выключить, и ошибки без
/// признаков мода (dialog.moderrors); модов много — список прокручивается, окно не растёт выше предела
/// (dialog.moderrors.many). Отчёты выдуманные, логи пользователя не читаются; пути — к модам стенда (или выдуманные
/// рядом с ними). Окно только открывается: «Выключить мод» и «Скрыть» на снимке никто не нажимает.
/// </summary>
internal static class ModErrors
{
    private static readonly DateTime At = new(2026, 10, 9, 21, 14, 0);

    [SnapshotDialog("moderrors")]
    private static void Open(MainViewModel main)
    {
        var profile = Profile(main);
        var report = new ModErrorReport
        {
            At = At,
            Total = 58,
            Unattributed = 5,
            Mods =
            [
                new("primitivesurvival", "Primitive Survival", "3.7.6", ModPath(profile, "primitivesurvival_3.7.6.zip"), 37,
                    "Exception: System.NullReferenceException: Object reference not set to an instance of an object.", CulpritSource.Stack),
                new("carryon", "Carry On", "1.8.0", ModPath(profile, "carryon_1.8.0.zip"), 12,
                    "[carryon] Failed to carry block game:chest-east: no carry behavior", CulpritSource.Message),
                // строка перевода мода, которого у профиля уже нет: выключать нечего
                new("expandedfoods", "expandedfoods", "1.6.2", null, 4,
                    "Lang key not found: expandedfoods:item-foodportion-vegetable — \"expandedfoods:item-foodportion-vegetable\"",
                    CulpritSource.Translation),
            ],
        };
        AppWindows.Current.ShowModErrors(main, profile, report);
    }

    [SnapshotDialog("moderrors.many")]
    private static void OpenMany(MainViewModel main)
    {
        var profile = Profile(main);
        (string Id, string Name, string Version, int Count, string Example)[] mods =
        [
            ("primitivesurvival", "Primitive Survival", "3.7.6", 214, "Exception: System.NullReferenceException: Object reference not set to an instance of an object."),
            ("carryon", "Carry On", "1.8.0", 96, "[carryon] Failed to carry block game:chest-east: no carry behavior"),
            ("butchering", "Butchering", "1.9.4", 41, "Exception: System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection."),
            ("hudclock", "HUD Clock", "3.2.1", 30, "[hudclock] Could not load texture hudclock:textures/hud/clockface.png"),
            ("tradeomat", "Trade-O-Mat", "2.4.0", 17, "Exception: System.InvalidOperationException: Collection was modified; enumeration operation may not execute."),
            ("smithingplus", "Smithing Plus", "1.8.3", 12, "[smithingplus] Recipe smithingplus:recipes/smithing/helve.json has no output"),
            ("bettertraders", "Better Traders", "0.0.12", 9, "Exception: System.Collections.Generic.KeyNotFoundException: The given key 'game:gear-rusty' was not present in the dictionary."),
            ("toolsmith", "Toolsmith", "1.2.9", 7, "[toolsmith] Patch toolsmith:patches/tools.json: path /attributes/handle not found"),
            ("latecomer", "Latecomer", "2.0.0", 5, "[latecomer] Player state not found for uid 7f3a9c"),
            ("vsimgui", "VS ImGui", "1.1.11", 3, "Exception: System.DllNotFoundException: Unable to load DLL 'cimgui'"),
        ];
        var report = new ModErrorReport
        {
            At = At,
            Total = mods.Sum(m => m.Count),
            Mods = [.. mods.Select(m => new ModErrorLine(m.Id, m.Name, m.Version, ModPath(profile, $"{m.Id}_{m.Version}.zip"), m.Count, m.Example,
                CulpritSource.Stack))],
        };
        AppWindows.Current.ShowModErrors(main, profile, report);
    }

    /// <summary>Профиль стенда («Стенд», папка данных — во временной папке стенда).</summary>
    private static GameProfile Profile(MainViewModel main) =>
        main.ActiveProfile?.Model ?? throw new InvalidOperationException("У стенда нет активного профиля");

    private static string ModPath(GameProfile profile, string file) => Path.Combine(profile.DataDir ?? "", "Mods", file);
}
