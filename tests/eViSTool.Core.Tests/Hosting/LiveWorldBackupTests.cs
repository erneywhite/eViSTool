using System.Diagnostics;
using System.Security.Cryptography;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Hosting;
using Xunit.Abstractions;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Копия мира и её восстановление на настоящем сервере у хостера, по SFTP или FTP. Скачивает мир (сервер должен быть
/// остановлен), сверяет копию с миром на сервере по SHA-256, заливает её обратно и убирает за собой прежний мир (.bak).
/// Запускается только вручную, когда задан сервер — те же переменные, что у <see cref="LiveSftpTests"/> и
/// <see cref="LiveFtpTests"/>: EVISTOOL_LIVE_SFTP=хост:порт:логин (+ EVISTOOL_LIVE_SFTP_KEY или _PASSWORD,
/// EVISTOOL_LIVE_SFTP_HOSTKEY — отпечаток «SHA256:…», обязателен, EVISTOOL_LIVE_SFTP_ROOT) или EVISTOOL_LIVE_FTP=хост:порт:логин
/// (+ EVISTOOL_LIVE_FTP_PASSWORD). Мир на сервере после теста тот же самый (сверяется по SHA-256).
/// </summary>
public sealed class LiveWorldBackupTests(ITestOutputHelper output)
{
    [Fact]
    public Task Sftp_BackupAndRestore() => RunAsync(SftpConnection());

    [Fact]
    public Task Ftp_BackupAndRestore() => RunAsync(FtpConnection());

    private static HostedConnection? SftpConnection()
    {
        var spec = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP");
        var key = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP_KEY");
        var password = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP_PASSWORD");
        var hostKey = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP_HOSTKEY");
        if (string.IsNullOrEmpty(spec) || string.IsNullOrEmpty(hostKey) || password is null && string.IsNullOrEmpty(key)) return null;
        var parts = spec.Split(':', 3);
        return new HostedConnection
        {
            Protocol = HostedProtocol.Sftp, Host = parts[0], Port = int.Parse(parts[1]), User = parts[2],
            Password = string.IsNullOrEmpty(password) ? null : HostedSecret.Protect(password),
            KeyFile = string.IsNullOrEmpty(key) ? null : key, HostKeySha256 = hostKey,
            Root = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_SFTP_ROOT") is { Length: > 0 } root ? root : "/",
        };
    }

    private static HostedConnection? FtpConnection()
    {
        var spec = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_FTP");
        var password = Environment.GetEnvironmentVariable("EVISTOOL_LIVE_FTP_PASSWORD");
        if (string.IsNullOrEmpty(spec) || password is null) return null;
        var parts = spec.Split(':', 3);
        return new HostedConnection
        {
            Host = parts[0], Port = int.Parse(parts[1]), User = parts[2], Password = HostedSecret.Protect(password),
            Tls = Enum.TryParse<FtpTls>(Environment.GetEnvironmentVariable("EVISTOOL_LIVE_FTP_TLS"), out var tls) ? tls : FtpTls.Auto,
        };
    }

    private async Task RunAsync(HostedConnection? connection)
    {
        if (connection is null)
        {
            output.WriteLine("сервер не задан — живой тест пропущен.");
            return;
        }
        HostedTransports.Register();
        var clock = Stopwatch.StartNew();
        void Log(string text) => output.WriteLine($"[{clock.Elapsed.TotalSeconds,7:0.00} с] {text}");
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-live-backup-" + Guid.NewGuid().ToString("N"))).FullName;

        var endpoint = HostedEndpoint.From(connection)!;
        var check = await HostedProbe.CheckAsync(endpoint, connection, CancellationToken.None);
        connection = HostedProbe.Pin(connection, check.Layout);
        connection.BackupDir = dir;
        var profile = new GameProfile
        {
            Id = "live-" + Guid.NewGuid().ToString("N")[..8], Name = "Live backup", Kind = ProfileKind.Server,
            RemoteCode = GameProfile.HostedMarker, Hosted = connection,
        };
        var target = ModTarget.For(profile) ?? throw new InvalidOperationException("цель не собралась");
        var backups = new HostedWorldBackups(target);
        string? bak = null;
        try
        {
            var world = await backups.LocateAsync(CancellationToken.None);
            Log($"мир: {world.Path} ({world.FoundBy}), {world.Entry?.Size / 1024 / 1024} МБ");
            var before = await RemoteSha256Async(endpoint, world.Path);

            var lastShown = -1L;
            var progress = new Progress<HostedTransfer>(t =>
            {
                var mb = t.Done / (64L * 1024 * 1024);
                if (mb != lastShown || t.Done == t.Total) { lastShown = mb; Log($"  {t.Step}: {t.Done / 1024 / 1024} из {t.Total / 1024 / 1024} МБ"); }
            });
            var made = await backups.CreateAsync(serverStopped: true, progress, CancellationToken.None);
            var seconds = clock.Elapsed.TotalSeconds;
            Log($"копия: {made.Backup.Name}, {made.Backup.Size / 1024 / 1024} МБ, архив {made.Backup.ExtraSize / 1024} КБ");
            foreach (var w in made.Warnings) Log("  предупреждение: " + w);
            Assert.Equal(before, Sha256(made.Backup.Path)); // копия — тот же мир байт в байт

            var restored = await backups.RestoreAsync(made.Backup,
                new HostedRestoreOptions { ServerStopped = true, KeepOldWorld = true, Extras = HostedWorldExtras.ModConfig }, null,
                CancellationToken.None);
            bak = restored.KeptOldWorld;
            Log($"восстановлено: {restored.WorldPath}, прежний мир — {bak}, файлов настроек {restored.ExtraFiles}");
            foreach (var w in restored.Warnings) Log("  предупреждение: " + w);
            Assert.Equal(before, await RemoteSha256Async(endpoint, world.Path)); // на сервере тот же мир
            Log($"всего {clock.Elapsed.TotalSeconds:0.0} с (скачивание с архивом — до {seconds:0.0} с)");
        }
        finally
        {
            // прежний мир, оставленный восстановлением, — тот же самый: убрать, чтобы сервер не копил .bak от тестов
            if (bak is not null)
            {
                await using var files = RemoteFilesFactory.Create(endpoint);
                await files.ConnectAsync(CancellationToken.None);
                await files.DeleteFileAsync(bak, CancellationToken.None);
                Log("уборка: .bak удалён");
            }
            HostedSessions.Forget(profile.Id);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<string> RemoteSha256Async(HostedEndpoint endpoint, string path)
    {
        await using var files = RemoteFilesFactory.Create(endpoint);
        await files.ConnectAsync(CancellationToken.None);
        var local = Path.GetTempFileName();
        try
        {
            await using (var stream = File.Create(local))
                await files.DownloadAsync(path, stream, null, CancellationToken.None);
            return Sha256(local);
        }
        finally
        {
            File.Delete(local);
        }
    }

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
