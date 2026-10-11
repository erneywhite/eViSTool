using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>Окно бана игрока (dialog.ban).</summary>
internal static class Ban
{
    [SnapshotDialog("ban")]
    private static void Open(MainViewModel main) => AppWindows.Current.Ban("Вася");
}
