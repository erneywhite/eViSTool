using eViSTool.Core.Diagnostics;
using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>
    /// «Ошибки модов» профиля: не модально и не поверх всех окон, с владельцем — главным окном (из него и открывают, по
    /// «!» у профиля), как Show с Owner в окне на WPF.
    /// </summary>
    public void ShowModErrors(MainViewModel main, GameProfile profile, ModErrorReport report) =>
        new ModErrorsWindow { DataContext = ModErrorsViewModel.For(main, profile, report) }.ShowModeless();
}
