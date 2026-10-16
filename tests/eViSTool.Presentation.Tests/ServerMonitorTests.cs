using System.Net;
using System.Net.Sockets;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Наблюдатель за сервером профиля: что и в каком порядке он сообщает разделу «Сервер», когда сеанс кончается сам и
/// когда его останавливают. Удалённый сервер — настоящий агент с поддельным сервером (<see cref="TestServers"/>). Свой —
/// без агента: агенты тестов живут в своей папке, а наблюдатель ищет агента профиля в папке программы (рядом с тестами).
/// </summary>
public sealed class ServerMonitorTests(TestServers servers) : IClassFixture<TestServers>
{
    /// <summary>Раздел «Сервер» для наблюдателя: записывает, что ему сообщили, по порядку.</summary>
    private sealed class Host : IServerMonitorHost
    {
        public GameProfile? Profile { get; set; }
        public ServerState State { get; set; } = ServerState.Stopped;
        public bool IsBusy { get; set; }

        public List<string> Calls { get; } = [];
        public List<Exception> RemoteErrors { get; } = [];
        public AgentClient? Attached { get; private set; }
        public int LocalChecks { get; private set; }

        /// <summary>Что сделать на проходе по своему серверу (номер прохода с 1) — например, упасть.</summary>
        public Action<int>? OnLocal { get; set; }

        public void OnAttached(AgentClient client, CancellationToken session)
        {
            Attached = client;
            Calls.Add("attached");
        }

        public void OnDetached() => Calls.Add("detached");
        public void OnStatus(AgentStatus status) => Calls.Add("status");

        public void OnLocalChecked(IReadOnlyList<int> serverPids, bool ourServerUp)
        {
            Calls.Add("local");
            LocalChecks++;
            OnLocal?.Invoke(LocalChecks);
        }

        public void OnRemoteChecked(Exception? error)
        {
            Calls.Add(error is null ? "remote-ok" : "remote-failed");
            if (error is not null) RemoteErrors.Add(error);
        }

        public void OnRemoteCodeUnreadable() => Calls.Add("unreadable");
    }

    /// <summary>Платформа, которая запоминает, что записали в журнал сбоев (настоящий журнал тест не трогает).</summary>
    private sealed class CrashLogPlatform : TestUiPlatform, IUiPlatform
    {
        public List<Exception> Logged { get; } = [];

        string IUiPlatform.WriteCrashLog(Exception ex)
        {
            Logged.Add(ex);
            return "";
        }
    }

    private static GameProfile Remote(string code) =>
        new() { Id = "monitor-" + Guid.NewGuid().ToString("N")[..8], Name = "remote", Kind = ProfileKind.Server, RemoteCode = code };

    private static GameProfile Local()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-monitor-" + Guid.NewGuid().ToString("N"))).FullName;
        return new() { Id = "monitor-" + Guid.NewGuid().ToString("N")[..8], Name = "local", Kind = ProfileKind.Server, GameDir = dir, DataDir = dir };
    }

    [Fact]
    public void NoProfile_SessionEndsAtOnce() => Sta.Run(() =>
    {
        var host = new Host();
        var monitor = new ServerMonitor(host);

        Sta.Wait(monitor.Start());

        Assert.Empty(host.Calls);
        Assert.Null(monitor.Client);
    });

    // код подключения не расшифровать — ждать нечего: раздел узнаёт об этом один раз, сеанс кончается сам
    [Fact]
    public void RemoteCodeUnreadable_SaysSoOnce_AndEndsTheSession() => Sta.Run(() =>
    {
        var host = new Host { Profile = Remote("not a protected code") };
        var monitor = new ServerMonitor(host);

        Sta.Wait(monitor.Start());

        Assert.Equal(["unreadable"], host.Calls);
        Assert.Null(monitor.Client);
    });

    // сначала связь (поток консоли запускается от неё), потом статус, потом «проход удался»; остановка закрывает связь
    [Fact]
    public void RemoteAgent_AttachesThenReportsStatus_AndStopClosesTheSession() => Sta.Run(() =>
    {
        var host = new Host { Profile = Remote(RemoteSecret.Protect(servers.RemoteA.Code!)) };
        var monitor = new ServerMonitor(host);

        var watch = monitor.Start();
        Sta.WaitFor(() => host.Calls.Contains("remote-ok"));

        Assert.Equal(["attached", "status", "remote-ok"], host.Calls.Take(3));
        Assert.NotNull(host.Attached);
        Assert.Same(host.Attached, monitor.Client);

        monitor.Stop();
        Sta.Wait(watch);

        Assert.Null(monitor.Client);
        Assert.DoesNotContain("detached", host.Calls); // сброс показанного при смене профиля — дело самого раздела
        Assert.DoesNotContain("remote-failed", host.Calls);
    });

    // агент не отвечает — раздел получает ошибку связи; клиента нет, сеанс продолжается (следующая попытка — позже)
    [Fact]
    public void RemoteUnreachable_ReportsTheError_AndKeepsWatching() => Sta.Run(() =>
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var host = new Host { Profile = Remote(RemoteSecret.Protect(servers.RemoteA.Code! with { Port = closedPort })) };
        var monitor = new ServerMonitor(host);

        var watch = monitor.Start();
        Sta.WaitFor(() => host.RemoteErrors.Count > 0);

        Assert.IsType<HttpRequestException>(host.RemoteErrors[0]);
        Assert.Equal(["remote-failed"], host.Calls);
        Assert.Null(monitor.Client);
        Assert.False(watch.IsCompleted);

        monitor.Stop();
        Sta.Wait(watch);
    });

    // своего агента нет — подключаться не к кому, но каждый проход ищет сервер из папки игры
    [Fact]
    public void LocalWithoutAgent_ChecksForServersEveryPass() => Sta.Run(() =>
    {
        var host = new Host { Profile = Local() };
        var monitor = new ServerMonitor(host);

        var watch = monitor.Start();
        Sta.WaitFor(() => host.LocalChecks >= 2);
        monitor.Stop();
        Sta.Wait(watch);

        Assert.All(host.Calls, c => Assert.Equal("local", c));
        Assert.Null(monitor.Client);
        Directory.Delete(host.Profile!.GameDir!, recursive: true);
    });

    // неожиданная ошибка прохода не останавливает слежение: она в журнале, следующий проход идёт своим чередом
    [Fact]
    public void UnexpectedErrorInAPass_IsLogged_AndWatchingGoesOn() => Sta.Run(() =>
    {
        var before = Ui.Platform;
        var platform = new CrashLogPlatform();
        Ui.Platform = platform;
        try
        {
            var host = new Host { Profile = Local(), OnLocal = pass => { if (pass == 1) throw new FormatException("broken"); } };
            var monitor = new ServerMonitor(host);

            var watch = monitor.Start();
            Sta.WaitFor(() => host.LocalChecks >= 2);
            monitor.Stop();
            Sta.Wait(watch);

            Assert.IsType<FormatException>(Assert.Single(platform.Logged));
            Directory.Delete(host.Profile!.GameDir!, recursive: true);
        }
        finally { Ui.Platform = before; }
    });
}
