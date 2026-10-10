using eViSTool.Core.Game;
using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

public sealed class ServerHostTests : IAsyncLifetime
{
    // поддельный сервер: на Windows FakeVsServer.exe, на Linux FakeVsServer.dll — её ServerHost запускает через dotnet
    private static readonly string FakeExe = FakeServer.ServerFile;

    private readonly string _data = Path.Combine(Path.GetTempPath(), "evistool-srv-" + Guid.NewGuid().ToString("N"));
    private ServerHost? _host;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_data);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        Directory.Delete(_data, recursive: true);
    }

    private ServerHost Host(Func<ServerHostOptions, ServerHostOptions>? tweak = null)
    {
        Assert.True(File.Exists(FakeExe), FakeExe);
        var options = new ServerHostOptions
        {
            ExePath = FakeExe,
            DataPath = _data,
            RestartDelay = TimeSpan.FromMilliseconds(200),
            StopTimeout = TimeSpan.FromSeconds(5),
        };
        return _host = new ServerHost(tweak?.Invoke(options) ?? options);
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 10000)
    {
        var until = DateTime.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.Now > until) throw new TimeoutException();
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task CommandsSentTogether_GoOneByOne_WithAPause()
    {
        var host = Host();
        await host.StartAsync();
        await Until(() => host.State == ServerState.Running);

        // «/announce …» и «/genbackup» перед перезапуском — подряд, как в агенте
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(host.SendCommandAsync("/announce one"), host.SendCommandAsync("/time"));
        Assert.True(clock.Elapsed >= ServerHost.CommandGap - TimeSpan.FromMilliseconds(50), clock.Elapsed.ToString());
        await Until(() => host.Console.GetSince(0).Any(l => l.Text.Contains("Handling Console Command /time")));
        Assert.Contains(host.Console.GetSince(0), l => l.Text.Contains("Handling Console Command /announce one"));

        await host.StopAsync();
    }

    [Fact]
    public async Task StartsSendsCommandAndStopsGracefully()
    {
        var host = Host();
        await host.StartAsync();
        await Until(() => host.State == ServerState.Running);
        Assert.NotNull(host.Pid);

        await host.SendCommandAsync("/time");
        await Until(() => host.Console.GetSince(0).Any(l => l.Text.Contains("Handling Console Command /time")));

        await host.StopAsync();
        Assert.Equal(ServerState.Stopped, host.State);
        Assert.Equal(0, host.LastExitCode);

        var lines = host.Console.GetSince(0);
        Assert.Contains(lines, l => l.Kind == ConsoleLineKind.Input && l.Text == "/time");
        Assert.Contains(lines, l => l.Text.Contains("Неповрежденный мир")); // кириллица не побилась
        Assert.Contains(lines, l => l.Level == "Server Notification"); // так уровень пишет консоль настоящего сервера
    }

    [Fact]
    public async Task WatchdogRestartsAfterCrash()
    {
        var host = Host();
        await host.StartAsync();
        await Until(() => host.State == ServerState.Running);
        var firstPid = host.Pid;

        await host.SendCommandAsync("/crash");
        await Until(() => host.State == ServerState.Running && host.Pid is { } pid && pid != firstPid);
        Assert.Contains(host.Console.GetSince(0), l => l.Kind == ConsoleLineKind.System && l.Text.Contains("1"));
    }

    [Fact]
    public async Task WatchdogGivesUpAfterTooManyCrashes()
    {
        var host = Host(o => o with { MaxRestartsPerHour = 1 });
        await host.StartAsync();
        await Until(() => host.State == ServerState.Running);

        var firstPid = host.Pid;
        await host.SendCommandAsync("/crash");                       // 1-е падение — перезапуск
        await Until(() => host.State == ServerState.Running && host.Pid is { } pid && pid != firstPid);
        await host.SendCommandAsync("/crash");                       // 2-е — сторож сдаётся
        await Until(() => host.State == ServerState.Stopped);
        await Task.Delay(600);
        Assert.Equal(ServerState.Stopped, host.State);
        Assert.Null(host.RestartScheduledAt);
    }

    [Fact]
    public async Task KillsWhenServerIgnoresStop()
    {
        var host = Host(o => o with { StopTimeout = TimeSpan.FromMilliseconds(800) });
        await host.StartAsync();
        await Until(() => host.State == ServerState.Running);
        await host.SendCommandAsync("/hang");

        await host.StopAsync();
        Assert.Equal(ServerState.Stopped, host.State);
        Assert.Null(host.RestartScheduledAt); // принудительная остановка — не «падение», сторож молчит
    }

    [Fact]
    public async Task CtrlCStopsServerThatIgnoresStop()
    {
        // на Windows — Ctrl+C через общую консоль: без консоли его некуда слать (так бывает только в окне eViSTool);
        // на Linux — SIGTERM серверу
        if (OperatingSystem.IsWindows() && !Server.ConsoleInterop.HasConsole) return;
        var host = _host = new ServerHost(new ServerHostOptions
        {
            ExePath = FakeExe, DataPath = _data, StopTimeout = TimeSpan.FromMilliseconds(800),
        }, new SharedConsoleCtrlC());
        await host.StartAsync();
        await Until(() => host.State == ServerState.Running);
        await host.SendCommandAsync("/hang");

        await host.StopAsync();
        Assert.Equal(0, host.LastExitCode); // мягкая остановка по Ctrl+C (SIGTERM), а не kill
        Assert.Contains(host.Console.GetSince(0), l => l.Text.Contains("termination event"));
    }

    [Fact]
    public async Task HeavyStderrDoesNotBlockServer()
    {
        var host = Host();
        await host.StartAsync();
        await Until(() => host.State == ServerState.Running);

        await host.SendCommandAsync("/spam 5000");  // ~1 МБ в stderr: непрочитанный — повесил бы сервер
        await host.SendCommandAsync("/ping");
        await Until(() => host.Console.GetSince(0, 100_000).Any(l => l.Text.Contains("Handling Console Command /ping")));
        Assert.True(host.Console.GetSince(0, 10000).Count(l => l.Kind == ConsoleLineKind.Error) >= 4000);
        await host.StopAsync();
    }
    // ---------- неудачный запуск (аудит, пункт 11) ----------

    /// <summary>Мир свободен (сервер не держит замок).</summary>
    private bool WorldIsFree()
    {
        using var probe = WorldLock.TryTake(_data, WorldLock.Copy);
        return probe is not null;
    }

    [Fact]
    public async Task BrokenExe_FailsCleanly_AndAFixedOneStartsWithoutANewHost()
    {
        var game = Directory.CreateDirectory(Path.Combine(_data, "game")).FullName;
        var exe = ServerExecutable.PathIn(game); // на Linux — dll: битую сборку ServerHost не отдаёт dotnet, а отказывает сразу
        await File.WriteAllTextAsync(exe, "это не программа");
        var host = Host(o => o with { ExePath = exe });

        for (var attempt = 0; attempt < 3; attempt++) // несколько неудачных попыток подряд — каждая чистая
        {
            await Assert.ThrowsAnyAsync<Exception>(host.StartAsync);
            Assert.Equal(ServerState.Stopped, host.State);
            Assert.Null(host.Pid);
            Assert.True(WorldIsFree());
        }

        // исправили: на место — настоящий (поддельный) сервер; тот же хост запускает его
        Assert.Equal(exe, FakeServer.InstallAs(game));

        await host.StartAsync();
        await Until(() => host.State == ServerState.Running);
        Assert.NotNull(host.Pid);
        Assert.False(WorldIsFree());
        await host.StopAsync();
        Assert.True(WorldIsFree());
    }

    [Fact]
    public async Task MissingExe_IsReported_AndNothingIsLeftBusy()
    {
        var host = Host(o => o with { ExePath = ServerExecutable.PathIn(Path.Combine(_data, "нет-такого")) });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<FileNotFoundException>(host.StartAsync);
            Assert.Equal(ServerState.Stopped, host.State);
            Assert.True(WorldIsFree());
        }
    }

    // ---------- замок мира (аудит, пункт 5) ----------

    [Fact]
    public async Task Start_WhileTheWorldIsBeingRestored_IsRefused()
    {
        var host = Host();
        using (WorldLock.Take(_data, WorldLock.Restore))
        {
            var ex = await Assert.ThrowsAsync<WorldBusyException>(host.StartAsync);
            Assert.Equal(WorldLock.BusyMessage(_data), ex.Message);
            Assert.Equal(ServerState.Stopped, host.State);
            Assert.Null(host.Pid);
        }

        await host.StartAsync(); // освободилось — запускается
        await Until(() => host.State == ServerState.Running);
        await host.StopAsync();
    }

    [Fact]
    public async Task Start_WhileAForeignServerHoldsTheSameWorld_IsRefused()
    {
        // «чужой» сервер на тот же мир: запущен мимо ServerHost (агент упал и оставил его, server.sh) — замка мира у него нет
        var game = Directory.CreateDirectory(Path.Combine(_data, "game")).FullName;
        var psi = ServerExecutable.StartInfo(FakeServer.InstallAs(game));
        psi.ArgumentList.Add("--dataPath");
        psi.ArgumentList.Add(_data);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.UseShellExecute = false;
        using var foreign = System.Diagnostics.Process.Start(psi)!;
        try
        {
            var host = Host();
            await Until(() => GameProcess.FindServers(null).Any(s => s.Pid == foreign.Id));
            var ex = await Assert.ThrowsAsync<WorldBusyException>(host.StartAsync);
            Assert.Contains(foreign.Id.ToString(), ex.Message);
            Assert.Equal(ServerState.Stopped, host.State);
            using (var probe = WorldLock.TryTake(_data, WorldLock.Copy)) Assert.NotNull(probe); // отказ не оставил мир занятым

            foreign.Kill(entireProcessTree: true);
            await foreign.WaitForExitAsync();
            await host.StartAsync(); // чужой ушёл — запускается
            await Until(() => host.State == ServerState.Running);
            await host.StopAsync();
        }
        finally
        {
            if (!foreign.HasExited) foreign.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task RunningServer_HoldsTheWorld_UntilItExits()
    {
        var host = Host();
        await host.StartAsync();
        Assert.Null(WorldLock.TryTake(_data, WorldLock.Restore)); // уже в «запускается» — восстановлению отказ
        Assert.Equal(WorldLock.Server, WorldLock.HolderOf(_data));
        await Until(() => host.State == ServerState.Running);
        Assert.Null(WorldLock.TryTake(_data, WorldLock.Copy));

        await host.StopAsync();
        using var after = WorldLock.TryTake(_data, WorldLock.Restore);
        Assert.NotNull(after);
    }

    [Fact]
    public async Task Watchdog_WaitsWhileTheWorldIsBusy_ThenStarts()
    {
        var host = Host(o => o with { RestartDelay = TimeSpan.FromMilliseconds(300) });
        await host.StartAsync();
        await Until(() => host.State == ServerState.Running);

        WorldLock? restore = null;
        host.StateChanged += s => { if (s == ServerState.Stopped) restore ??= WorldLock.TryTake(_data, WorldLock.Restore); };
        await host.SendCommandAsync("/crash");
        await Until(() => restore is not null);

        await Task.Delay(1200); // несколько пауз сторожа — мир занят, сервер не стартует
        Assert.Equal(ServerState.Stopped, host.State);
        Assert.Single(host.Console.GetSince(0), l => l.Text.Contains(WorldLock.BusyMessage(_data))); // сказал один раз

        restore!.Dispose();
        await Until(() => host.State == ServerState.Running);
        await host.StopAsync();
    }

    [Fact]
    public async Task CancelledRestart_DoesNotStartTheServerAfterTheRestore()
    {
        var host = Host(o => o with { RestartDelay = TimeSpan.FromMilliseconds(300) });
        await host.StartAsync();
        await Until(() => host.State == ServerState.Running);

        WorldLock? restore = null;
        host.StateChanged += s => { if (s == ServerState.Stopped) restore ??= WorldLock.TryTake(_data, WorldLock.Restore); };
        await host.SendCommandAsync("/crash");
        await Until(() => restore is not null);
        // сторож назначает перезапуск сразу после «остановлен» — отменять есть что только с этого момента
        await Until(() => host.RestartScheduledAt is not null);

        host.CancelPendingRestart(); // так делает восстановление через агента
        Assert.Null(host.RestartScheduledAt);
        restore!.Dispose();
        await Task.Delay(1000);
        Assert.Equal(ServerState.Stopped, host.State);
    }
}
