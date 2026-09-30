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
        _profile = new GameProfile { Name = "Тест мир", Kind = ProfileKind.Server, GameDir = game, DataDir = data };
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
    public async Task AgentReportsWhoIsOnline()
    {
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: true, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Running);
        Assert.Empty((await _client.StatusAsync()).Players);

        await _client.CommandAsync("/fakejoin 1 Erney");
        await _client.CommandAsync("/fakejoin 2 toristarm");
        await Until(async () => (await _client.StatusAsync()).Players.Count == 2);
        var players = (await _client.StatusAsync()).Players;
        Assert.Equal(["Erney", "toristarm"], players.Select(p => p.Name));
        Assert.Equal("10.0.0.1:5000", players[0].Address);

        await _client.CommandAsync("/fakeleave 1");
        await Until(async () => (await _client.StatusAsync()).Players.Count == 1);
        Assert.Equal("toristarm", (await _client.StatusAsync()).Players[0].Name);

        // сервер остановлен — на нём никого
        await _client.StopAsync();
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Stopped);
        Assert.Empty((await _client.StatusAsync()).Players);
    }

    [Fact]
    public async Task BackupOnRunningServer_IsRotatedByTheAgent()
    {
        // расписание включено, хранить две копии; на диске уже лежат три старые свои, прежняя «default-…» и «ручная»
        new ServerAutomation { BackupEnabled = true, BackupIntervalHours = 24, BackupKeep = 2 }.Save(_profile.Id, AgentsDir);
        var dir = Directory.CreateDirectory(Path.Combine(_profile.DataDir!, "Backups")).FullName;
        foreach (var day in new[] { "01", "02", "03" }) File.WriteAllText(Path.Combine(dir, $"Тест_мир-2026-09-{day}_10-00-00.vcdbs"), "old");
        File.WriteAllText(Path.Combine(dir, "default-2026-08-01_10-00-00.vcdbs"), "legacy");
        File.WriteAllText(Path.Combine(dir, "before-update.vcdbs"), "manual");

        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: true, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Running);
        var status = await _client.StatusAsync();
        Assert.Equal(new DateTime(2026, 9, 3, 10, 0, 0), status.LastBackupAt); // самая свежая своя на диске
        Assert.NotNull(status.NextBackupAt);

        await _client.BackupAsync();
        var store = new BackupStore(_profile.DataDir!, BackupStore.Slug(_profile.Name));
        await Until(() => Task.FromResult(store.List().Count(b => b.IsOwn) == 2));

        var names = store.List().Select(b => b.Name).ToList();
        var fresh = Assert.Single(names, n => n.StartsWith($"Тест_мир-{DateTime.Now:yyyy-MM-dd}")); // имя профиля — в имени копии
        Assert.Contains("Тест_мир-2026-09-03_10-00-00.vcdbs", names); // вторая по свежести осталась
        Assert.DoesNotContain("Тест_мир-2026-09-01_10-00-00.vcdbs", names);
        Assert.Contains("default-2026-08-01_10-00-00.vcdbs", names); // чужие и ручные копии ротация не трогает
        Assert.Contains("before-update.vcdbs", names);
        Assert.True((await _client.StatusAsync()).LastBackupAt > DateTime.Now.AddMinutes(-1));

        // о готовой копии — строка в консоли и объявление игрокам в чат
        await Until(async () => (await _client.ConsoleAsync(0, 0)).Any(l => l.Text.Contains("/announce") && l.Text.Contains(fresh)));
        Assert.Contains(await _client.ConsoleAsync(0, 0), l => l.Kind == ConsoleLineKind.System && l.Text.Contains(fresh));
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

    [Fact]
    public async Task RemoteAccess_OverTls_WithPinnedCertificateAndOwnKey()
    {
        // включили удалённый доступ до запуска агента — он поднимет сетевой вход сам
        var remote = eViSTool.Core.Server.Remote.RemoteAccess.Enable(_profile.Id, AgentsDir);
        string fingerprint;
        using (var cert = eViSTool.Core.Server.Remote.RemoteAccess.EnsureCertificate(_profile.Id, AgentsDir))
            fingerprint = eViSTool.Core.Server.Remote.RemoteAccess.Fingerprint(cert);
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: true, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).RemotePort == remote.Port);

        // по коду подключения: HTTPS, сертификат сверяется с отпечатком
        var code = new eViSTool.Core.Server.Remote.ConnectionCode("127.0.0.1", remote.Port, remote.Key, fingerprint);
        using (var client = AgentClient.ForRemote(code))
        {
            await Until(async () => (await client.StatusAsync()).State == ServerState.Running);
            await client.CommandAsync("/time");
            await Until(async () => (await client.ConsoleAsync(0, 0)).Any(l => l.Text.Contains("Handling Console Command /time")));
            // выключить агента по сети нельзя — эта точка только для окна на той же машине
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.ShutdownAsync());
        }

        // чужой агент (другой отпечаток) — соединение не устанавливается вовсе
        using (var impostor = AgentClient.ForRemote(code with { Fingerprint = new string('0', 64) }))
            await Assert.ThrowsAsync<HttpRequestException>(() => impostor.StatusAsync());

        // ключ окна не подходит для удалённого входа, и наоборот
        using (var wrong = AgentClient.ForRemote(code with { Key = eViSTool.Core.Server.Remote.RemoteAccess.NewKey() }))
        {
            for (var i = 0; i < 5; i++)
                Assert.Contains("401", (await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.StatusAsync())).Message);
            // после пяти неверных попыток адрес ждёт — даже с верным ключом
            using var right = AgentClient.ForRemote(code);
            Assert.Contains("429", (await Assert.ThrowsAsync<InvalidOperationException>(() => right.StatusAsync())).Message);
        }
        using (var local = new AgentClient(_client.Endpoint, remote.Key))
            await Assert.ThrowsAsync<InvalidOperationException>(() => local.StatusAsync());

        // выключили в настройках — агент сам закрывает сетевой вход
        eViSTool.Core.Server.Remote.RemoteAccess.Disable(_profile.Id, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).RemotePort is null, 15000);
        using (var late = AgentClient.ForRemote(code))
            await Assert.ThrowsAsync<HttpRequestException>(() => late.StatusAsync());
    }

    [Fact]
    public async Task RemoteSchedule_AndBackups_ThroughTheAgent()
    {
        // мир профиля: конфиг указывает на файл сохранения в папке данных
        var saves = Directory.CreateDirectory(Path.Combine(_profile.DataDir!, "Saves")).FullName;
        var save = Path.Combine(saves, "default.vcdbs");
        File.WriteAllText(save, "world v1");
        File.WriteAllText(Path.Combine(_profile.DataDir!, "serverconfig.json"),
            "{ \"WorldConfig\": { \"SaveFileLocation\": \"" + save.Replace("\\", "\\\\") + "\" } }");

        var remote = eViSTool.Core.Server.Remote.RemoteAccess.Enable(_profile.Id, AgentsDir);
        string fingerprint;
        using (var cert = eViSTool.Core.Server.Remote.RemoteAccess.EnsureCertificate(_profile.Id, AgentsDir))
            fingerprint = eViSTool.Core.Server.Remote.RemoteAccess.Fingerprint(cert);
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).RemotePort == remote.Port);
        using var client = AgentClient.ForRemote(new eViSTool.Core.Server.Remote.ConnectionCode("127.0.0.1", remote.Port, remote.Key, fingerprint));
        IServerData data = new RemoteServerData(() => client);

        // настройки расписания: прочитать, поменять, прочитать снова — и файл на «сервере» тот же
        var settings = await data.LoadAutomationAsync();
        await data.SaveAutomationAsync(settings with { BackupEnabled = true, BackupKeep = 3, RestartMode = RestartMode.Daily });
        var saved = await data.LoadAutomationAsync();
        Assert.Equal((true, 3, RestartMode.Daily), (saved.BackupEnabled, saved.BackupKeep, saved.RestartMode));
        Assert.True(ServerAutomation.Load(_profile.Id, AgentsDir).BackupEnabled);

        // копия при остановленном сервере — делает агент
        var made = await data.CopyWorldAsync();
        Assert.StartsWith("Тест_мир-", made.Name);
        Assert.True(made.IsOwn);
        Assert.Null(made.LocalPath); // путь на чужой машине окну не нужен
        Assert.Contains(await data.ListBackupsAsync(), b => b.Name == made.Name && b.Size == made.Size);

        // восстановление: мир из копии, прежний — рядом
        File.WriteAllText(save, "world v2");
        var restored = await data.RestoreAsync(made.Name);
        Assert.Equal("world v1", File.ReadAllText(save));
        Assert.Contains("before-restore", restored.SafetyName);

        // чужие пути и несуществующие копии — отказ
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.RestoreAsync(@"..\..\serverconfig.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.DeleteBackupAsync("нет-такой.vcdbs"));

        // сервер работает — файл мира не трогаем
        await client.StartAsync();
        await Until(async () => (await client.StatusAsync()).State == ServerState.Running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.CopyWorldAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.RestoreAsync(made.Name));
    }
}
