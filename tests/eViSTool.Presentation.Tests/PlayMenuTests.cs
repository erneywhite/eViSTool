using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;
using Newtonsoft.Json.Linq;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Сборка меню «Играть»: сервер по умолчанию по ключу (свой или из избранного), пятёрка избранного с сервером по
/// умолчанию впереди, «ещё N», «без сервера», свои серверы без агента, варианты поля «Сервер по умолчанию». Избранное —
/// настоящий clientsettings.json во временной папке; агентов нет, сеть не трогается.
/// </summary>
public sealed class PlayMenuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-playmenu-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;

    public PlayMenuTests() => _data = Directory.CreateDirectory(Path.Combine(_root, "VintagestoryData")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Избранное игры — строками «имя,адрес,пароль», как его хранит сама игра.</summary>
    private void WriteFavorites(params string[] lines) =>
        File.WriteAllText(Path.Combine(_data, "clientsettings.json"),
            new JObject { ["stringListSettings"] = new JObject { ["multiplayerservers"] = new JArray(lines) } }.ToString());

    private ProfileViewModel Client(string? defaultServer = null) => new(new GameProfile
    {
        Id = "playmenu-client",
        Name = "Игра",
        Kind = ProfileKind.Client,
        DataDir = _data,
        DefaultServer = defaultServer,
    }, _ => { });

    /// <summary>Свой сервер на этой машине; агента у него нет (id новый, файла агента нет).</summary>
    private ProfileViewModel LocalServer(string name) => new(new GameProfile
    {
        Id = "playmenu-" + Guid.NewGuid().ToString("N"),
        Name = name,
        Kind = ProfileKind.Server,
        DataDir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName,
    }, _ => { });

    // ---- сервер по умолчанию

    [Fact]
    public void DefaultTarget_FavoriteKey_IsFoundInTheGameFavorites()
    {
        WriteFavorites("Дом,192.168.1.20,", "Друзья,play.example.org,секрет");
        var client = Client("fav:Друзья|play.example.org");

        var target = PlayMenu.DefaultTarget(client, [client]);

        Assert.Equal("Друзья", target?.Name);
        Assert.Equal(PlayTargetKind.Favorite, target?.Kind);
    }

    [Fact]
    public void DefaultTarget_OwnKey_IsFoundAmongTheProfiles()
    {
        var server = LocalServer("Сервер");
        var client = Client(PlayTargets.OwnKey(server.Model.Id));

        var target = PlayMenu.DefaultTarget(client, [client, server]);

        Assert.Equal(PlayTargetKind.OwnServer, target?.Kind);
        Assert.Equal(server.Model.Id, target?.ProfileId);
    }

    // сервер удалили из избранного или профиль сервера удалён — значит, без сервера
    [Fact]
    public void DefaultTarget_Gone_MeansNoServer()
    {
        WriteFavorites("Дом,192.168.1.20,");
        var gone = Client("fav:Друзья|play.example.org");
        var goneOwn = Client(PlayTargets.OwnKey("нет-такого"));

        Assert.Null(PlayMenu.DefaultTarget(gone, [gone]));
        Assert.Null(PlayMenu.DefaultTarget(goneOwn, [goneOwn]));
        Assert.Null(PlayMenu.DefaultTarget(Client(), [])); // не выбран
        Assert.Null(PlayMenu.DefaultTarget(null, []));
    }

    // у серверного профиля «Играть» нет — и сервера по умолчанию тоже
    [Fact]
    public void DefaultTarget_ServerProfile_IsNull()
    {
        var server = LocalServer("Сервер");
        server.Model.DefaultServer = PlayTargets.OwnKey(server.Model.Id);

        Assert.Null(PlayMenu.DefaultTarget(server, [server]));
    }

    // ---- избранное в меню

    [Fact]
    public void Favorites_FiveShown_DefaultFirstEvenBeyondTheFifth_AndTheRestCounted()
    {
        WriteFavorites("A,10.0.0.1,", "B,10.0.0.2,", "C,10.0.0.3,", "D,10.0.0.4,", "E,10.0.0.5,", "F,10.0.0.6,", "G,10.0.0.7,");
        var items = new List<PlayMenuItemViewModel> { new(null, "старый пункт", "") };

        var more = PlayMenu.FillFavorites(items, Client().Model, "fav:G|10.0.0.7");

        Assert.Equal(new[] { "G", "A", "B", "C", "D" }, items.Select(i => i.Name));
        Assert.True(items[0].IsDefault);
        Assert.All(items.Skip(1), i => Assert.False(i.IsDefault));
        Assert.Equal(Loc.T("play.moreFavorites", 2), more);
    }

    [Fact]
    public void Favorites_AllFit_NoMoreText_AndPasswordIsMentioned()
    {
        WriteFavorites("Дом,192.168.1.20,", "Друзья,play.example.org,секрет");
        var items = new List<PlayMenuItemViewModel>();

        var more = PlayMenu.FillFavorites(items, Client().Model, null);

        Assert.Equal("", more);
        Assert.Equal("192.168.1.20", items[0].Detail);
        Assert.Equal("play.example.org · " + Loc.T("play.withPassword"), items[1].Detail);
        Assert.True(items[1].HasPassword);
    }

    [Fact]
    public void NoServer_IsDefault_OnlyWithoutDefaultKey()
    {
        Assert.True(PlayMenu.NoServer(null).IsDefault);
        Assert.False(PlayMenu.NoServer("fav:Дом|192.168.1.20").IsDefault);
        Assert.True(PlayMenu.NoServer(null).IsNone);
        Assert.Equal(Loc.T("play.noServer"), PlayMenu.NoServer(null).Name);
    }

    // ---- свои серверы

    // свой сервер без агента — «остановлен» сразу, без ожидания; удалённый с нечитаемым кодом в меню не попадает
    [Fact]
    public void OwnServers_LocalWithoutAgent_IsStopped_UnreadableRemoteIsSkipped()
    {
        var local = LocalServer("Дома");
        var remote = new ProfileViewModel(new GameProfile { Id = "playmenu-remote", Name = "Чужой", Kind = ProfileKind.Server, RemoteCode = "не-код" }, _ => { });
        var items = new List<PlayMenuItemViewModel>();

        PlayMenu.FillOwnServers(items, [Client(), local, remote], PlayTargets.OwnKey(local.Model.Id), null);

        var item = Assert.Single(items);
        Assert.Equal("Дома", item.Name);
        Assert.True(item.IsOwn);
        Assert.True(item.IsDefault);
        Assert.Equal(Loc.T("play.thisPc") + " · " + Loc.T("play.stopped"), item.Detail);
    }

    // ---- «Сервер по умолчанию» в настройках

    [Fact]
    public void DefaultServerOptions_NoServerFirst_ThenOwnServers_ThenFavorites()
    {
        WriteFavorites("Дом,192.168.1.20,");
        var server = LocalServer("Сервер");
        var client = Client();

        var options = PlayMenu.DefaultServerOptions(client.Model, [client, server]);

        Assert.Equal(new[] { "", PlayTargets.OwnKey(server.Model.Id), "fav:Дом|192.168.1.20" }, options.Select(o => o.Key));
        Assert.Equal(Loc.T("play.noServer"), options[0].Title);
        Assert.Equal("Дом  ·  192.168.1.20", options[2].Title);
    }
}
