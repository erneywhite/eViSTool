using eViSTool.Core.Profiles;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Клон серверного профиля (importName не null — импорт существующих данных); готовый профиль или null, если не копировали.</summary>
    public GameProfile? CloneServerProfile(GameProfile source, bool serverRunning, string? importName = null) =>
        new CloneProfileWindow { DataContext = new CloneProfileViewModel(source, serverRunning, importName) }.ShowModal<GameProfile>();
}
