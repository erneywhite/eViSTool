using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>Окно сборки модпака (dialog.exportpack): профиль с настройками модов; без них — dialog.exportpack.noconfig.</summary>
internal static class ExportPack
{
    [SnapshotDialog("exportpack")]
    private static void Open(MainViewModel main) => AppWindows.Current.ExportPack("Выживание с друзьями", 24, 3, true);

    // у профиля нет папки ModConfig: галочка настроек модов выключена
    [SnapshotDialog("exportpack.noconfig")]
    private static void OpenWithoutConfig(MainViewModel main) => AppWindows.Current.ExportPack("Выживание с друзьями", 24, 0, false);
}
