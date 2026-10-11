using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>Окно выбора версии мода (dialog.rollback): сохранённые копии и релизы модбазы.</summary>
internal static class Rollback
{
    [SnapshotDialog("rollback")]
    private static void Open(MainViewModel main) =>
        AppWindows.Current.PickVersion(Loc.T("rollback.heading", "Better Ruins", "0.6.0"), Sample());

    /// <summary>Состояние «версий нет» (dialog.rollback.empty).</summary>
    [SnapshotDialog("rollback.empty")]
    private static void OpenEmpty(MainViewModel main) =>
        AppWindows.Current.PickVersion(Loc.T("rollback.heading", "Better Ruins", "0.6.0"), []);

    private static List<VersionOption> Sample()
    {
        var copy = Loc.T("rollback.savedCopy");
        var installed = Loc.T("rollback.installed");
        return
        [
            new("0.5.3", copy, Dates.Stamp(new DateTime(2026, 10, 9, 20, 37, 0)), "", @"C:\Backups\betterruins_0.5.3.zip", null),
            new("0.5.2", copy, Dates.Stamp(new DateTime(2026, 9, 21, 18, 4, 0)), "", @"C:\Backups\betterruins_0.5.2.zip", null),
            new("0.6.0", "ModDB", Dates.Day(new DateTime(2026, 10, 2)), installed, null, null),
            new("0.5.3", "ModDB", Dates.Day(new DateTime(2026, 8, 17)), "", null, null),
            new("0.5.2", "ModDB", Dates.Day(new DateTime(2026, 7, 30)), "", null, null),
            new("0.5.0", "ModDB", Dates.Day(new DateTime(2026, 6, 11)), "", null, null),
        ];
    }
}
