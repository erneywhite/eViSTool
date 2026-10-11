using eViSTool.Core.Server;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    public string? PickRemoteFolder(IServerData data, string? start, string heading) =>
        new RemoteFolderWindow { DataContext = new RemoteFolderViewModel(data, start, heading) }.ShowModal<string>();
}
