using System.Diagnostics;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Настоящий eViSTool.Agent.exe + поддельный сервер, управление по HTTP — как из окна.</summary>
public sealed class AgentTests : IAsyncLifetime
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string AgentExe = Path.Combine(Root, "src", "eViSTool.Agent", "bin", "Debug", "net10.0", "eViSTool.Agent.exe");
    private static readonly string FakeBin = Path.Combine(Root, "tests", "FakeVsServer", "bin", "Debug", "net10.0");

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "evistool-agent-" + Guid.NewGuid().ToString("N"));
    private string AgentsDir => Path.Combine(_tmp, "agents");
    private GameProfile _profile = null!;
    private AgentClient? _client;

    public Task InitializeAsync()
    {
        // «папка игры»: поддельный сервер под именем VintagestoryServer.exe (apphost сам найдёт FakeVsServer.dll)
        var game = Directory.CreateDirectory(Path.Combine(_tmp, "game")).FullName;
        foreach (var f in Directory.GetFiles(FakeBin)) File.Copy(f, Path.Combine(game, Path.GetFileName(f)));
        File.Copy(Path.Combine(FakeBin, "FakeVsServer.exe"), Path.Combine(game, "VintagestoryServer.exe"));
        var data = Directory.CreateDirectory(Path.Combine(_tmp, "data")).FullName;
        _profile = new GameProfile { Kind = ProfileKind.Server, GameDir = game, DataDir = data };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            try { await _client.ShutdownAsync(); } catch (Exception) { /* уже завершён */ }
            var pid = _client.Endpoint.Pid;
            _client.Dispose();
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(10000); if (!p.HasExited) p.Kill(true); }
            catch (ArgumentException) { }
        }
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private static async Task Until(Func<Task<bool>> condition, int timeoutMs = 15000)
    {
        var until = DateTime.Now.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.Now > until) throw new TimeoutException();
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task AgentRunsServerAndIsControlledOverHttp()
    {
        Assert.True(File.Exists(AgentExe), AgentExe);
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: true, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Running);

        // второй раз — тот же агент, а не новый
        using (var again = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir))
            Assert.Equal(_client.Endpoint.Pid, again.Endpoint.Pid);

        await _client.CommandAsync("/time");
        await Until(async () => (await _client.ConsoleAsync(0, 0)).Any(l => l.Text.Contains("Handling Console Command /time")));
        var lines = await _client.ConsoleAsync(0, 0);
        Assert.Contains(lines, l => l.Text.Contains("Неповрежденный мир")); // UTF-8 через консоль агента

        // долгий опрос: ждёт новую строку, а не возвращается пустым сразу
        var last = lines[^1].Seq;
        var waiting = _client.ConsoleAsync(last, 10);
        await Task.Delay(300);
        await _client.CommandAsync("/ping");
        Assert.Contains(await waiting, l => l.Seq > last);

        await _client.StopAsync();
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Stopped);
        Assert.Equal(0, (await _client.StatusAsync()).LastExitCode);
    }

    [Fact]
    public async Task RejectsWrongKey()
    {
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir);
        using var stranger = new AgentClient(_client.Endpoint, "wrong-key");
        await Assert.ThrowsAsync<InvalidOperationException>(() => stranger.StatusAsync());
    }

    [Fact]
    public async Task ShutdownRemovesStateFileAndExits()
    {
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: true, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Running);
        var pid = _client.Endpoint.Pid;

        await _client.ShutdownAsync();
        using var p = Process.GetProcessById(pid);
        Assert.True(p.WaitForExit(15000));
        Assert.False(File.Exists(AgentProtocol.StateFile(_profile.Id, AgentsDir)));
        Assert.Null(AgentClient.TryConnect(_profile.Id, AgentsDir));
        _client.Dispose();
        _client = null;
    }
}
