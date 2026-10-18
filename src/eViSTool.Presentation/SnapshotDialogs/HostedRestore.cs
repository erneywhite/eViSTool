using eViSTool.Core.Hosting;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно «Восстановить мир» сервера у хостера: dialog.hostedrestore (своя копия мира с архивом настроек — галочка стоит)
/// и dialog.hostedrestore.manual (копия, положенная руками, без архива: предупреждение, галочка недоступна). Пути и
/// размеры выдуманные; окно только открывается, «Восстановить» на снимке никто не нажимает.
/// </summary>
internal static class HostedRestore
{
    private static readonly DateTime Made = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Local);

    [SnapshotDialog("hostedrestore")]
    private static void Open(MainViewModel main) =>
        AppWindows.Current.ConfirmHostedRestore(new HostedWorldBackup(@"C:\Backups\Survival-2026-10-18_09-00-00.vcdbs",
            "Survival-2026-10-18_09-00-00.vcdbs", Made, 94_371_840)
        {
            IsStamped = true, IsOwn = true, ExtraSize = 1_258_291, Origin = HostedWorldOrigin.World,
            SourcePath = "/Saves/MyVintageStoryWorld.vcdbs",
        }, "Survival");

    [SnapshotDialog("hostedrestore.manual")]
    private static void OpenManual(MainViewModel main) =>
        AppWindows.Current.ConfirmHostedRestore(new HostedWorldBackup(@"C:\Backups\old-world.vcdbs", "old-world.vcdbs", Made, 52_428_800),
            "Survival");
}
