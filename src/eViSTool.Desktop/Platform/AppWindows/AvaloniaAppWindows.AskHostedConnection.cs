using eViSTool.Core.Hosting;
using eViSTool.Desktop.Dialogs;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    /// <summary>Подключение к серверу у хостера по FTP; null — закрыли без «ОК». Пароль в ответе уже зашифрован.</summary>
    public HostedConnection? AskHostedConnection(HostedConnection? existing, string okText) =>
        new HostedConnectWindow { DataContext = new HostedConnectViewModel(existing, okText) }.ShowModal<HostedConnection>();
}
