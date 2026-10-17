using eViSTool.Core.Hosting;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>Версия игры сервера у хостера — из каждого источника по порядку (через «Проверить подключение»).</summary>
public sealed class HostedGameVersionTests : IDisposable
{
    private readonly HostedFixture _host = new();

    public void Dispose() => _host.Dispose();

    private Task<HostedCheckResult> CheckAsync() => HostedProbe.CheckAsync(_host.Endpoint, _host.Settings, default);

    [Fact]
    public async Task Manual_WinsOverEverything()
    {
        _host.GPortalLayout();
        _host.Settings.GameVersion = "1.21.6";

        var result = await CheckAsync();

        Assert.Equal("1.21.6", result.GameVersion?.ToString());
        Assert.Equal(GameVersionSource.Manual, result.VersionSource);
    }

    [Fact]
    public async Task VersionFile_LikeGPortal()
    {
        _host.GPortalLayout();
        _host.Server.Put("/version.txt", "linux-x64_1.22.8\n");

        var result = await CheckAsync();

        Assert.Equal("1.22.8", result.GameVersion?.ToString());
        Assert.Equal(GameVersionSource.VersionFile, result.VersionSource);
    }

    [Fact]
    public async Task LastLaunchMods_WhenThereIsNoVersionFile()
    {
        _host.GPortalLayout(versionFile: false);

        var result = await CheckAsync();

        Assert.Equal("1.22.7", result.GameVersion?.ToString());
        Assert.Equal(GameVersionSource.LastLaunch, result.VersionSource);
    }

    [Fact]
    public async Task ServerLog_WhenTheConfigHasNotBeenLaunchedYet()
    {
        _host.GPortalLayout(versionFile: false, config: false);
        _host.Server.Put("/serverconfig.json", File.ReadAllBytes(HostedFixture.Sample("gportal-serverconfig-template.json")));

        var result = await CheckAsync();

        Assert.Equal("1.22.7", result.GameVersion?.ToString());
        Assert.Equal(GameVersionSource.ServerLog, result.VersionSource);
    }

    [Fact]
    public async Task ServerDll_FromTheFileVersion()
    {
        // настоящая сборка с FileVersion 1.22.7 — поддельный сервер из tests/FakeVsServer
        _host.Server.Put("/VintagestoryServer.dll", File.ReadAllBytes(Path.Combine(FakeServer.Bin, "FakeVsServer.dll")));
        _host.Server.MakeDir("/Mods");

        var result = await CheckAsync();

        Assert.Equal("1.22.7", result.GameVersion?.ToString());
        Assert.Equal(GameVersionSource.ServerDll, result.VersionSource);
    }

    [Fact]
    public async Task Nothing_GivesNull_AndTheModCountSkipsBaseFiles()
    {
        _host.Server.Put("/Mods/VSSurvivalMod.dll", [1]);
        _host.Server.Put("/Mods/VSSurvivalMod.pdb", [1]);
        _host.Server.Put("/Mods/do_not_add_mods_here.txt", "x");
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");

        var result = await CheckAsync();

        Assert.Null(result.GameVersion);
        Assert.Null(result.VersionSource);
        Assert.Equal(1, result.ModCount);
        Assert.True(result.Features.RangeReads);
    }

    [Fact]
    public void Parsers()
    {
        Assert.Equal("1.22.7", HostedGameVersion.FromVersionFile("linux-x64_1.22.7")?.ToString());
        Assert.Equal("1.22.7", HostedGameVersion.FromVersionFile("1.22.7\r\n")?.ToString());
        Assert.Null(HostedGameVersion.FromVersionFile("garbage_"));
        Assert.Equal("1.22.7", HostedGameVersion.FromLog(HostedFixture.GPortalLog)?.ToString());
        Assert.Equal("1.22.7", HostedGameVersion.FromLastLaunch(JObject.Parse("""{ "LastLaunchMods": "[{\"id\":\"game\",\"version\":\"1.22.7\"}]" }"""))?.ToString());
        Assert.Equal("1.21.0", HostedGameVersion.FromLastLaunch(JObject.Parse("""{ "LastLaunchMods": [{ "id": "game", "version": "1.21.0" }] }"""))?.ToString());
        Assert.Null(HostedGameVersion.FromLastLaunch(JObject.Parse("""{ "LastLaunchMods": "not json" }""")));
        Assert.Null(HostedGameVersion.FromLastLaunch(JObject.Parse("""{ "LastLaunchMods": null }""")));
    }
}
