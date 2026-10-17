using eViSTool.Core.Hosting;

namespace eViSTool.Desktop.Platform;

partial class AvaloniaAppWindows
{
    // shortcut: окна подключения ещё нет — всегда null, как будто закрыли без «ОК»; само окно — пакет P5
    /// <summary>Подключение к серверу у хостера по FTP; null — закрыли без «ОК».</summary>
    public HostedConnection? AskHostedConnection(HostedConnection? existing, string okText) => null;
}
