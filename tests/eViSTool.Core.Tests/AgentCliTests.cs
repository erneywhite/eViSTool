using System.Diagnostics;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Core.Tests;

/// <summary>
/// Команды агента без окна (Linux): «remote …» на временной папке агентов и «status / start / stop / restart /
/// command» против настоящего агента с поддельным сервером. Агент запускается как отдельная программа — так же,
/// как его зовёт человек в терминале.
/// </summary>
public sealed class AgentCliTests : IAsyncLifetime
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    // EVISTOOL_TEST_AGENT — те же сценарии на релизном (обрезанном) агенте
    private static readonly string AgentExe = Environment.GetEnvironmentVariable("EVISTOOL_TEST_AGENT")
        ?? Path.Combine(Root, "src", "eViSTool.Agent", "bin", "Debug", "net10.0", AgentProtocol.ExeName);

    // профиль агента без окна (AgentArgs.DefaultProfile в самом агенте)
    private const string Profile = "server";

    private readonly string _tmp = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "evistool-cli-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly List<Process> _started = [];
    private string AgentsDir => Path.Combine(_tmp, "agents");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (AgentClient.TryConnect(Profile, AgentsDir) is { } client)
            using (client)
                try { await client.ShutdownAsync(); } catch (Exception) { /* уже выходит */ }
        foreach (var p in _started)
        {
            try { if (!p.WaitForExit(15000)) p.Kill(true); } catch (InvalidOperationException) { }
            p.Dispose();
        }
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { }
    }

    private Process Start(params string[] args)
    {
        var psi = new ProcessStartInfo(AgentExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    /// <summary>Команда агента на временной папке агентов (по-английски: консоль Windows исказила бы кириллицу).</summary>
    private async Task<(int Code, string Out, string Err)> Cli(params string[] args)
    {
        var p = Start([.. args, "--agents-dir", AgentsDir, "--lang", "en"]);
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await output, await error);
    }

    private static async Task Until(Func<Task<bool>> condition, int timeoutMs = 15000)
    {
        for (var until = DateTime.Now.AddMilliseconds(timeoutMs); !await condition(); await Task.Delay(100))
            if (DateTime.Now > until) throw new TimeoutException();
    }

    /// <summary>Агент профиля «server» с поддельным сервером (или с exe, которого нет), как его запускает служба.</summary>
    private async Task<AgentClient> RunAgent(string? exe = null)
    {
        var game = Directory.CreateDirectory(Path.Combine(_tmp, "game")).FullName;
        exe ??= FakeServer.InstallAs(game);
        var data = Directory.CreateDirectory(Path.Combine(_tmp, "data")).FullName;
        var agent = Start("--exe", exe, "--data", data, "--agents-dir", AgentsDir, "--idle-exit", "120", "--lang", "en");
        _ = agent.StandardOutput.ReadToEndAsync();
        _ = agent.StandardError.ReadToEndAsync();
        for (var until = DateTime.Now.AddSeconds(20); ; await Task.Delay(150))
        {
            if (agent.HasExited) Assert.Fail($"agent exited: {agent.ExitCode}");
            Assert.True(DateTime.Now < until, "agent did not start");
            if (AgentClient.TryConnect(Profile, AgentsDir) is { } client) return client;
        }
    }

    [Fact]
    public async Task WrongWords_AreRefusedWithCode2_AndTouchNothing()
    {
        foreach (var args in new[]
                 {
                     new[] { "remote" }, ["remote", "frob"], ["remote", "host", "bad_host!"], ["remote", "host", "a.b", "c.d"],
                     ["remote", "enable", "--port", "5"], ["remote", "enable", "--port", "x"], ["remote", "enable", "--hots", "1.2.3.4"],
                     ["remote", "enable", "--host"], ["remote", "enable", "--host", "http://1.2.3.4"], ["remote", "status", "--host", "1.2.3.4"],
                     ["remote", "code", "now"], ["status", "extra"], ["start", "--timeout", "0"], ["stop", "--wait", "3"],
                     ["command"], ["command", "--wait", "x", "/time"],
                 })
        {
            var (code, output, error) = await Cli(args);
            Assert.True(code == 2, $"{string.Join(" ", args)}: {code}\n{output}{error}");
            Assert.NotEmpty(error);
        }
        Assert.False(Directory.Exists(AgentsDir)); // ничего не создано

        // справка по remote — по --help, в stdout и с кодом 0
        var help = await Cli("remote", "--help");
        Assert.Equal(0, help.Code);
        Assert.Contains("new-key", help.Out);
    }

    [Fact]
    public async Task Remote_EnableHostCodeNewKeyDisable_WithoutAnAgent()
    {
        // выключен — кода нет
        var r = await Cli("remote", "code");
        Assert.Equal(1, r.Code);
        Assert.Contains("Remote access is off", r.Err);
        Assert.DoesNotContain(ConnectionCode.Prefix, r.Out);

        // включили без адреса: порт, ключ и сертификат есть, а код без адреса не выдаётся — подсказка «remote host»
        r = await Cli("remote", "enable");
        Assert.True(r.Code == 0, r.Out + r.Err);
        var s = RemoteAccess.Load(Profile, AgentsDir);
        Assert.True(s.Enabled);
        Assert.InRange(s.Port, RemoteAccess.MinPort, RemoteAccess.MaxPort);
        Assert.Contains($"on, port {s.Port}", r.Out);
        Assert.Contains("agent is not running", r.Out);
        Assert.Contains("remote host", r.Err);
        r = await Cli("remote", "code");
        Assert.Equal(1, r.Code);
        Assert.Contains("remote host <address>", r.Err);
        Assert.DoesNotContain(ConnectionCode.Prefix, r.Out);

        // «remote host» без адреса — что можно подставить
        r = await Cli("remote", "host");
        Assert.Equal(0, r.Code);
        Assert.Contains("No address in the code yet", r.Out);
        foreach (var a in RemoteAccess.LocalAddresses()) Assert.Contains(a, r.Out);

        r = await Cli("remote", "host", "192.168.31.70");
        Assert.Equal(0, r.Code);
        Assert.Equal("192.168.31.70", RemoteAccess.Load(Profile, AgentsDir).Host);

        // код — одной строкой в stdout, предупреждение — в stderr; внутри всё то, что в настройках и сертификате
        var code = await CodeAsync();
        string fingerprint;
        using (var cert = RemoteAccess.EnsureCertificate(Profile, AgentsDir)) fingerprint = RemoteAccess.Fingerprint(cert);
        Assert.Equal(new ConnectionCode("192.168.31.70", s.Port, s.Key, fingerprint), code);

        // статус — без ключа
        r = await Cli("remote", "status");
        Assert.Equal(0, r.Code);
        Assert.Contains($"Remote access: on, port {s.Port}", r.Out);
        Assert.Contains("Address in the code: 192.168.31.70", r.Out);
        Assert.Contains("Agent: not running", r.Out);
        Assert.DoesNotContain(s.Key, r.Out + r.Err);

        // новый ключ — прежний код больше не подходит, порт тот же
        r = await Cli("remote", "new-key");
        Assert.Equal(0, r.Code);
        Assert.Contains("no longer works", r.Out);
        var renewed = await CodeAsync();
        Assert.NotEqual(code.Key, renewed.Key);
        Assert.Equal((code.Port, code.Fingerprint), (renewed.Port, renewed.Fingerprint));
        Assert.DoesNotContain(renewed.Key, r.Out + r.Err);

        // свой порт и адрес — ключами enable (домен тоже годится)
        var port = RemoteAccess.FreePort();
        r = await Cli("remote", "enable", "--host", "vs.example.org", "--port", port.ToString());
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Equal(("vs.example.org", port, renewed.Key), ((await CodeAsync()).Host, (await CodeAsync()).Port, (await CodeAsync()).Key));

        r = await Cli("remote", "disable");
        Assert.Equal(0, r.Code);
        Assert.False(RemoteAccess.Load(Profile, AgentsDir).Enabled);
        Assert.Contains("Remote access: off", (await Cli("remote", "status")).Out);
        Assert.Equal(1, (await Cli("remote", "code")).Code);

        // на Linux ключ и сертификат — только владельцу
        if (!OperatingSystem.IsWindows())
            foreach (var file in new[] { RemoteAccess.FileFor(Profile, AgentsDir), RemoteAccess.CertFile(Profile, AgentsDir) })
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    /// <summary>«remote code»: одна строка в stdout, её разбирает окно (ConnectionCode.TryParse).</summary>
    private async Task<ConnectionCode> CodeAsync()
    {
        var (exit, output, error) = await Cli("remote", "code");
        Assert.True(exit == 0, output + error);
        Assert.Contains("like a password", error);
        Assert.DoesNotContain(ConnectionCode.Prefix, error);
        var line = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.True(ConnectionCode.TryParse(line, out var code), line);
        return code;
    }

    [Fact]
    public async Task NoAgent_StatusSaysSo_AndHowToStartIt()
    {
        foreach (var command in new[] { "status", "start", "stop", "restart" })
        {
            var (code, output, error) = await Cli(command);
            Assert.True(code == 1, output + error);
            Assert.Contains("The agent of profile server is not running", error);
            Assert.Contains("systemctl start", error);
        }
        var r = await Cli("command", "/time");
        Assert.Equal(1, r.Code);
    }

    [Fact]
    public async Task StatusStartCommandRestartStop_ThroughTheRunningAgent()
    {
        using var client = await RunAgent();

        var r = await Cli("status");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains($"Agent: running, PID {client.Endpoint.Pid}", r.Out);
        Assert.Contains("Server: stopped", r.Out);
        Assert.Contains("Remote access: off", r.Out);

        // команда остановленному серверу — отказ агента его словами
        r = await Cli("command", "/time");
        Assert.Equal(1, r.Code);
        Assert.Contains("The server is not running", r.Err);

        r = await Cli("start");
        Assert.True(r.Code == 0, r.Out + r.Err);
        var running = await client.StatusAsync();
        Assert.Equal(ServerState.Running, running.State);
        Assert.Contains($"The server is running (PID {running.ServerPid})", r.Out);
        Assert.Contains("already running", (await Cli("start")).Out);

        // ответ сервера — строки консоли после команды; «time» без косой черты — тоже команда
        r = await Cli("command", "time");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("Handling Console Command /time", r.Out);
        r = await Cli("command", "/fakejoin", "1", "Erney");
        Assert.Contains("Erney 10.0.0.1:5000 joins", r.Out);

        r = await Cli("status");
        Assert.Contains("Server: running for", r.Out);
        Assert.Contains($"PID {running.ServerPid}", r.Out);
        Assert.Contains("Players (1): Erney", r.Out);

        r = await Cli("restart");
        Assert.True(r.Code == 0, r.Out + r.Err);
        var restarted = await client.StatusAsync();
        Assert.Equal(ServerState.Running, restarted.State);
        Assert.NotEqual(running.ServerPid, restarted.ServerPid);
        Assert.Contains($"(PID {restarted.ServerPid})", r.Out);

        r = await Cli("stop");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains("The server is stopped (exit code 0)", r.Out);
        Assert.Equal(ServerState.Stopped, (await client.StatusAsync()).State);
        Assert.Contains("already stopped", (await Cli("stop")).Out);
    }

    [Fact]
    public async Task RemoteEnable_TheRunningAgentListens_AndTheCodeConnects()
    {
        using var client = await RunAgent();

        // агент подхватывает файл сам (раз в 5 секунд) — команда дожидается и говорит итог
        var r = await Cli("remote", "enable", "--host", "127.0.0.1");
        Assert.True(r.Code == 0, r.Out + r.Err);
        var port = RemoteAccess.Load(Profile, AgentsDir).Port;
        Assert.Contains($"The agent accepts connections on port {port}", r.Out);
        Assert.Equal(port, (await client.StatusAsync()).RemotePort);
        Assert.Contains($"Agent: accepts connections on port {port}", (await Cli("remote", "status")).Out);
        Assert.Contains($"Remote access: on, port {port}", (await Cli("status")).Out);

        // код из командной строки подключается так же, как его вставили бы в окно: TLS, отпечаток, свой ключ
        var code = await CodeAsync();
        using (var remote = AgentClient.ForRemote(code))
            Assert.Equal(client.Endpoint.Pid, (await remote.StatusAsync()).AgentPid);

        // новый ключ: прежний код отказывают, новый — пускают. Агент при этом перезапускает удалённый вход — на мгновение
        // подключение отбивается («Connection refused»), окно в таком случае просто повторяет запрос; тест — тоже
        Assert.Equal(0, (await Cli("remote", "new-key")).Code);
        await Until(async () =>
        {
            using var old = AgentClient.ForRemote(code);
            try { await old.StatusAsync(); return false; }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("401")) { return true; }
            catch (HttpRequestException) { return false; }
        });
        var freshCode = await CodeAsync();
        await Until(async () =>
        {
            using var fresh = AgentClient.ForRemote(freshCode);
            try { return (await fresh.StatusAsync()).AgentPid == client.Endpoint.Pid; }
            catch (HttpRequestException) { return false; }
        });

        r = await Cli("remote", "disable");
        Assert.True(r.Code == 0, r.Out + r.Err);
        Assert.Contains($"The agent has closed port {port}", r.Out);
        Assert.Null((await client.StatusAsync()).RemotePort);
    }

    [Fact]
    public async Task StartFails_TheReasonFromTheAgentIsShown()
    {
        var missing = Path.Combine(_tmp, "nowhere", "VintagestoryServer.dll");
        using var client = await RunAgent(missing);

        var r = await Cli("start");
        Assert.Equal(1, r.Code);
        Assert.Contains(missing, r.Err);
        Assert.Equal(ServerState.Stopped, (await client.StatusAsync()).State);
    }
}
