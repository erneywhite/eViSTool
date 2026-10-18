using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Hosting;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Копии мира через настоящий адаптер FluentFTP на <see cref="MiniFtpServer"/>: «как G-Portal» (передача, оборванная
/// сервером посередине, закрывает и управляющее соединение; поверх не переименовывает) и «как vsftpd» (обрыв — «426»,
/// листинг LIST до минуты).
/// </summary>
public sealed class HostedWorldBackupsFtpTests : IDisposable
{
    private const string World = "/Saves/MyVintageStoryWorld.vcdbs";

    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-hwb-" + Guid.NewGuid().ToString("N"))).FullName;

    public static TheoryData<string> Modes => ["gportal", "vsftpd"];

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string[] BigRows(string tag) => [.. Enumerable.Range(0, 60).Select(i => tag + i + new string('y', 8000))];

    private static void PutWorld(MiniFtpServer server, params string[] rows)
    {
        var local = server.Local(World);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.Delete(local);
        SqliteWorld.WriteClean(local, rows);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Create_TransferCutByTheServer_ResumesFromThePosition(string mode)
    {
        var options = mode == "gportal" ? MiniFtpOptions.GPortal() : MiniFtpOptions.Vsftpd();
        await using var server = new MiniFtpServer(options);
        server.Put("/serverconfig.json", File.ReadAllBytes(HostedFixture.Sample("gportal-serverconfig-after-start.json")));
        PutWorld(server, BigRows("w"));
        options.Cut("MyVintageStoryWorld.vcdbs", 120_000);
        var (backups, forget) = Connect(server, mode);
        try
        {
            var result = await backups.CreateAsync(serverStopped: true, null, default);

            Assert.Equal(BigRows("w"), SqliteWorld.Read(result.Backup.Path));
            Assert.Contains(server.Commands, c => c.StartsWith("REST ", StringComparison.Ordinal) && c != "REST 0");
            if (mode == "gportal") Assert.True(server.Sessions >= 2); // обрыв закрыл соединение — докачка на новом
            Assert.DoesNotContain(Directory.GetFiles(backups.Dir), f => f.EndsWith(".part"));
        }
        finally
        {
            forget();
        }
    }

    [Fact]
    public async Task Create_ServerWithoutRest_CutTransfer_StartsOver()
    {
        var options = MiniFtpOptions.Vsftpd();
        options.Rest = false;
        await using var server = new MiniFtpServer(options);
        server.Put("/serverconfig.json", File.ReadAllBytes(HostedFixture.Sample("gportal-serverconfig-after-start.json")));
        PutWorld(server, BigRows("n"));
        options.Cut("MyVintageStoryWorld.vcdbs", 120_000);
        var (backups, forget) = Connect(server, "norest");
        try
        {
            var result = await backups.CreateAsync(serverStopped: true, null, default);

            Assert.Equal(BigRows("n"), SqliteWorld.Read(result.Backup.Path));
            Assert.True(server.Commands.Count(c => c.StartsWith("RETR ", StringComparison.Ordinal) && c.Contains("MyVintageStoryWorld")) >= 2);
        }
        finally
        {
            forget();
        }
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task Restore_PutsTheWorldInPlace_EvenWhereRenameOverIsRefused(string mode)
    {
        var options = mode == "gportal" ? MiniFtpOptions.GPortal() : MiniFtpOptions.Vsftpd();
        await using var server = new MiniFtpServer(options);
        server.Put("/serverconfig.json", File.ReadAllBytes(HostedFixture.Sample("gportal-serverconfig-after-start.json")));
        PutWorld(server, "v1");
        var (backups, forget) = Connect(server, mode);
        try
        {
            var copy = (await backups.CreateAsync(serverStopped: true, null, default)).Backup;
            PutWorld(server, "v2");

            var result = await backups.RestoreAsync(copy, new HostedRestoreOptions { ServerStopped = true }, null, default);

            Assert.Equal(World, result.WorldPath);
            Assert.Equal(["v1"], SqliteWorld.Read(server.Local(World)));
            Assert.Equal(["MyVintageStoryWorld.vcdbs"], Directory.GetFiles(Path.GetDirectoryName(server.Local(World))!).Select(Path.GetFileName));
        }
        finally
        {
            forget();
        }
    }

    /// <summary>Профиль у хостера, чьи соединения идут на MiniFtpServer через адаптер FluentFTP; копии — во временной папке.</summary>
    private (HostedWorldBackups Backups, Action Forget) Connect(MiniFtpServer server, string mode)
    {
        var host = "hwb-" + Guid.NewGuid().ToString("N")[..12] + ".test";
        var direct = new HostedEndpoint(HostedProtocol.Ftp, "127.0.0.1", server.Port, FtpTls.None, server.Options.User, server.Options.Password,
            false, null) { Timeout = TimeSpan.FromSeconds(5) };
        var registration = RemoteFilesFactory.RegisterHost(host, _ => new FluentFtpFiles(direct));
        var profile = new GameProfile
        {
            Id = "hwb-" + Guid.NewGuid().ToString("N"),
            Name = "Hosted " + mode,
            Kind = ProfileKind.Server,
            RemoteCode = GameProfile.HostedMarker,
            Hosted = new HostedConnection
            {
                Host = host, Port = server.Port, User = server.Options.User, Password = HostedSecret.Protect(server.Options.Password),
                Tls = FtpTls.None, BackupDir = Path.Combine(_dir, mode),
            },
        };
        var backups = new HostedWorldBackups(ModTarget.For(profile) ?? throw new InvalidOperationException("no target")) { FreeSpace = _ => null };
        return (backups, () =>
        {
            HostedSessions.Forget(profile.Id);
            registration.Dispose();
            foreach (var dir in new[] { HostedJournal.DirFor(profile.Id), ModInfoCache.DirFor(profile.Id) })
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
        });
    }
}
