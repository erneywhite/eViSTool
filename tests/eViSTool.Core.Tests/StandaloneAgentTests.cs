using System.Diagnostics;
using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>
/// Агент без окна (так он работает на Linux): сам находит сервер, профиль «server», один на профиль.
/// Сервер здесь не запускается — нужна только папка, похожая на папку игры, поэтому тесты идут и на Linux.
/// </summary>
public sealed class StandaloneAgentTests : IDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string AgentExe = Environment.GetEnvironmentVariable("EVISTOOL_TEST_AGENT")
        ?? Path.Combine(Root, "src", "eViSTool.Agent", "bin", "Debug", "net10.0", AgentProtocol.ExeName);

    private readonly string _tmp = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "evistool-standalone-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly List<Process> _started = [];
    private string AgentsDir => Path.Combine(_tmp, "agents");

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { if (!p.HasExited) p.Kill(true); p.WaitForExit(10000); } catch (InvalidOperationException) { }
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

    /// <summary>Запустить и дождаться выхода: код и всё, что агент написал.</summary>
    private async Task<(int Code, string Output)> Run(params string[] args)
    {
        var p = Start(args);
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await output + await error);
    }

    [Fact]
    public async Task WithoutTheWindow_FindsTheServer_ByItsServerSh_AndWorksWithItsData()
    {
        var game = Directory.CreateDirectory(Path.Combine(_tmp, "server")).FullName;
        File.WriteAllText(Path.Combine(game, "VintagestoryServer.dll"), "");
        File.WriteAllText(Path.Combine(game, "VintagestoryServer.exe"), "");
        var data = Directory.CreateDirectory(Path.Combine(_tmp, "var-data")).FullName;
        File.WriteAllText(Path.Combine(game, ServerLocator.ScriptName), $"VSPATH='{game}'\nDATAPATH='{data}'\n");
        File.WriteAllText(Path.Combine(data, "serverconfig.json"), """{ "Port": 42777 }""");

        var agent = Start("--game", game, "--agents-dir", AgentsDir, "--idle-exit", "60", "--lang", "en");
        var output = agent.StandardOutput.ReadToEndAsync();
        _ = agent.StandardError.ReadToEndAsync();
        AgentClient? client = null;
        for (var until = DateTime.Now.AddSeconds(20); client is null; await Task.Delay(150))
        {
            if (agent.HasExited) Assert.Fail($"agent exited: {agent.ExitCode}");
            Assert.True(DateTime.Now < until, "agent did not start");
            client = AgentClient.TryConnect(Profile, AgentsDir);
        }
        using (client)
        {
            // профиль по умолчанию, данные — из server.sh: порт игры прочитан из их serverconfig.json
            var status = await client.StatusAsync();
            Assert.Equal((ServerState.Stopped, 42777), (status.State, status.GamePort));
            Assert.Equal(agent.Id, client.Endpoint.Pid);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(AgentProtocol.KeyFile(Profile, AgentsDir)));

            // отказ — с понятным текстом (problem+json), а не пустой 500: обрезанный агент не умеет Results.Problem
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteBackupAsync("no-such.vcdbs"));
            Assert.Equal("There is no backup named no-such.vcdbs.", refused.Message); // агент — с --lang en

            // второй агент на тот же профиль не запускается
            var (code, text) = await Run("--game", game, "--agents-dir", AgentsDir, "--lang", "en");
            Assert.True(code == 3, text);
            Assert.Contains("already running", text);

            await client.ShutdownAsync();
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await agent.WaitForExitAsync(cts.Token);
        Assert.Equal(0, agent.ExitCode);
        Assert.False(File.Exists(AgentProtocol.StateFile(Profile, AgentsDir)));
        Assert.Contains($"Server: {game}, data: {data} (from {Path.Combine(game, ServerLocator.ScriptName)})", await output);
    }

    [Fact]
    public async Task NoServerThere_SaysWhereItLooked_AndHowToPointToIt()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_tmp, "empty")).FullName;

        // по-английски: консоль Windows без UTF-8 превратила бы кириллицу в «?»
        var (code, text) = await Run("--game", empty, "--agents-dir", AgentsDir, "--lang", "en");
        Assert.True(code == 2, text);
        Assert.Contains("Vintage Story server not found", text);
        Assert.Contains(empty, text);
        Assert.Contains("--game", text);
        Assert.False(Directory.Exists(AgentsDir)); // ничего не создал
    }

    [Fact]
    public async Task UnknownCommand_IsRefused_HelpIsShown()
    {
        var (code, text) = await Run("frobnicate", "--lang", "en");
        Assert.True(code == 2, text);
        Assert.Contains("Unknown command: frobnicate", text);

        (code, text) = await Run("--help", "--lang", "en");
        Assert.True(code == 0, text);
        Assert.Contains("--game", text);
    }

    // профиль агента без окна (AgentArgs.DefaultProfile в самом агенте)
    private const string Profile = "server";
}
