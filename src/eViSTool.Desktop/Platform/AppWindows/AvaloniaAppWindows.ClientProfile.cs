using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    public GameProfile? ClientProfile(GameProfile source, bool clone, string suggestedName) =>
        new ClientProfileWindow { DataContext = new ClientProfileViewModel(source, clone, suggestedName) }.ShowModal<GameProfile>();
}
