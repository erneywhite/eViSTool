using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окна кода набора: dialog.modcode — «Вставить код» (пустое поле), dialog.modcode.share — готовый код с модами,
/// не вошедшими в него. Нажатий нет: окна только открываются.
/// </summary>
internal static class ModCode
{
    [SnapshotDialog("modcode")]
    private static void Open(MainViewModel main) => AppWindows.Current.AskModCode();

    [SnapshotDialog("modcode.share")]
    private static void Share(MainViewModel main) => AppWindows.Current.ShowModCode(
        new ModCodeShare("EV1-zxfSBEAoY3aWVZ5RgP9T8W1bVgsxEe4fnXHMTRpRZhuSLz3XyJBP6HqT", 16, ["My Local Mod 1.0.0", "Server Tweaks 0.3.1"]));
}
