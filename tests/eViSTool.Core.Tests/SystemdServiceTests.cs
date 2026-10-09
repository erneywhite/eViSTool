using System.Diagnostics;
using eViSTool.Core.Platform;
using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>
/// Служба systemd для агента (команда «service»): текст юнита, от кого запускать агента и отказы. Сами install и
/// uninstall здесь не запускаются — они меняют систему; их проверяют руками на машине с systemd.
/// </summary>
public sealed class SystemdServiceTests : IDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string AgentExe = Environment.GetEnvironmentVariable("EVISTOOL_TEST_AGENT")
        ?? Path.Combine(Root, "src", "eViSTool.Agent", "bin", "Debug", "net10.0", AgentProtocol.ExeName);

    private readonly string _tmp = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "evistool-systemd-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static SystemdUnit Unit(string appDir = "/home/vintagestory/evistool", string data = "/var/vintagestory/data") => new()
    {
        Name = "evistool",
        ExecPath = appDir + "/eViSTool.Agent",
        Args = ["--start"],
        WorkingDirectory = appDir,
        User = "vintagestory",
        Group = "vintagestory",
        DataDir = data,
        WritablePaths = [appDir, data],
    };

    private static string[] Lines(string text) => text.Split('\n');

    [Fact]
    public void Unit_HasEverythingTheServiceNeeds()
    {
        var text = Unit().Render();
        var lines = Lines(text);

        Assert.True(SystemdUnit.IsOurs(text));
        Assert.DoesNotContain('\r', text); // systemd читает и CRLF, но юнит пишется на Linux — как у всех
        foreach (var line in new[]
                 {
                     "[Unit]", "After=network-online.target", "Wants=network-online.target",
                     "[Service]", "Type=simple", "User=vintagestory", "Group=vintagestory",
                     "WorkingDirectory=/home/vintagestory/evistool",
                     "ExecStart=/home/vintagestory/evistool/eViSTool.Agent --start",
                     "Environment=EVISTOOL_SERVICE=evistool",
                     // SIGTERM — только агенту, он остановит сервер с сохранением мира; ждём это дольше, чем ждёт агент
                     "KillMode=mixed", "TimeoutStopSec=300",
                     // выход 75 после самообновления — не сбой, но перезапуск; остальные сбои — тоже перезапуск
                     "Restart=on-failure", "RestartSec=5", "SuccessExitStatus=75", "RestartForceExitStatus=75",
                     "NoNewPrivileges=true", "PrivateTmp=true", "ProtectSystem=full",
                     "[Install]", "WantedBy=multi-user.target",
                 })
            Assert.Contains(line, lines);
        Assert.Equal(75, SystemdUnit.SelfUpdateExitCode);
        Assert.Contains(lines, l => l.StartsWith("Description=") && l.Contains("/var/vintagestory/data"));
        // SIGKILL всем, MemoryDenyWriteExecute (ломает JIT .NET), ProtectHome (всё лежит в /home) — нельзя
        Assert.DoesNotContain("KillMode=control-group", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("MemoryDenyWriteExecute") || l.StartsWith("ProtectHome"));
        Assert.Equal("/etc/systemd/system/evistool.service", SystemdUnit.PathFor("evistool"));
    }

    [Fact]
    public void Unit_PathsWithSpaces_AreQuoted_AndPercentAndDollar_ReachTheAgentAsIs()
    {
        var unit = Unit("/home/vs user/eViSTool 100%") with
        {
            Args = ["--start", "--data", "/var/vs data/$HOME", "--profile", "server"],
            Environment = new Dictionary<string, string> { ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = "/home/vs user/eViSTool 100%/data/.net" },
        };
        var lines = Lines(unit.Render());

        Assert.Contains("ExecStart=\"/home/vs user/eViSTool 100%%/eViSTool.Agent\" --start --data \"/var/vs data/$$HOME\" --profile server", lines);
        // WorkingDirectory кавычек не снимает — только спецификаторы
        Assert.Contains("WorkingDirectory=/home/vs user/eViSTool 100%%", lines);
        // в Environment= «$» не раскрывается, его удваивать нельзя
        Assert.Contains("Environment=\"DOTNET_BUNDLE_EXTRACT_BASE_DIR=/home/vs user/eViSTool 100%%/data/.net\"", lines);

        Assert.Equal("\"a\\\"b\\\\c\"", SystemdUnit.QuoteArg("a\"b\\c"));
        Assert.Equal("\"\"", SystemdUnit.QuoteArg(""));
        Assert.Equal("\"x;y\"", SystemdUnit.QuoteArg("x;y")); // «;» отдельным словом — разделитель команд у systemd
        Assert.Equal("\"line\\nbreak\"", SystemdUnit.QuoteArg("line\nbreak"));
        Assert.Equal("/simple/path-1.2_x:y=z", SystemdUnit.QuoteArg("/simple/path-1.2_x:y=z"));
    }

    [Fact]
    public void Unit_InUsr_StaysWritable()
    {
        // ProtectSystem=full сделал бы /usr только для чтения: агент не обновился бы, сервер не записал бы мир
        var lines = Lines(Unit("/usr/local/evistool").Render());
        Assert.DoesNotContain(lines, l => l.StartsWith("ProtectSystem"));
        Assert.Contains("NoNewPrivileges=true", lines);
        Assert.Contains(Lines(Unit("/usrdata/evistool").Render()), l => l == "ProtectSystem=full");
    }

    [Fact]
    public void ServiceName_DefaultSuffixAndBadOnes()
    {
        Assert.Equal("evistool", SystemdUnit.NormalizeName(null));
        Assert.Equal("evistool", SystemdUnit.NormalizeName("  "));
        Assert.Equal("evistool-test", SystemdUnit.NormalizeName("evistool-test.service"));
        Assert.Equal("vs_2", SystemdUnit.NormalizeName("vs_2"));
        foreach (var bad in new[] { "a b", "../x", "x/y", "x;y", "evistool@1", "%n", ".service" })
            Assert.Null(SystemdUnit.NormalizeName(bad));
        Assert.False(SystemdUnit.IsOurs("[Unit]\nDescription=OpenBSD Secure Shell server\n"));
    }

    [Fact]
    public void Passwd_IsParsed()
    {
        var user = UnixAccounts.ParsePasswd("vintagestory:x:999:988::/home/vintagestory:/bin/false");
        Assert.Equal(new UnixUser("vintagestory", 999, 988, "/home/vintagestory", "988"), user);
        Assert.Null(UnixAccounts.ParsePasswd(""));
        Assert.Null(UnixAccounts.ParsePasswd("broken:x:abc:1::/:/bin/sh"));
        Assert.Null(UnixAccounts.ParsePasswd("short:x:1"));
    }

    [Fact]
    public void ServiceUser_IsNamedOrTheOwner_ButNeverRootByItself()
    {
        Assert.Equal("vs", UnixAccounts.PickServiceUser("vs", "root", "vintagestory"));
        Assert.Equal("root", UnixAccounts.PickServiceUser("root", "vintagestory")); // явно — можно
        Assert.Equal("vintagestory", UnixAccounts.PickServiceUser(null, "root", "vintagestory"));
        Assert.Equal("vintagestory", UnixAccounts.PickServiceUser(" ", null, "vintagestory"));
        Assert.Equal("erney", UnixAccounts.PickServiceUser(null, "erney", "vintagestory")); // данные сервера — первыми
        Assert.Null(UnixAccounts.PickServiceUser(null, "root", "root"));
        Assert.Null(UnixAccounts.PickServiceUser(null, null, null));
    }

    [Fact]
    public void ServiceUser_FromRealFolders_AndTheirPermissions()
    {
        if (!OperatingSystem.IsLinux()) return;
        var me = Environment.UserName;
        var data = Directory.CreateDirectory(Path.Combine(_tmp, "data")).FullName;
        var app = Directory.CreateDirectory(Path.Combine(_tmp, "evistool")).FullName;

        Assert.Equal(me, UnixAccounts.OwnerOf(data));
        Assert.Null(UnixAccounts.OwnerOf(Path.Combine(_tmp, "no-such")));
        Assert.Equal(me == "root" ? null : me, UnixAccounts.PickServiceUser(null, UnixAccounts.OwnerOf(data), UnixAccounts.OwnerOf(app)));
        Assert.Equal(me, UnixAccounts.Find(me)?.Name);
        Assert.Null(UnixAccounts.Find("no-such-user-" + Guid.NewGuid().ToString("N")[..8]));

        Assert.True(UnixAccounts.CanAccess(me, app, 'w'));
        Assert.False(UnixAccounts.CanAccess(me, Path.Combine(_tmp, "no-such"), 'r'));
        Assert.Null(UnixAccounts.ForeignEntry(app, me));

        if (me != "root")
        {
            // папка своя, а спрашиваем про root: первой «чужой» оказывается она сама; про другого — не узнать без root
            Assert.Equal((app, me), UnixAccounts.ForeignEntry(app, "root"));
            Assert.Null(UnixAccounts.CanAccess("root", app, 'w'));
            return;
        }

        // от root (так тесты идут на проверочной машине): папка, отданная nobody, — его; файл root внутри — чужой
        Assert.Equal(0, UnixAccounts.Run("chown", "nobody", data).Code);
        Assert.Equal("nobody", UnixAccounts.OwnerOf(data));
        Assert.Equal("nobody", UnixAccounts.PickServiceUser(null, UnixAccounts.OwnerOf(data), UnixAccounts.OwnerOf(app)));
        File.WriteAllText(Path.Combine(data, "made-by-root.json"), "{}");
        Assert.Equal((Path.Combine(data, "made-by-root.json"), "root"), UnixAccounts.ForeignEntry(data, "nobody"));
        // права проверяются от имени пользователя: в свою папку nobody писать может, в закрытую папку root — нет
        File.SetUnixFileMode(_tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                   | UnixFileMode.OtherExecute);
        Assert.True(UnixAccounts.CanAccess("nobody", data, 'w'));
        File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.False(UnixAccounts.CanAccess("nobody", app, 'w'));
    }

    // ---- сама команда агента: отказы, которые не меняют систему

    private static async Task<(int Code, string Output)> Agent(params string[] args) => await Agent(null, args);

    private static async Task<(int Code, string Output)> Agent(IDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo(AgentExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await output + await error);
    }

    [Fact]
    public async Task ServiceCommand_BadWords_AreRefused_OnAnySystem()
    {
        var (code, text) = await Agent("service", "frobnicate", "--lang", "en");
        Assert.True(code == 2, text);
        Assert.Contains("Unknown command: service frobnicate", text);
        Assert.Contains("service install", text); // справка по service

        (code, text) = await Agent("service", "install", "--name", "a b", "--lang", "en");
        Assert.True(code == 2, text);
        Assert.Contains("\"a b\"", text);

        (code, text) = await Agent("service", "status", "--user", "vs", "--lang", "en"); // --user — только для install
        Assert.True(code == 2, text);
        Assert.Contains("Unknown option: --user", text);

        (code, text) = await Agent("service", "--help", "--lang", "en");
        Assert.True(code == 0, text);
        Assert.Contains("service uninstall", text);
    }

    [Fact]
    public async Task ServiceCommand_OnWindows_SaysItIsForLinux()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var sub in new[] { "install", "uninstall", "status" })
        {
            var (code, text) = await Agent("service", sub, "--lang", "en");
            Assert.True(code == 1, text);
            Assert.Contains("for Linux only", text);
        }
    }

    [Fact]
    public async Task ServiceCommand_OnLinux_StatusOfAMissingService_AndNoInstallWithoutRoot()
    {
        if (!OperatingSystem.IsLinux()) return;
        var systemd = Directory.Exists("/run/systemd/system");
        var name = "evistool-test-" + Guid.NewGuid().ToString("N")[..8];

        var (code, text) = await Agent("service", "status", "--name", name, "--lang", "en");
        Assert.True(code == (systemd ? 0 : 1), text);
        Assert.Contains(systemd ? $"Service {name} is not installed" : "systemd is not running", text);

        // ставить службу из тестов нельзя: от root проверка поставила бы её по-настоящему
        if (Environment.IsPrivilegedProcess || !systemd) return;
        (code, text) = await Agent("service", "install", "--name", name, "--lang", "en");
        Assert.True(code == 1, text);
        Assert.Contains("This needs root: sudo ./eViSTool.Agent service install --name", text);
        Assert.False(File.Exists(SystemdUnit.PathFor(name)));
    }
}
