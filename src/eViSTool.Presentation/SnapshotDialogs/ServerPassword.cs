using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.SnapshotDialogs;

/// <summary>
/// Окно пароля сервера (dialog.serverpassword): поле пустое, как при открытии.
/// Имя сервера выдуманное.
/// </summary>
internal static class ServerPassword
{
    [SnapshotDialog("serverpassword")]
    private static void Open(MainViewModel main) => AppWindows.Current.AskServerPassword("Деревня у реки");
}
