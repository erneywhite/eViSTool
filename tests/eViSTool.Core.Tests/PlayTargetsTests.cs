using eViSTool.Core.Game;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

/// <summary>Куда подключаться по «Играть»: избранное игры, свои серверы, параметры запуска.</summary>
public sealed class PlayTargetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-play-" + Guid.NewGuid().ToString("N"));

    public PlayTargetsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Favorites_AreReadFromTheGameSettings()
    {
        File.WriteAllText(Path.Combine(_root, "clientsettings.json"), """
            { "stringListSettings": { "multiplayerservers": [ "home,192.168.31.31,", "test-pass,127.0.0.1:42421,se,cret", ",bad", "noaddress," ] } }
            """);

        var list = PlayTargets.Favorites(_root);

        Assert.Equal(["home", "test-pass", "bad"], list.Select(f => f.Name)); // без имени — по адресу; без адреса — пропуск
        Assert.Equal("192.168.31.31", list[0].Address);
        Assert.False(list[0].HasPassword);
        Assert.Equal("se,cret", list[1].Password); // запятые в пароле — его часть
        Assert.Equal("fav:home|192.168.31.31", list[0].Key);
    }

    [Fact]
    public void OwnLocalServer_LocalhostWithThePortFromItsConfig()
    {
        File.WriteAllText(Path.Combine(_root, "serverconfig.json"), """{ "Port": 42500 }""");
        var server = new GameProfile { Id = "s1", Name = "Survival", Kind = ProfileKind.Server, DataDir = _root };

        var target = PlayTargets.Own(server)!;

        Assert.Equal("127.0.0.1:42500", target.Address);
        Assert.Equal("own:s1", target.Key);
        Assert.Equal(PlayTargets.DefaultGamePort, PlayTargets.GamePortOf(Path.Combine(_root, "none")));
    }

    [Fact]
    public void StartInfo_ConnectsAndPassesThePassword()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        File.WriteAllText(Path.Combine(game, GameLauncher.ClientExeName), "");
        var client = new GameProfile { Kind = ProfileKind.Client, GameDir = game };

        var plain = GameLauncher.StartInfo(client);
        Assert.DoesNotContain("--connect", plain.ArgumentList);

        var psi = GameLauncher.StartInfo(client, PlayTargets.ParseFavorite("srv,example.org:42420,pw1"));
        Assert.Equal(["--connect", "example.org:42420", "--pw", "pw1"], psi.ArgumentList.TakeLast(4));
    }
}
