using eViSTool.Core.Localization;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>Просмотр скриншотов мода (dialog.screenshots): три настоящих скриншота Primitive Survival с модбазы, открыт второй.</summary>
internal static class Screenshots
{
    // сеть нужна: окно показывает картинки по адресам (на снимок — -Wait 8)
    private static readonly string[] Urls =
    [
        "https://moddbcdn.vintagestory.at/ps1_0442d20e922b496642c3cca2d81b13bf.png",
        "https://moddbcdn.vintagestory.at/ps2_7f23e9c5c6d250dc4d5e3f6121529563.png",
        "https://moddbcdn.vintagestory.at/ps3_05ed9fd1e6372b3d50102b6d53564a79.png",
    ];

    [SnapshotDialog("screenshots")]
    private static void Open(MainViewModel main) =>
        AppWindows.Current.ShowScreenshots(Urls, 1, Loc.T("shots.title", "Primitive Survival"));

    /// <summary>Картинка не загрузилась (dialog.screenshots.failed): одна, по адресу, которого нет; сеть не нужна.</summary>
    [SnapshotDialog("screenshots.failed")]
    private static void OpenFailed(MainViewModel main) =>
        AppWindows.Current.ShowScreenshots(["https://screenshots.example.invalid/none.png"], 0, Loc.T("shots.title", "Primitive Survival"));
}
