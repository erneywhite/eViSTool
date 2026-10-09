using eViSTool.Core.Mods;
using System.Diagnostics;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Настоящий eViSTool.Agent + поддельный сервер, управление по HTTP — как из окна.</summary>
public sealed class AgentTests : IAsyncLifetime
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    // EVISTOOL_TEST_AGENT — проверить те же сценарии на релизном (обрезанном) агенте из build/publish.ps1
    private static readonly string AgentExe = Environment.GetEnvironmentVariable("EVISTOOL_TEST_AGENT")
        ?? Path.Combine(Root, "src", "eViSTool.Agent", "bin", "Debug", "net10.0", AgentProtocol.ExeName); // на Linux — без .exe

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "evistool-agent-" + Guid.NewGuid().ToString("N"));
    private string AgentsDir => Path.Combine(_tmp, "agents");
    private GameProfile _profile = null!;
    private AgentClient? _client;

    public Task InitializeAsync()
    {
        // «папка игры»: поддельный сервер под именем настоящего (на Windows — exe, на Linux — dll через dotnet)
        var game = Directory.CreateDirectory(Path.Combine(_tmp, "game")).FullName;
        FakeServer.InstallAs(game);
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

    /// <summary>Сервер с модом CrashTest (настоящая DLL с пространством имён CrashTestMod) в своей папке модов.</summary>
    private void ServerWithCrashTestMod()
    {
        var mods = Directory.CreateDirectory(Path.Combine(_profile.DataDir!, "Mods")).FullName;
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "Crash", "CrashTest_1.0.0.zip"), Path.Combine(mods, "CrashTest_1.0.0.zip"));
        File.WriteAllText(Path.Combine(_profile.DataDir!, "serverconfig.json"),
            Newtonsoft.Json.JsonConvert.SerializeObject(new { ModPaths = new[] { "Mods", mods } }));
    }

    [Theory]
    [InlineData("/moderrors CrashTestMod", "Stack")]   // ошибки со стеком мода → «too many errors» → сервер выключился сам
    [InlineData("/modfatal crashtest", "GameReport")]  // отчёт о вылете, где сервер сам назвал мод
    public async Task ServerStoppedByAMod_TheAgentNamesTheMod(string command, string source)
    {
        ServerWithCrashTestMod();
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: true, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Running);
        Assert.Null((await _client.StatusAsync()).LastCrash);

        await _client.CommandAsync(command);
        await Until(async () => (await _client.StatusAsync()).LastCrash is not null);

        var crash = (await _client.StatusAsync()).LastCrash!;
        Assert.Equal("crashtest", crash.ModId);
        Assert.Equal(source, crash.Source.ToString());
        Assert.EndsWith("CrashTest_1.0.0.zip", crash.ModPath); // по этому пути окно выключит мод через агента
        Assert.False(string.IsNullOrEmpty(crash.Id));
        // и строка в консоли сервера — видно и без окна
        Assert.Contains((await _client.ConsoleAsync(0, 0)), l => l.Kind == ConsoleLineKind.System && l.Text.Contains("Crash Test"));

        if (source == "Stack")
        {
            // «ошибки модов» за этот запуск — для «!» у профиля в окне
            await Until(async () => (await _client.StatusAsync()).ModErrors is not null);
            var errors = (await _client.StatusAsync()).ModErrors!;
            var line = Assert.Single(errors.Mods);
            Assert.Equal(("crashtest", 5), (line.ModId, line.Count));
        }
    }

    [Fact]
    public async Task ServerStoppedFromTheWindow_IsNotACrash()
    {
        ServerWithCrashTestMod();
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: true, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Running);

        await _client.StopAsync();
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Stopped);
        await Task.Delay(500);
        Assert.Null((await _client.StatusAsync()).LastCrash);
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

        // команды для подсказок — из ответа сервера на /help; запоминаются для профиля
        await _client.CommandAsync("/help");
        await Until(async () => (await _client.StatusAsync()).CommandCount == 3);
        var tp = (await _client.CommandsAsync()).Single(c => c.Name == "tp");
        Assert.Equal("/tp <source> <target>", tp.Usage);
        await Until(() => Task.FromResult(File.Exists(ServerCommands.FileFor(_profile.Id, AgentsDir))));

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
        // мод хранит прогресс рядом с миром — он должен попасть в копию вместе с миром
        Directory.CreateDirectory(Path.Combine(_profile.DataDir!, "Saves", "XLeveling"));
        File.WriteAllText(Path.Combine(_profile.DataDir!, "Saves", "XLeveling", "Erney.json"), "skills");

        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: true, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Running);
        // мир держит сервер в другом процессе (агенте) — окну на этой машине восстановление не начать
        Assert.Null(WorldLock.TryTake(_profile.DataDir!, WorldLock.Restore));
        Assert.Equal(WorldLock.Server, WorldLock.HolderOf(_profile.DataDir!));
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
        await Until(() => Task.FromResult(store.List().Single(b => b.Name == fresh).ModDataSize > 0));

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

        // сервер упал: часть мира только в журнале SQLite — копия через агента получает её внутрь, одним файлом
        await Task.Delay(1100); // имя копии — по секундам
        SqliteWorld.WriteCrashed(save, saved: ["a"], pending: ["b"]);
        var crashed = await data.CopyWorldAsync();
        Assert.Equal(["a", "b"], SqliteWorld.Read(Path.Combine(_profile.DataDir!, "Backups", crashed.Name)));

        // чужие пути и несуществующие копии — отказ
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.RestoreAsync(@"..\..\serverconfig.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.DeleteBackupAsync("нет-такой.vcdbs"));

        // сервер работает — файл мира не трогаем
        await client.StartAsync();
        await Until(async () => (await client.StatusAsync()).State == ServerState.Running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.CopyWorldAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.RestoreAsync(made.Name));
    }

    [Fact]
    public async Task RemoteConfig_ReadWrite_WithVersionCheck()
    {
        var config = Path.Combine(_profile.DataDir!, "serverconfig.json");
        File.WriteAllText(config, "{ \"ServerName\": \"Old\", \"Port\": 42420 }");

        var remote = eViSTool.Core.Server.Remote.RemoteAccess.Enable(_profile.Id, AgentsDir);
        string fingerprint;
        using (var cert = eViSTool.Core.Server.Remote.RemoteAccess.EnsureCertificate(_profile.Id, AgentsDir))
            fingerprint = eViSTool.Core.Server.Remote.RemoteAccess.Fingerprint(cert);
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).RemotePort == remote.Port);
        using var client = AgentClient.ForRemote(new eViSTool.Core.Server.Remote.ConnectionCode("127.0.0.1", remote.Port, remote.Key, fingerprint));

        var file = await client.GetConfigAsync();
        Assert.True(file.Exists);
        Assert.Contains("Old", file.Text);
        Assert.NotNull((await client.StatusAsync()).ConfigChangedAt);

        // версия та, что читали, — записывается (с копией прежнего файла рядом)
        var saved = await client.SaveConfigAsync(new ConfigSaveRequest("{ \"ServerName\": \"New\", \"Port\": 42420 }", file.Stamp, Force: false));
        Assert.False(saved.Conflict);
        Assert.Contains("New", File.ReadAllText(config));
        Assert.True(File.Exists(config + ".evistool.bak"));

        // файл на сервере успел поменяться — отказ, ничего не записано
        await Task.Delay(20);
        File.WriteAllText(config, "{ \"ServerName\": \"Changed on server\", \"Port\": 42420 }");
        var conflict = await client.SaveConfigAsync(new ConfigSaveRequest("{ \"ServerName\": \"Mine\" }", saved.File.Stamp, Force: false));
        Assert.True(conflict.Conflict);
        Assert.Contains("Changed on server", File.ReadAllText(config));
        Assert.Contains("Changed on server", conflict.File.Text);

        // перезаписать по согласию
        var forced = await client.SaveConfigAsync(new ConfigSaveRequest("{ \"ServerName\": \"Mine\" }", saved.File.Stamp, Force: true));
        Assert.False(forced.Conflict);
        Assert.Contains("Mine", File.ReadAllText(config));

        // не JSON — отказ, файл цел
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SaveConfigAsync(new ConfigSaveRequest("не json", forced.File.Stamp, Force: true)));
        Assert.Contains("Mine", File.ReadAllText(config));

        // сервер работает — конфиг не трогаем (он перезапишет файл при остановке)
        await client.StartAsync();
        await Until(async () => (await client.StatusAsync()).State == ServerState.Running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SaveConfigAsync(new ConfigSaveRequest("{}", forced.File.Stamp, Force: true)));
    }

    private static string MakeModZip(string dir, string fileName, string modId, string version)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
        w.Write($$"""{ "modid": "{{modId}}", "name": "{{modId}}", "version": "{{version}}", "dependencies": { "game": "" } }""");
        return path;
    }

    [Fact]
    public async Task RemoteMods_List_Toggle_Install_Delete()
    {
        var mods = Path.Combine(_profile.DataDir!, "Mods");
        File.WriteAllText(Path.Combine(_profile.DataDir!, "serverconfig.json"),
            "{ \"ModPaths\": [\"Mods\", " + Newtonsoft.Json.JsonConvert.ToString(mods) + "], \"WorldConfig\": { \"DisabledMods\": [] } }");
        var carry = MakeModZip(mods, "carryon_1.0.0.zip", "CarryOn", "1.0.0");
        MakeModZip(mods, "other_2.0.0.zip", "other", "2.0.0");

        var remote = eViSTool.Core.Server.Remote.RemoteAccess.Enable(_profile.Id, AgentsDir);
        string fingerprint;
        using (var cert = eViSTool.Core.Server.Remote.RemoteAccess.EnsureCertificate(_profile.Id, AgentsDir))
            fingerprint = eViSTool.Core.Server.Remote.RemoteAccess.Fingerprint(cert);
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).RemotePort == remote.Port);
        var code = new eViSTool.Core.Server.Remote.ConnectionCode("127.0.0.1", remote.Port, remote.Key, fingerprint);
        using var client = AgentClient.ForRemote(code);
        var backups = Path.Combine(Path.GetDirectoryName(AgentExe)!, "data", "ModBackups", _profile.Id);
        try
        {
            // список: оба мода, папки и modinfo доезжают до окна целиком
            var list = await client.ModsAsync();
            Assert.Equal(2, list.Mods.Count);
            Assert.Equal(mods, list.InstallDir);
            var info = list.Mods.Single(m => m.Info?.ModId == "carryon").Info!;
            Assert.Equal("CarryOn", info.OriginalModId);
            Assert.True(info.Dependencies.ContainsKey("game"));
            Assert.NotNull(list.ChangedAt);

            // выключить — как игра: modid@версия не нужен, пишется OriginalModId в WorldConfig.DisabledMods
            await client.SetModEnabledAsync(carry, enabled: false);
            list = await client.ModsAsync();
            Assert.Contains("CarryOn", list.DisabledMods);
            var stamp = (await client.StatusAsync()).ModsChangedAt;

            // новая версия архивом по сети: старая уходит в хранилище, выключенный мод остаётся выключенным
            await Task.Delay(20);
            var incoming = MakeModZip(Path.Combine(_tmp, "upload"), "carryon_1.1.0.zip", "CarryOn", "1.1.0");
            var installed = await client.InstallModAsync(incoming);
            Assert.Equal(("CarryOn", "1.1.0", "1.0.0"), (installed.Name, installed.Version, installed.OldVersion));
            Assert.False(File.Exists(carry));
            Assert.True(File.Exists(Path.Combine(mods, "carryon_1.1.0.zip")));
            Assert.Single(Directory.GetFiles(Path.Combine(backups, "carryon")));
            list = await client.ModsAsync();
            Assert.Contains("CarryOn", list.DisabledMods);
            Assert.NotEqual(stamp, (await client.StatusAsync()).ModsChangedAt);

            // не мод — отказ с понятной причиной, ничего не поставлено
            var junk = Path.Combine(_tmp, "upload", "junk.zip");
            File.WriteAllText(junk, "not a zip");
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.InstallModAsync(junk));
            Assert.False(File.Exists(Path.Combine(mods, "junk.zip")));

            // чужой путь (не из списка модов) не удаляется и не выключается
            var outside = Path.Combine(_tmp, "upload", "carryon_1.1.0.zip");
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteModAsync(outside));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetModEnabledAsync(outside, false));
            Assert.True(File.Exists(outside));

            // цель установки — удалённый сервер по коду: мод уходит на него, а не в «текущий» профиль этой машины
            var target = new ModTarget(new GameProfile { Id = "remote-test", Name = "Remote", Kind = ProfileKind.Server }, code);
            var viaTarget = await ModTargets.InstallAsync(target, MakeModZip(Path.Combine(_tmp, "upload"), "gamma_0.5.0.zip", "gamma", "0.5.0"));
            Assert.Equal("gamma 0.5.0", viaTarget!.Text);
            Assert.True(File.Exists(Path.Combine(mods, "gamma_0.5.0.zip")));
            var (_, remoteMods) = await ModTargets.ScanAsync(target);
            await ModTargets.SetEnabledAsync(target, remoteMods.Single(m => m.Info?.ModId == "gamma"), enabled: false);
            Assert.Contains("gamma", (await client.ModsAsync()).DisabledMods);

            // удаление последней копии — в корзину, и из списка выключенных тоже
            await client.DeleteModAsync(Path.Combine(mods, "carryon_1.1.0.zip"));
            list = await client.ModsAsync();
            Assert.Equal(["gamma", "other"], list.Mods.Select(m => m.Info!.ModId).Order());
            Assert.DoesNotContain("CarryOn", list.DisabledMods);
        }
        finally
        {
            try { Directory.Delete(backups, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Players_ReadAndEdit_OnlyWhileStopped()
    {
        var pd = Directory.CreateDirectory(Path.Combine(_profile.DataDir!, "Playerdata")).FullName;
        File.WriteAllText(Path.Combine(pd, "playerdata.json"),
            """[ { "PlayerUID": "UID-ANNA", "RoleCode": "suplayer", "LastKnownPlayername": "Anna", "LastJoinDate": "2026-10-07T21:00:00+03:00" } ]""");
        File.WriteAllText(Path.Combine(pd, "playersbanned.json"),
            """[ { "PlayerUID": "UID-ANNA", "PlayerName": "Anna", "UntilDate": "2099-01-01T00:00:00+03:00", "Reason": "test", "IssuedByPlayerName": "Console" } ]""");

        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir);
        var view = await _client.PlayersAsync();
        Assert.Equal("Anna", Assert.Single(view.Players).Name);
        Assert.NotNull(view.Players[0].Ban);
        Assert.NotNull((await _client.StatusAsync()).PlayersChangedAt);

        // сервер остановлен — правка файлов
        await _client.EditPlayersAsync(new PlayerFileEdit("UID-ANNA", Role: "admin", Unban: true));
        view = await _client.PlayersAsync();
        Assert.Equal("admin", view.Players[0].Role);
        Assert.Empty(view.Bans);

        // сервер запущен — файлы не трогаем (он перезапишет их сам): отказ с понятной причиной
        await _client.StartAsync();
        await Until(async () => (await _client.StatusAsync()).State == ServerState.Running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.EditPlayersAsync(new PlayerFileEdit("UID-ANNA", Role: "suplayer")));
        Assert.Equal("admin", (await _client.PlayersAsync()).Players[0].Role);
    }

    [Fact]
    public async Task SelfUpdate_ToTheSameVersion_IsRefused_AndNothingStarts()
    {
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir);
        var version = (await _client.StatusAsync()).AgentVersion;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.SelfUpdateAsync(version));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _client.SelfUpdateAsync("not a version"));
        Assert.Null((await _client.StatusAsync()).SelfUpdate);
    }

    [Fact]
    public async Task Notify_SavedThroughTheAgent_SecretEncryptedThere_ReturnedWithout()
    {
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir);
        var channel = new Notifications.NotifyChannel
        {
            Id = "c1", Name = "tg", Kind = Notifications.NotifyKind.Telegram, ChatId = "1",
            SecretProtected = Notifications.NotifySecret.Protect("123456789:ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ"),
        };
        var settings = new Notifications.ServerNotifySettings
        {
            ServerName = "Srv", Channels = [channel], Routes = new() { [Notifications.NotifyEvent.ServerCrashed] = ["c1"] },
        };

        await _client.SaveNotifyAsync(settings.ToUpload());

        var back = await _client.GetNotifyAsync();
        Assert.True(back.IsOn(Notifications.NotifyEvent.ServerCrashed, "c1"));
        Assert.Null(Assert.Single(back.Channels).SecretProtected); // секрет окну не отдаётся
        Assert.NotNull((await _client.StatusAsync()).NotifyChangedAt);
        // на машине агента секрет есть и зашифрован
        var onDisk = Notifications.ServerNotifySettings.Load(_profile.Id, AgentsDir);
        Assert.Equal("123456789:ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ", onDisk.Channels[0].Secret);
        Assert.DoesNotContain("ZZZZZZZZ", File.ReadAllText(Notifications.ServerNotifySettings.FileFor(_profile.Id, AgentsDir)));
    }

    [Fact]
    public async Task Announcements_SavedThroughTheAgent_WithAChangeStamp()
    {
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir);
        Assert.Empty((await _client.GetAnnouncementsAsync()).Items);
        Assert.Null((await _client.StatusAsync()).AnnouncementsChangedAt);

        await _client.SaveAnnouncementsAsync(new ServerAnnouncements { OnlyWithPlayers = false, Items = [new Announcement("Привет", 15)] });

        var back = await _client.GetAnnouncementsAsync();
        Assert.False(back.OnlyWithPlayers);
        Assert.Equal(new Announcement("Привет", 15), Assert.Single(back.Items));
        Assert.NotNull((await _client.StatusAsync()).AnnouncementsChangedAt);
        // файл — рядом с настройками расписания этого профиля, там его читает и окно на этой машине
        Assert.Equal("Привет", ServerAnnouncements.Load(_profile.Id, AgentsDir).Items[0].Text);
    }

    [Fact]
    public async Task RemotePackImport_ThroughTheAgent()
    {
        var mods = Path.Combine(_profile.DataDir!, "Mods");
        File.WriteAllText(Path.Combine(_profile.DataDir!, "serverconfig.json"),
            "{ \"ModPaths\": [\"Mods\", " + Newtonsoft.Json.JsonConvert.ToString(mods) + "], \"WorldConfig\": { \"DisabledMods\": [] } }");
        MakeModZip(mods, "carryon_1.0.0.zip", "CarryOn", "1.0.0");
        MakeModZip(mods, "other_2.0.0.zip", "other", "2.0.0");

        // пак: новая версия CarryOn, новый мод (выключенный) и настройки модов — текст и картинка
        var src = Path.Combine(_tmp, "packsrc");
        var carry = MakeModZip(src, "carryon_1.1.0.zip", "CarryOn", "1.1.0");
        var fresh = MakeModZip(src, "fresh_1.0.0.zip", "fresh", "1.0.0");
        var packPath = Path.Combine(_tmp, "test.evpack");
        using (var zip = System.IO.Compression.ZipFile.Open(packPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, carry, eViSTool.Core.Packs.PackManifest.ModsFolder + "carryon_1.1.0.zip");
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, fresh, eViSTool.Core.Packs.PackManifest.ModsFolder + "fresh_1.0.0.zip");
            using (var w = new StreamWriter(zip.CreateEntry(eViSTool.Core.Packs.PackManifest.ConfigFolder + "CarryOnConfig.json").Open()))
                w.Write("{ \"Speed\": 2.0 }");
            using (var w = zip.CreateEntry(eViSTool.Core.Packs.PackManifest.ConfigFolder + "icon.png").Open()) w.Write([1, 2, 3]);
            var manifest = new eViSTool.Core.Packs.PackManifest
            {
                Name = "remote", IncludesModConfig = true, Kind = ProfileKind.Server,
                Mods =
                [
                    new() { ModId = "carryon", Name = "CarryOn", Version = "1.1.0", FileName = "carryon_1.1.0.zip", Bundled = true,
                            Sha256 = eViSTool.Core.Packs.PackBuilder.Sha256Of(carry) },
                    new() { ModId = "fresh", Name = "fresh", Version = "1.0.0", FileName = "fresh_1.0.0.zip", Bundled = true, Enabled = false,
                            Sha256 = eViSTool.Core.Packs.PackBuilder.Sha256Of(fresh) },
                ],
            };
            using var mw = new StreamWriter(zip.CreateEntry(eViSTool.Core.Packs.PackManifest.FileName).Open());
            mw.Write(Newtonsoft.Json.JsonConvert.SerializeObject(manifest));
        }

        var remote = eViSTool.Core.Server.Remote.RemoteAccess.Enable(_profile.Id, AgentsDir);
        string fingerprint;
        using (var cert = eViSTool.Core.Server.Remote.RemoteAccess.EnsureCertificate(_profile.Id, AgentsDir))
            fingerprint = eViSTool.Core.Server.Remote.RemoteAccess.Fingerprint(cert);
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).RemotePort == remote.Port);
        var code = new eViSTool.Core.Server.Remote.ConnectionCode("127.0.0.1", remote.Port, remote.Key, fingerprint);
        var target = new ModTarget(new GameProfile { Id = "remote-pack", Name = "Remote", Kind = ProfileKind.Server }, code);
        var appData = Path.Combine(Path.GetDirectoryName(AgentExe)!, "data");
        try
        {
            using var pack = eViSTool.Core.Packs.PackFile.Open(packPath);
            var (resolved, locals) = await ModTargets.ScanAsync(target);
            var plan = eViSTool.Core.Packs.PackImporter.Plan(pack.Manifest, resolved, locals);
            using var db = new eViSTool.Core.ModDb.ModDbClient();
            var importer = new eViSTool.Core.Packs.PackImporter(db, new ModUpdater(db, Path.Combine(_tmp, "dl")));

            var result = await importer.ApplyToRemoteAsync(pack, plan, target, mirror: true, applyConfig: true);

            // моды доехали на сервер, старая версия — в хранилище там же
            Assert.True(File.Exists(Path.Combine(mods, "carryon_1.1.0.zip")));
            Assert.False(File.Exists(Path.Combine(mods, "carryon_1.0.0.zip")));
            Assert.True(File.Exists(Path.Combine(mods, "fresh_1.0.0.zip")));
            // включение — как в паке, лишний «other» при зеркалировании выключен
            var list = await ModTargets.ScanAsync(target);
            var disabled = list.Profile.DisabledMods;
            Assert.Contains("fresh", disabled);
            Assert.Contains("other", disabled);
            Assert.DoesNotContain("CarryOn", disabled);
            Assert.Contains(result.Done, d => d.Contains("other"));
            // настройки: текст — на сервере, картинка — нет, о ней строка в отчёте
            Assert.Equal("{ \"Speed\": 2.0 }", File.ReadAllText(Path.Combine(_profile.DataDir!, "ModConfig", "CarryOnConfig.json")));
            Assert.False(File.Exists(Path.Combine(_profile.DataDir!, "ModConfig", "icon.png")));
            Assert.Contains(result.Problems, p => p.Contains("icon.png"));
        }
        finally
        {
            try { Directory.Delete(Path.Combine(appData, "ModBackups", _profile.Id), recursive: true); } catch (IOException) { }
            try { Directory.Delete(Path.Combine(appData, "ModConfigBackups", _profile.Id), recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RemoteModConfigs_ReadSaveUndoReset_ThroughTheAgent()
    {
        var mods = Path.Combine(_profile.DataDir!, "Mods");
        File.WriteAllText(Path.Combine(_profile.DataDir!, "serverconfig.json"),
            "{ \"ModPaths\": [\"Mods\", " + Newtonsoft.Json.JsonConvert.ToString(mods) + "] }");
        MakeModZip(mods, "carryon_1.0.0.zip", "CarryOn", "1.0.0");
        var cfgDir = Directory.CreateDirectory(Path.Combine(_profile.DataDir!, "ModConfig")).FullName;
        File.WriteAllText(Path.Combine(cfgDir, "CarryOnConfig.json"), "{\n  \"Speed\": 1.0\n}");
        File.WriteAllText(Path.Combine(_profile.DataDir!, "serverconfig-secret.json"), "{}");

        var remote = eViSTool.Core.Server.Remote.RemoteAccess.Enable(_profile.Id, AgentsDir);
        string fingerprint;
        using (var cert = eViSTool.Core.Server.Remote.RemoteAccess.EnsureCertificate(_profile.Id, AgentsDir))
            fingerprint = eViSTool.Core.Server.Remote.RemoteAccess.Fingerprint(cert);
        _client = await AgentLauncher.EnsureRunningAsync(_profile, startServer: false, AgentExe, AgentsDir);
        await Until(async () => (await _client.StatusAsync()).RemotePort == remote.Port);
        var source = new RemoteModConfigSource(new eViSTool.Core.Server.Remote.ConnectionCode("127.0.0.1", remote.Port, remote.Key, fingerprint));
        var backups = Path.Combine(Path.GetDirectoryName(AgentExe)!, "data", "ModConfigBackups", _profile.Id);
        try
        {
            // список с модом, угаданным по имени файла
            var entry = Assert.Single(await source.ListAsync());
            Assert.Equal(("CarryOnConfig.json", "carryon"), (entry.RelativePath, entry.ModId));

            var opened = await source.ReadAsync("CarryOnConfig.json");
            Assert.Equal("{\n  \"Speed\": 1.0\n}", opened.Text);

            // запись: прежняя версия остаётся на сервере
            var saved = await source.SaveAsync(new ModConfigSaveRequest("CarryOnConfig.json", "{\n  \"Speed\": 2.0\n}", opened.ChangedUtc));
            Assert.False(saved.Changed);
            Assert.Equal(1, saved.Content!.Versions);
            Assert.Contains("2.0", File.ReadAllText(Path.Combine(cfgDir, "CarryOnConfig.json")));
            Assert.Single(Directory.GetFiles(backups, "*", SearchOption.AllDirectories));

            // поменяли с момента открытия (второе окно) — без вопроса не перезаписывается
            var stale = await source.SaveAsync(new ModConfigSaveRequest("CarryOnConfig.json", "{}", opened.ChangedUtc));
            Assert.True(stale.Changed);

            // шаг назад, сброс и его отмена
            Assert.Contains("1.0", (await source.UndoAsync("CarryOnConfig.json")).Text);
            Assert.False((await source.ResetAsync("CarryOnConfig.json")).Exists);
            Assert.Contains("1.0", (await source.UndoAsync("CarryOnConfig.json")).Text);

            // за пределы ModConfig — отказ, файл рядом цел
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync("../serverconfig-secret.json"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                source.SaveAsync(new ModConfigSaveRequest("../serverconfig-secret.json", "{ \"x\": 1 }", null)));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(_profile.DataDir!, "serverconfig-secret.json")));

            // сломанный JSON не записывается
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                source.SaveAsync(new ModConfigSaveRequest("CarryOnConfig.json", "{ \"Speed\": ", null)));
        }
        finally
        {
            try { Directory.Delete(backups, recursive: true); } catch (IOException) { }
        }
    }
}
