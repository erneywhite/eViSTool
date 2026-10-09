using System.Diagnostics;
using eViSTool.Core.Server;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

/// <summary>
/// Команда setup и агент без окна, который берёт её data/agent.json: настоящий eViSTool.Agent и поддельный сервер.
/// agent.json лежит рядом с папкой agents (--agents-dir) — тесты не трогают настоящий.
/// </summary>
public sealed class AgentSetupTests : IDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string AgentExe = Environment.GetEnvironmentVariable("EVISTOOL_TEST_AGENT")
        ?? Path.Combine(Root, "src", "eViSTool.Agent", "bin", "Debug", "net10.0", AgentProtocol.ExeName);

    private readonly string _tmp = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "evistool-setup-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly List<Process> _started = [];
    private string AgentsDir => Path.Combine(_tmp, "agents");
    private string ConfigFile => Path.Combine(_tmp, AgentConfig.FileName);

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

    private async Task<(int Code, string Output)> Run(params string[] args)
    {
        var p = Start(args);
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await output + await error);
    }

    /// <summary>Папка игры с поддельным сервером, который агент может запустить (на Windows — ещё и dll для поиска).</summary>
    private string Game()
    {
        var game = Path.Combine(_tmp, "server");
        FakeServer.InstallAs(game);
        var dll = Path.Combine(game, "VintagestoryServer.dll");
        if (!File.Exists(dll)) File.WriteAllText(dll, "");
        return game;
    }

    private string Data(string name, int port, string? serverName = null)
    {
        var data = Directory.CreateDirectory(Path.Combine(_tmp, name)).FullName;
        File.WriteAllText(Path.Combine(data, "serverconfig.json"),
            new JObject { ["Port"] = port, ["ServerName"] = serverName }.ToString());
        return data;
    }

    private async Task<AgentClient> Connect(Process agent, string profile)
    {
        for (var until = DateTime.Now.AddSeconds(20); ; await Task.Delay(150))
        {
            if (agent.HasExited) Assert.Fail($"agent exited: {agent.ExitCode}");
            Assert.True(DateTime.Now < until, "agent did not start");
            if (AgentClient.TryConnect(profile, AgentsDir) is { } client) return client;
        }
    }

    private static async Task Until(Func<Task<bool>> condition, int timeoutMs = 20000)
    {
        for (var until = DateTime.Now.AddMilliseconds(timeoutMs); !await condition(); await Task.Delay(150))
            if (DateTime.Now > until) throw new TimeoutException();
    }

    [Fact]
    public async Task Setup_RemembersTheServer_KeepsHandEdits_AndTheAgentStartsFromAgentJson()
    {
        var (game, data) = (Game(), Data("vs-data", 42781, "Остров"));

        var (code, text) = await Run("setup", "--game", game, "--data", data, "--agents-dir", AgentsDir, "--lang", "en");
        Assert.True(code == 0, text);
        Assert.Contains($"Server:  {game} (option --game)", text);
        Assert.Contains("[ok] server files can be read", text);
        Assert.Contains($"Saved: {ConfigFile}", text);
        Assert.Contains("remote enable --host", text); // что дальше
        var saved = AgentConfig.Load(ConfigFile)!;
        Assert.Equal((game, data, "server", true), (saved.GameDir, saved.DataDir, saved.Profile, saved.StartServer));

        // человек дописал в файл своё: имя сервера, профиль, заметку
        var doc = JObject.Parse(File.ReadAllText(ConfigFile));
        doc["ServerName"] = "Survival Island";
        doc["Profile"] = "survival";
        doc["Note"] = "keep me";
        File.WriteAllText(ConfigFile, doc.ToString());

        // повторный setup без ключей: папки — из agent.json, правки на месте
        (code, text) = await Run("setup", "--agents-dir", AgentsDir, "--lang", "en");
        Assert.True(code == 0, text);
        Assert.Contains($"Data:    {data} (from agent.json)", text);
        Assert.Contains("Profile: survival", text);
        doc = JObject.Parse(File.ReadAllText(ConfigFile));
        Assert.Equal(("keep me", "Survival Island", "survival"), ((string?)doc["Note"], (string?)doc["ServerName"], (string?)doc["Profile"]));

        // агент без ключей: всё из agent.json — профиль, папки, «запускать сервер», имя копий
        var agent = Start("--agents-dir", AgentsDir, "--lang", "en", "--idle-exit", "60");
        var output = agent.StandardOutput.ReadToEndAsync();
        _ = agent.StandardError.ReadToEndAsync();
        using (var client = await Connect(agent, "survival"))
        {
            await Until(async () => (await client.StatusAsync()).State == ServerState.Running);
            Assert.Equal(42781, (await client.StatusAsync()).GamePort);

            await client.BackupAsync();
            var store = new BackupStore(data);
            await Until(() => Task.FromResult(store.List().Any(b => b.Name.StartsWith($"Survival_Island-{DateTime.Now:yyyy-MM-dd}"))));
            await client.ShutdownAsync();
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await agent.WaitForExitAsync(cts.Token);
        Assert.Equal(0, agent.ExitCode);
        Assert.Contains($"Server: {game}, data: {data} (from {ConfigFile})", await output);
    }

    [Fact]
    public async Task Keys_WinOverAgentJson()
    {
        var game = Game();
        var (saved, given) = (Data("saved-data", 42782), Data("given-data", 42783));
        // профиль свой: на Windows агент занимает профиль по имени на всю машину, а тесты идут параллельно
        AgentConfig.Save(ConfigFile, new AgentConfigUpdate(game, saved, Profile: "keys-win", StartServer: false));

        // --data — поверх agent.json, игра — из него; сервер не запускается: так сказано в agent.json
        var agent = Start("--data", given, "--agents-dir", AgentsDir, "--lang", "en", "--idle-exit", "60");
        _ = agent.StandardOutput.ReadToEndAsync();
        _ = agent.StandardError.ReadToEndAsync();
        using var client = await Connect(agent, "keys-win");
        var status = await client.StatusAsync();
        Assert.Equal((ServerState.Stopped, 42783), (status.State, status.GamePort));
        await Task.Delay(1000);
        Assert.Equal(ServerState.Stopped, (await client.StatusAsync()).State);
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task BrokenAgentJson_TheAgentSaysWhere_SetupDoesNotOverwriteIt()
    {
        File.WriteAllText(ConfigFile, "{ \"GameDir\": ");

        var (code, text) = await Run("--agents-dir", AgentsDir, "--lang", "en");
        Assert.True(code == 2, text);
        Assert.Contains(ConfigFile, text);
        Assert.Contains("Fix the file", text);
        Assert.False(Directory.Exists(AgentsDir)); // не запускался

        (code, text) = await Run("setup", "--game", Game(), "--data", Data("d", 42784), "--agents-dir", AgentsDir, "--lang", "en");
        Assert.True(code == 1, text);
        Assert.Contains(ConfigFile, text);
        Assert.Equal("{ \"GameDir\": ", File.ReadAllText(ConfigFile));
    }

    [Fact]
    public async Task Setup_NoServer_OrTypoInAnOption_WritesNothing()
    {
        var (code, text) = await Run("setup", "--game", Directory.CreateDirectory(Path.Combine(_tmp, "empty")).FullName,
            "--agents-dir", AgentsDir, "--lang", "en");
        Assert.True(code == 1, text);
        Assert.Contains("Vintage Story server not found", text);

        (code, text) = await Run("setup", "--gmae", "/x", "--agents-dir", AgentsDir, "--lang", "en");
        Assert.True(code == 2, text);
        Assert.Contains("Unknown option for setup: --gmae", text);
        Assert.False(File.Exists(ConfigFile));
    }
}
