using System.IO.Compression;
using System.Net.Http;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Remote;
using eViSTool.Presentation.ViewModels;
using Newtonsoft.Json.Linq;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Выключение мода из окна вылета (и «Ошибок модов»): отложить, пока игра открыта; сразу — и перечитать моды;
/// ошибки — словами; цель берётся, когда окно открывается. Игра, главная модель и агент подменены делегатами, а
/// настоящая запись в конфиг проверяется на временной папке (данные пользователя не трогаются).
/// </summary>
public sealed class ModDisablerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-moddisabler-" + Guid.NewGuid().ToString("N"));

    public ModDisablerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly GameProfile Client = new() { Id = "client-1", Name = "Игра", Kind = ProfileKind.Client };
    private static readonly GameProfile Server = new() { Id = "server-1", Name = "Сервер", Kind = ProfileKind.Server };

    /// <summary>Что делал выключатель: игра открыта, отложенные дела, перечитывания, выключения (цель и путь).</summary>
    private sealed class Probe
    {
        public bool GameRunning { get; set; }
        public int GameChecks { get; private set; }
        public int TargetCalls { get; private set; }
        public List<(GameProfile Profile, Func<Task> Action)> Deferred { get; } = [];
        public List<GameProfile> Reloaded { get; } = [];
        public List<(ModTarget Target, string Path)> Disabled { get; } = [];
        /// <summary>Чем ответит выключение: null — удачно.</summary>
        public Exception? Fail { get; set; }
        /// <summary>Цели нет (удалённый сервер, код не прочитать).</summary>
        public bool NoTarget { get; set; }

        public ModDisabler Disabler() => new(
            p => { GameChecks++; return GameRunning; },
            (p, action) => Deferred.Add((p, action)),
            p => Reloaded.Add(p),
            p => { TargetCalls++; return NoTarget ? null : new ModTarget(p, null); },
            (target, path) =>
            {
                if (Fail is { } ex) return Task.FromException(ex);
                Disabled.Add((target, path));
                return Task.CompletedTask;
            });
    }

    // ---- когда выключать нечего

    [Fact]
    public void NoPath_NothingToDisable()
    {
        var probe = new Probe();
        Assert.Null(probe.Disabler().For(Client, null, "Carry On"));
        Assert.Equal(0, probe.TargetCalls); // без пути и цель не нужна
    }

    [Fact]
    public void NoTarget_NothingToDisable()
    {
        var probe = new Probe { NoTarget = true };
        Assert.Null(probe.Disabler().For(Server, @"C:\Mods\carryon.zip", "Carry On"));
    }

    // удалённый сервер, код которого на этой машине не прочитать (скопировали настройки с другого компьютера), — цели нет
    [Fact]
    public void RemoteServer_WithUnreadableCode_NothingToDisable()
    {
        var remote = new GameProfile { Id = "remote-1", Name = "Чужой", Kind = ProfileKind.Server, RemoteCode = "не-код" };
        var disabler = new ModDisabler(_ => false, (_, _) => { }, _ => { });
        Assert.Null(disabler.For(remote, "/srv/vs/Mods/carryon.zip", "Carry On"));
    }

    // ---- клиент: игра открыта — после её выхода, закрыта — сразу

    [Fact]
    public async Task Client_GameRunning_DisablesAfterExit()
    {
        var probe = new Probe { GameRunning = true };
        var disable = probe.Disabler().For(Client, @"C:\Mods\carryon.zip", "Carry On")!;

        var result = await disable();

        Assert.Equal(new ModDisableResult(Loc.T("crash.disableAfterExit", "Carry On"), Failed: false), result);
        Assert.Empty(probe.Disabled); // пока игра открыта — ничего не трогаем
        Assert.Empty(probe.Reloaded); // моды после отложенного перечитает сама программа, когда игра выйдет
        var (profile, action) = Assert.Single(probe.Deferred);
        Assert.Same(Client, profile);

        await action(); // игра закрылась
        var (target, path) = Assert.Single(probe.Disabled);
        Assert.Equal(Client.Id, target.ProfileId);
        Assert.Equal(@"C:\Mods\carryon.zip", path);
    }

    [Fact]
    public async Task Client_GameClosed_DisablesNow_AndReloadsMods()
    {
        var probe = new Probe();
        var result = await probe.Disabler().For(Client, @"C:\Mods\carryon.zip", "Carry On")!();

        Assert.Equal(new ModDisableResult(Loc.T("crash.disabled", "Carry On"), Failed: false), result);
        Assert.Empty(probe.Deferred);
        Assert.Equal(@"C:\Mods\carryon.zip", Assert.Single(probe.Disabled).Path);
        Assert.Same(Client, Assert.Single(probe.Reloaded));
    }

    // игру программа отслеживает только у клиентского профиля: у сервера мод выключается сразу, в его конфиге
    [Fact]
    public async Task Server_DisablesNow_WithServerText()
    {
        var probe = new Probe { GameRunning = true };
        var result = await probe.Disabler().For(Server, "/srv/vs/Mods/carryon.zip", "Carry On")!();

        Assert.Equal(new ModDisableResult(Loc.T("crash.disabledServer", "Carry On"), Failed: false), result);
        Assert.Equal(0, probe.GameChecks);
        Assert.Empty(probe.Deferred);
        Assert.Equal("/srv/vs/Mods/carryon.zip", Assert.Single(probe.Disabled).Path);
        Assert.Same(Server, Assert.Single(probe.Reloaded));
    }

    // цель — снимок на момент, когда окно открылось: её не берут заново при каждом нажатии
    [Fact]
    public async Task Target_IsTakenWhenWindowOpens()
    {
        var probe = new Probe();
        var disable = probe.Disabler().For(Server, "/srv/a.zip", "A")!;
        Assert.Equal(1, probe.TargetCalls);

        await disable();
        probe.Fail = new IOException("занят");
        await disable();
        Assert.Equal(1, probe.TargetCalls);
    }

    // ---- ошибки

    public static TheoryData<Exception> ExpectedFailures() => new()
    {
        new IOException("Файл занят другим процессом"),
        new InvalidOperationException("Настроек игры ещё нет"),
        new UnauthorizedAccessException("Нет доступа"),
        new HttpRequestException("Нет связи с агентом"),
        new TaskCanceledException("Агент не ответил"),
    };

    [Theory]
    [MemberData(nameof(ExpectedFailures))]
    public async Task ExpectedFailure_IsReported_AndNothingReloaded(Exception failure)
    {
        var probe = new Probe { Fail = failure };
        var result = await probe.Disabler().For(Client, @"C:\Mods\carryon.zip", "Carry On")!();

        Assert.Equal(new ModDisableResult(Loc.T("crash.disableFailed", failure.Message), Failed: true), result);
        Assert.Empty(probe.Reloaded);
        Assert.True(ModDisabler.IsExpectedFailure(failure));
    }

    // чего не ждали — не глотаем: уходит в общий обработчик ошибок программы, как в окне на WPF
    [Fact]
    public async Task UnexpectedFailure_IsNotSwallowed()
    {
        var probe = new Probe { Fail = new ArgumentException("ошибка в коде") };
        var disable = probe.Disabler().For(Client, @"C:\Mods\carryon.zip", "Carry On")!;
        await Assert.ThrowsAsync<ArgumentException>(disable);
        Assert.False(ModDisabler.IsExpectedFailure(new ArgumentException()));
    }

    // ---- мод по пути и настоящая запись в конфиг (временная папка)

    private string ModZip(string modId, string version = "1.0.0", string? name = null)
    {
        var path = Path.Combine(_root, $"{modId}_{version}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("modinfo.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(new JObject { ["type"] = "code", ["modid"] = modId, ["name"] = name ?? modId, ["version"] = version }.ToString());
        return path;
    }

    // у удалённого сервера путь — на его машине: архив не читается, агенту хватит пути
    [Fact]
    public void ModAt_Remote_TakesPathOnly()
    {
        var code = new ConnectionCode("192.0.2.10", 42420, "made-up-key-for-tests", new string('a', 64));
        var mod = ModDisabler.ModAt(new ModTarget(Server, code), "/srv/vs/Mods/carryon.zip");
        Assert.Equal(new LocalMod("/srv/vs/Mods/carryon.zip", null, null), mod);
    }

    [Fact]
    public void ModAt_Local_ReadsArchive()
    {
        var mod = ModDisabler.ModAt(new ModTarget(Server, null), ModZip("crashtest", "1.2.0", "Crash Test"));
        Assert.Null(mod.Error);
        Assert.Equal("crashtest", mod.Info!.ModId);
        Assert.Equal("1.2.0", mod.Info.Version);
    }

    [Fact]
    public async Task Default_LocalServer_WritesDisabledModsToServerConfig()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "server-data")).FullName;
        var config = Path.Combine(data, ProfileResolver.ServerConfigName);
        File.WriteAllText(config, """{ "ModPaths": [ "Mods" ], "WorldConfig": { "DisabledMods": [] } }""");
        var server = new GameProfile { Id = "srv-" + Guid.NewGuid().ToString("N")[..6], Name = "Свой", Kind = ProfileKind.Server, DataDir = data };
        var reloaded = new List<GameProfile>();
        var disabler = new ModDisabler(_ => false, (_, _) => throw new InvalidOperationException("откладывать не должны"), reloaded.Add);

        var result = await disabler.For(server, ModZip("crashtest"), "Crash Test")!();

        Assert.Equal(new ModDisableResult(Loc.T("crash.disabledServer", "Crash Test"), Failed: false), result);
        var disabled = JObject.Parse(File.ReadAllText(config))["WorldConfig"]!["DisabledMods"]!.Select(t => t.ToString());
        Assert.Contains("crashtest", disabled);
        Assert.Same(server, Assert.Single(reloaded));
    }

    // игра профиля ещё ни разу не запускалась — её настроек нет: выключить негде, это ошибка словами, а не падение
    [Fact]
    public async Task Default_ClientWithoutSettings_FailsWithText()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "client-data")).FullName;
        var client = new GameProfile { Id = "cl-" + Guid.NewGuid().ToString("N")[..6], Name = "Игра", Kind = ProfileKind.Client, DataDir = data };
        var disabler = new ModDisabler(_ => false, (_, _) => { }, _ => { });

        var result = await disabler.For(client, ModZip("crashtest"), "Crash Test")!();

        Assert.Equal(new ModDisableResult(Loc.T("crash.disableFailed", Loc.T("cfg.noClientConfigRun")), Failed: true), result);
    }

    // архив не прочитать (битый или удалён) — мод с ошибкой, и окно говорит, что с ним
    [Fact]
    public async Task Default_BrokenArchive_FailsWithText()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "client-data")).FullName;
        File.WriteAllText(Path.Combine(data, "clientsettings.json"), "{}");
        var client = new GameProfile { Id = "cl-" + Guid.NewGuid().ToString("N")[..6], Name = "Игра", Kind = ProfileKind.Client, DataDir = data };
        var broken = Path.Combine(_root, "broken.zip");
        File.WriteAllText(broken, "это не zip");
        var disabler = new ModDisabler(_ => false, (_, _) => { }, _ => { });

        var result = await disabler.For(client, broken, "Broken")!();

        Assert.True(result.Failed);
        Assert.Equal(Loc.T("crash.disableFailed", Loc.T("scan.corrupt")), result.Message);
    }
}
