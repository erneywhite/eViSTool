using eViSTool.Core.Server;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>«Команды сервера»: выбранная команда или null (закрыли без выбора).</summary>
    public ServerCommand? PickServerCommand(ServerViewModel server)
    {
        using var model = new ServerCommandsViewModel(server); // слушает сервер, пока окно открыто
        return new ServerCommandsWindow { DataContext = model }.ShowModal<ServerCommand>();
    }
}
