using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    public RoleCopyChoice? DuplicateRole(string sourceCode, string sourceName, IReadOnlyList<string> takenCodes) =>
        new DuplicateRoleWindow { DataContext = new DuplicateRoleViewModel(sourceCode, sourceName, takenCodes) }.ShowModal<RoleCopyChoice>();
}
