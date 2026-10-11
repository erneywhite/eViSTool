using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>
    /// «Уборка диска» профиля: модально над главным окном, ответа нет (как ShowDialog в окне на WPF). Главного окна на
    /// экране нет — окно не открывается, как и в окне на WPF.
    /// </summary>
    public void ShowCleanup(GameProfile profile)
    {
        if (AvaloniaUiPlatform.MainWindow is null) return;
        new CleanupWindow { DataContext = new CleanupViewModel(profile) }.ShowModal<bool>();
    }
}
