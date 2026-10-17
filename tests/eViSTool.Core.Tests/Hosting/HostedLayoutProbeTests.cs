using eViSTool.Core.Hosting;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>Где на сервере у хостера папки модов и конфиг: G-Portal (лог, шаблон, после запуска), Pterodactyl, ZAP.</summary>
public sealed class HostedLayoutProbeTests : IDisposable
{
    private readonly HostedFixture _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<HostedLayout> DetectAsync(HostedConnection? settings = null)
    {
        await _host.Server.ConnectAsync(default);
        return await HostedLayoutProbe.DetectAsync(_host.Server, settings ?? _host.Settings, default);
    }

    private static string Config(object modPaths, string disabled = "null") =>
        $$"""{ "ModPaths": {{JsonConvert.SerializeObject(modPaths)}}, "WorldConfig": { "DisabledMods": {{disabled}} } }""";

    [Fact]
    public async Task GPortal_PathsFromTheServerLog_RootIsGameAndData()
    {
        _host.GPortalLayout();

        var layout = await DetectAsync();

        Assert.Equal(["/Mods", "/gportal_mods"], layout.ModDirs);
        Assert.Equal("/Mods", layout.InstallDir); // в папку хостера не ставим
        Assert.Equal("/serverconfig.json", layout.ConfigPath);
        Assert.Equal("/", layout.DataDir);
        Assert.Equal("/", layout.GameDir);
        Assert.Empty(layout.Warnings); // ни «встроенной папки», ни путей Windows
    }

    [Fact]
    public async Task GPortal_WithoutLog_ConfigAfterFirstStart_DoubleSlashesAndOneFolderOnce()
    {
        _host.GPortalLayout(log: false);

        var layout = await DetectAsync();

        // «Mods» и «//home//sid_…//vintageStory//Mods» — одна папка, показывается один раз
        Assert.Equal(["/Mods", "/gportal_mods"], layout.ModDirs);
        Assert.Equal("/Mods", layout.InstallDir);
        Assert.Empty(layout.Warnings);
    }

    [Fact]
    public async Task GPortal_Template_WindowsPathIsSkippedSilently()
    {
        _host.GPortalLayout(log: false, config: false);
        _host.Server.Put("/serverconfig.json", File.ReadAllBytes(HostedFixture.Sample("gportal-serverconfig-template.json")));

        var layout = await DetectAsync();

        Assert.Equal(["/Mods"], layout.ModDirs);
        Assert.Equal("/Mods", layout.InstallDir);
        Assert.Empty(layout.Warnings);
    }

    [Fact]
    public async Task GPortal_WithoutConfig_ModsAreVisible_ButTheConfigIsReported()
    {
        _host.GPortalLayout(config: false, log: false);

        var layout = await DetectAsync();

        Assert.Equal(["/Mods"], layout.ModDirs);
        Assert.Null(layout.ConfigPath);
        Assert.Equal("/Mods", layout.InstallDir);
        var warning = Assert.Single(layout.Warnings);
        HostedTestText.AssertIs("hosted.noConfig", warning);
    }

    [Fact]
    public async Task Pterodactyl_DataFolder_AbsolutePathByTail_InstallsIntoData()
    {
        _host.Server.Put("/VintagestoryServer.dll", [1]);
        _host.Server.Put("/Mods/VSSurvivalMod.dll", [1]);
        _host.Server.MakeDir("/data/Mods");
        _host.Server.Put("/data/serverconfig.json", Config(new[] { "Mods", "/home/container/data/Mods" }));

        var layout = await DetectAsync();

        Assert.Equal(["/Mods", "/data/Mods"], layout.ModDirs);
        Assert.Equal("/data/Mods", layout.InstallDir);
        Assert.Equal("/data", layout.DataDir);
        Assert.Equal("/", layout.GameDir);
        Assert.Empty(layout.Warnings);
    }

    [Fact]
    public async Task Zap_ServerInASubfolder_EmptyModPathsMeansGameAndDataMods()
    {
        _host.Server.Put("/g123456/VintagestoryServer.dll", [1]);
        _host.Server.Put("/g123456/Mods/VSSurvivalMod.dll", [1]);
        _host.Server.MakeDir("/g123456/VintagestoryData/Mods");
        _host.Server.Put("/g123456/VintagestoryData/serverconfig.json", Config(Array.Empty<string>()));
        _host.Settings.Root = "/g123456";

        var layout = await DetectAsync();

        Assert.Equal("/g123456", layout.Root);
        Assert.Equal("/g123456/VintagestoryData/serverconfig.json", layout.ConfigPath);
        Assert.Equal(["/g123456/Mods", "/g123456/VintagestoryData/Mods"], layout.ModDirs);
        Assert.Equal("/g123456/VintagestoryData/Mods", layout.InstallDir);
    }

    [Fact]
    public async Task BuiltInModsFolderApartFromData_InstallThereIsWarned()
    {
        _host.Server.Put("/VintagestoryServer.dll", [1]);
        _host.Server.Put("/Mods/VSSurvivalMod.dll", [1]);
        _host.Server.Put("/data/serverconfig.json", Config(new[] { "Mods" }));

        var layout = await DetectAsync();

        Assert.Equal(["/Mods"], layout.ModDirs);
        Assert.Equal("/Mods", layout.InstallDir);
        var warning = Assert.Single(layout.Warnings);
        Assert.Contains(warning, HostedTestText.Of("hosted.builtinInstall").Select(t => t.Replace("{0}", "/Mods")));
    }

