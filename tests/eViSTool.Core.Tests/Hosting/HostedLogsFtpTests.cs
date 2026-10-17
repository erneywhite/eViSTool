using System.Globalization;
using System.Text;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Hosting;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Лог сервера у хостера через настоящий адаптер FluentFTP на <see cref="MiniFtpServer"/>: «как G-Portal» (MLSD в UTC,
/// REST, прерванная передача убивает соединение) и «как vsftpd» (LIST со временем сервера до минуты).
/// </summary>
public sealed class HostedLogsFtpTests
{
    public static TheoryData<string> Modes => ["gportal", "vsftpd"];

    // время в строках — по часам сервера; у MiniFtpServer они идут по UTC (LIST тоже отдаёт UTC)
    private static readonly DateTime Start = DateTime.UtcNow.AddMinutes(-10);

    private static string Line(int second, string level, string text) =>
        $"{Start.AddSeconds(second).ToString("d.M.yyyy HH:mm:ss", CultureInfo.InvariantCulture)} [{level}] {text}\n";

    private static void Append(MiniFtpServer server, string path, string text) => File.AppendAllText(server.Local(path), text, new UTF8Encoding(false));

    [Theory, MemberData(nameof(Modes))]
    public async Task Poll_FollowsTheRun_OnOneConnection(string mode)
    {
        await using var server = new MiniFtpServer(mode == "gportal" ? MiniFtpOptions.GPortal() : MiniFtpOptions.Vsftpd());
        var (logs, forget) = Connect(server);
        try
        {
            server.Put("/Logs/server-main.log", Encoding.UTF8.GetBytes(
                Line(0, "Notification", "Server logger started.") + Line(1, "Event", "Launching server...")));
            Assert.Equal(HostedServerState.Starting, (await logs.PollAsync(default)).State);

            Append(server, "/Logs/server-main.log",
                Line(20, "Event", "Dedicated Server now running on Port 29600 and ip 198.51.100.10!")
                + Line(21, "Notification", "Client 1 uid TestPlayerUid0000000000= attempting identification. Name: Erney")
                + Line(25, "Event", "Erney 203.0.113.7:50643 joins."));
            var running = await logs.PollAsync(default);
            Assert.Equal(HostedServerState.Running, running.State);
            Assert.Equal("Erney", Assert.Single(running.Players).Name);
            Assert.Equal(3, running.NewLines.Count);

            Append(server, "/Logs/server-main.log",
                Line(60, "Notification", "Server termination event SIGTERM received. Shutting down server")
                + Line(61, "Notification", "Server stop requested, begin shutdown sequence. Stop reason: External close event SIGTERM received")
                + Line(70, "Event", "Stopped the server!"));
            var stopped = await logs.PollAsync(default);
            Assert.Equal(HostedServerState.Stopped, stopped.State);
            Assert.Empty(stopped.Players);

            // хвост каждый раз дочитан до конца файла: соединение не рвалось, всё — одним
            Assert.Equal(1, server.Sessions);
            if (mode == "gportal") Assert.True(server.Count("REST") >= 2);

            // запуск заново: лог начат с нуля и короче прежнего
            server.Put("/Logs/server-main.log", Encoding.UTF8.GetBytes(Line(120, "Notification", "Server logger started.")));
            var again = await logs.PollAsync(default);
            Assert.True(again.Restarted);
            Assert.Equal(HostedServerState.Starting, again.State);
            Assert.Single(again.NewLines);
        }
        finally
        {
            forget();
        }
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task FreshCrashLog_IsSeen_WithUtcAndWithServerTime(string mode)
    {
        await using var server = new MiniFtpServer(mode == "gportal" ? MiniFtpOptions.GPortal() : MiniFtpOptions.Vsftpd());
        var (logs, forget) = Connect(server);
        try
        {
            server.Put("/Logs/server-main.log", Encoding.UTF8.GetBytes(
                Line(0, "Notification", "Server logger started.") + Line(20, "Event", "Dedicated Server now running on Port 29600!")),
                Start.AddSeconds(20));
            // прошлый вылет: до начала этого запуска
            server.Put("/Logs/server-crash.log", "old crash"u8.ToArray(), Start.AddHours(-3));
            Assert.Equal(HostedServerState.Running, (await logs.PollAsync(default)).State);

            server.Put("/Logs/server-crash.log", "Critical error occurred in the following mod: badmod@1.0.0\n"u8.ToArray(), Start.AddMinutes(2));
            Assert.Equal(HostedServerState.Crashed, (await logs.PollAsync(default)).State);

            var crash = await logs.FindCrashAsync(default, []);
            Assert.NotNull(crash);
            Assert.Equal("badmod", crash.Info.ModId);
            Assert.Contains("badmod@1.0.0", crash.Report);
        }
        finally
        {
            forget();
        }
    }

    /// <summary>Профиль у хостера, чьи соединения идут на MiniFtpServer через адаптер FluentFTP.</summary>
    private static (HostedLogs Logs, Action Forget) Connect(MiniFtpServer server)
    {
        var host = "logs-" + Guid.NewGuid().ToString("N")[..12] + ".test";
        var direct = new HostedEndpoint(HostedProtocol.Ftp, "127.0.0.1", server.Port, FtpTls.None, server.Options.User, server.Options.Password,
            false, null) { Timeout = TimeSpan.FromSeconds(5) };
        var registration = RemoteFilesFactory.RegisterHost(host, _ => new FluentFtpFiles(direct));
        var profile = new GameProfile
        {
            Id = "logs-" + Guid.NewGuid().ToString("N"),
            Name = "Hosted logs",
            Kind = ProfileKind.Server,
            RemoteCode = GameProfile.HostedMarker,
            Hosted = new HostedConnection
            {
                Host = host, Port = server.Port, User = server.Options.User, Password = HostedSecret.Protect(server.Options.Password),
                Tls = FtpTls.None, ConfigPath = "serverconfig.json",
            },
        };
        var logs = new HostedLogs(ModTarget.For(profile) ?? throw new InvalidOperationException("no target"));
        return (logs, () =>
        {
            HostedSessions.Forget(profile.Id);
            registration.Dispose();
        });
    }
}
