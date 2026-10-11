using eViSTool.Core.Server.Remote;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Код подключения к серверу на другом компьютере; null — закрыли без «ОК».</summary>
    public ConnectionCode? AskConnectionCode(string okText) =>
        new ConnectCodeWindow { DataContext = new ConnectCodeViewModel(okText) }.ShowModal<ConnectionCode>();
}
