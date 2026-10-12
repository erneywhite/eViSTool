using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>
    /// «Уборка диска» профиля: модально над главным окном, ответа нет. Главного окна на
    /// экране нет — окно не открывается.
    /// </summary>
    public void ShowCleanup(GameProfile profile)
    {
        if (AvaloniaUiPlatform.MainWindow is null) return;
        new CleanupWindow { DataContext = new CleanupViewModel(profile) }.ShowModal<bool>();
    }
}
