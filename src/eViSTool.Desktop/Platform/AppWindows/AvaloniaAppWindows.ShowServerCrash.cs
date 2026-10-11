using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Падение сервера профиля: не модально, поверх всех окон и сразу впереди, как вылет игры.</summary>
    public void ShowServerCrash(MainViewModel main, GameProfile profile, ServerCrashInfo crash) =>
        CrashWindow.Open(CrashWindowViewModel.ForServer(main, profile, crash));
}
