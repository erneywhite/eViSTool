using eViSTool.Core.Diagnostics;
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>
    /// Вылет игры профиля: не модально (игра, если закрылся только мир, открыта, и программа остаются доступны),
    /// поверх всех окон и сразу впереди.
    /// </summary>
    public void ShowGameCrash(MainViewModel main, GameProfile profile, CrashFinding finding) =>
        CrashWindow.Open(CrashWindowViewModel.ForGame(main, profile, finding));
}
