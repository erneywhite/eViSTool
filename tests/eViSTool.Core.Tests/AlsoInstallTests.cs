using System.IO.Compression;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server.Remote;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests;

/// <summary>«Установить также в»: что предложить для каждого профиля.</summary>
public sealed class AlsoInstallTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evistool-also-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ModDbRelease R(string version, params string[] games) =>
        new() { ModVersion = version, MainFile = "https://x/" + version, Tags = [.. games] };

    private static readonly ModDbRelease[] Releases = [R("1.0.0", "1.21.5"), R("1.1.0", "1.22.3")];

    private GameProfile Client(string name, string? installedVersion = null)
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var mods = Directory.CreateDirectory(Path.Combine(data, "Mods")).FullName;
        File.WriteAllText(Path.Combine(data, "clientsettings.json"),
            JsonConvert.SerializeObject(new { stringListSettings = new { disabledMods = Array.Empty<string>(), modPaths = new[] { mods } } }));
        if (installedVersion is not null)
        {
            using var zip = ZipFile.Open(Path.Combine(mods, $"carryon_{installedVersion}.zip"), ZipArchiveMode.Create);
            using var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
            w.Write($$"""{ "modid": "carryon", "name": "Carry On", "version": "{{installedVersion}}" }""");
        }
        return new GameProfile { Id = name, Name = name, Kind = ProfileKind.Client, DataDir = data };
    }

    [Fact]
    public void Offered_OnlyForModsTheClientNeeds_AndOnlyWhereNeeded()
    {
        Assert.True(AlsoInstall.WorthOffering(ModSide.Both));
        Assert.True(AlsoInstall.WorthOffering(ModSide.Client));
        Assert.False(AlsoInstall.WorthOffering(ModSide.Server)); // серверный мод игроку для входа не нужен

        Assert.False(AlsoInstall.Needed(ModSide.Client, ProfileKind.Server)); // клиентский — выделенному серверу ни к чему
        Assert.True(AlsoInstall.Needed(ModSide.Client, ProfileKind.Client));
        Assert.True(AlsoInstall.Needed(ModSide.Both, ProfileKind.Server));
    }

    [Fact]
    public async Task UnknownGameVersion_TheChosenRelease_MarkedUnchecked()
    {
        var option = await AlsoInstall.EvaluateAsync(ModTarget.For(Client("c"))!, "carryon", Releases, Releases[1]);

        Assert.Equal(AlsoInstallState.UnknownGame, option.State);
        Assert.Equal("1.1.0", option.Release!.ModVersion);
        Assert.Null(option.Installed);
        Assert.True(option.CanInstall);
    }

    [Fact]
    public async Task SameVersionAlreadyInstalled_IsNotOffered_ButAnOlderOneIsAnUpdate()
    {
        var same = await AlsoInstall.EvaluateAsync(ModTarget.For(Client("same", "1.1.0"))!, "carryon", Releases, Releases[1]);
        Assert.Equal(AlsoInstallState.AlreadyThere, same.State);
        Assert.False(same.CanInstall);

        var older = await AlsoInstall.EvaluateAsync(ModTarget.For(Client("older", "1.0.0"))!, "carryon", Releases, Releases[1]);
        Assert.True(older.CanInstall);
        Assert.Equal("1.0.0", older.Installed);
        Assert.Contains("1.0.0", older.Describe());
        Assert.Contains("1.1.0", older.Describe());
    }

    [Fact]
    public async Task UnreachableRemoteServer_IsShownButCannotBeChosen()
    {
        var remote = new GameProfile { Id = "remote", Name = "remote", Kind = ProfileKind.Server, RemoteCode = "x" };
        var target = new ModTarget(remote, new ConnectionCode("127.0.0.1", 1, "key", new string('A', 64)));

        var option = await AlsoInstall.EvaluateAsync(target, "carryon", Releases, Releases[1]);

        Assert.Equal(AlsoInstallState.Unavailable, option.State);
        Assert.False(option.CanInstall);
    }
}
