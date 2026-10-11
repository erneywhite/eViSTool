using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    public NewProfileChoice? NewProfile(ProfileKind kind, string suggestedName) =>
        new NewProfileWindow { DataContext = new NewProfileViewModel(kind, suggestedName) }.ShowModal<NewProfileChoice>();
}