    [Fact]
    public async Task HiddenModPath_IsReported_AndTheVisibleModsFolderIsUsed()
    {
        _host.Server.MakeDir("/Mods");
        _host.Server.Put("/serverconfig.json", Config(new[] { "/srv/elsewhere/mymods" }));

        var layout = await DetectAsync();

        Assert.Equal(["/Mods"], layout.ModDirs);
        Assert.Contains(layout.Warnings, w => w.Contains("/srv/elsewhere/mymods"));
    }

    [Fact]
    public async Task AbsolutePath_MatchesIgnoringCase_WhenTheExactCaseIsNotThere()
    {
        _host.Server.MakeDir("/data/Mods");
        _host.Server.Put("/data/serverconfig.json", Config(new[] { "/home/container/data/mods" }));

        var layout = await DetectAsync();

        Assert.Equal(["/data/Mods"], layout.ModDirs);
    }

    [Fact]
    public async Task PinnedPaths_AreUsed_AndAGonePinIsReportedAndSearchedAgain()
    {
        _host.Server.Put("/data/serverconfig.json", Config(new[] { "/x/data/Mods" }));
        _host.Server.MakeDir("/data/Mods");
        _host.Server.MakeDir("/custom");
        var pinned = _host.Settings.Clone();
        pinned.ConfigPath = "data/serverconfig.json";
        pinned.ModDirs = ["custom"];
        pinned.InstallDir = "custom";

        var layout = await DetectAsync(pinned);
        Assert.Equal(["/custom"], layout.ModDirs);
        Assert.Equal("/custom", layout.InstallDir);
        Assert.Empty(layout.Warnings);

        pinned.ModDirs = ["gone"];
        pinned.InstallDir = "gone";
        layout = await DetectAsync(pinned);
        Assert.Equal(["/data/Mods"], layout.ModDirs);
        HostedTestText.AssertIs("hosted.layoutChanged", layout.Warnings[0]);
    }

    [Fact]
    public async Task Pin_ThenDetect_GivesTheSameLayout()
    {
        _host.GPortalLayout();
        var first = await DetectAsync();

        var pinned = HostedProbe.Pin(_host.Settings, first);
        Assert.Equal("serverconfig.json", pinned.ConfigPath);
        Assert.Equal(["Mods", "gportal_mods"], pinned.ModDirs);

        var again = await DetectAsync(pinned);
        Assert.Equal(first.ModDirs, again.ModDirs);
        Assert.Equal(first.InstallDir, again.InstallDir);
    }

    [Fact]
    public void LogModPaths_TakesTheIndentedLinesRightAfterTheHeader()
    {
        Assert.Equal(["~/vintageStory/Mods", "~/vintageStory/gportal_mods"], HostedLayoutProbe.LogModPaths(HostedFixture.GPortalLog));
        Assert.Equal(["/srv/vs/Mods"], HostedLayoutProbe.LogModPaths(
            "1.1.2026 10:00:00 [Notification] Will search the following paths for mods:\r\n1.1.2026 10:00:00 [Notification]     /srv/vs/Mods\r\n1.1.2026 10:00:00 [Notification]   [x] other\r\n"));
        Assert.Empty(HostedLayoutProbe.LogModPaths("1.1.2026 [Notification] Server logger started."));
        Assert.Empty(HostedLayoutProbe.LogModPaths(null));
    }

    [Fact]
    public async Task MatchAsync_StripsHomeMarkersDriveLettersAndRejectsDotDot()
    {
        _host.Server.MakeDir("/Mods");
        await _host.Server.ConnectAsync(default);
        var ls = new HostedListings(_host.Server);

        Assert.Equal("/Mods", await HostedLayoutProbe.MatchAsync(ls, "~/vintageStory/Mods", "/", null, null, default));
        Assert.Equal("/Mods", await HostedLayoutProbe.MatchAsync(ls, "//home//sid_1//vintageStory//Mods", "/", null, null, default));
        Assert.Equal("/Mods", await HostedLayoutProbe.MatchAsync(ls, @"D:\games\vs\Mods", "/", null, null, default));
        Assert.Null(await HostedLayoutProbe.MatchAsync(ls, "/home/../etc/Mods", "/", null, null, default));
        Assert.Null(await HostedLayoutProbe.MatchAsync(ls, "/home/x/NoSuch", "/", null, null, default));
    }

    [Fact]
    public async Task RootInASubfolder_WorksWhenTheFoldersAboveItCannotBeListed()
    {
        _host.Server.Put("/home/vs/serverconfig.json", Config(new[] { "Mods" }));
        _host.Server.MakeDir("/home/vs/Mods");
        _host.PutMod("/home/vs/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        // хостер не даёт листать ничего выше корня сервера
        _host.Server.FailOn = (op, path) => op == "list" && path is "/" or "/home";

        var layout = await DetectAsync(new HostedConnection { Host = _host.Host, Port = 21, User = "user", Root = "/home/vs" });

        Assert.Equal("/home/vs/serverconfig.json", layout.ConfigPath);
        Assert.Equal(["/home/vs/Mods"], layout.ModDirs);
        var ls = new HostedListings(_host.Server, "/home/vs");
        Assert.Equal("/home/vs/Mods", await ls.DirectoryIgnoreCaseAsync("/home/vs/mods", default));
        Assert.True(await ls.IsDirectoryAsync("/home", default));
        Assert.Null(await new HostedListings(_host.Server).FileAsync("/home/vs/serverconfig.json", default)); // без корня — не видно
    }
}
