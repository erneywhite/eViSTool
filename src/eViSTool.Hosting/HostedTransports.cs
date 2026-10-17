using eViSTool.Core.Hosting;

namespace eViSTool.Hosting;

/// <summary>
/// Подключает транспорты серверов у хостера к <see cref="RemoteFilesFactory"/>. Вызывается один раз при запуске окна:
/// Core про FluentFTP не знает, а агент эту сборку не подключает вовсе — FluentFTP в его exe не попадает.
/// </summary>
public static class HostedTransports
{
    public static void Register()
    {
        RemoteFilesFactory.Register(HostedProtocol.Ftp, endpoint => new FluentFtpFiles(endpoint));
        // SFTP — следующим выпуском; до тех пор фабрика отвечает RemoteFailure.Unsupported
    }
}
