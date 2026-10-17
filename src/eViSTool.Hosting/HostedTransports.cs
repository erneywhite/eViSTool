using eViSTool.Core.Hosting;

namespace eViSTool.Hosting;

/// <summary>
/// Подключает транспорты серверов у хостера к <see cref="RemoteFilesFactory"/>. Вызывается один раз при запуске окна:
/// Core про FluentFTP и SSH.NET не знает, а агент эту сборку не подключает вовсе — ни то ни другое в его exe не попадает.
/// </summary>
public static class HostedTransports
{
    public static void Register()
    {
        RemoteFilesFactory.Register(HostedProtocol.Ftp, endpoint => new FluentFtpFiles(endpoint));
        RemoteFilesFactory.Register(HostedProtocol.Sftp, endpoint => new SshNetFiles(endpoint));
    }
}
