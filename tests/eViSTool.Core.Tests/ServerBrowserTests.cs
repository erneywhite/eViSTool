using eViSTool.Core.Game;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

/// <summary>Общий список серверов и запись в избранное игры.</summary>
public sealed class ServerBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-browse-" + Guid.NewGuid().ToString("N"));

    public ServerBrowserTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Parse_TheMasterServerFormat_DescriptionAsText()
    {
        var list = ServerBrowser.Parse("""
            { "status": "ok", "data": [
              { "serverName": "Whispers of Oldwood", "serverIP": "1.2.3.4:42420", "playstyle": { "id": "x", "langCode": "preset-surviveandbuild" },
                "mods": [ { "id": "buffberries", "version": "1.1.0" } ], "maxPlayers": "123", "players": 25, "gameVersion": "1.22.7",
                "hasPassword": false, "whitelisted": true, "gameDescription": "Discord: <a href=\"https://x\">Here</a><br>Rules &amp; lore<br>  <br><br><br>Season 3" },
              { "serverName": "no address" }
            ] }
            """);

        var s = Assert.Single(list);
        Assert.Equal("1.2.3.4:42420", s.Address);
        Assert.Equal("preset-surviveandbuild", s.Playstyle);
        Assert.Equal(new PublicServerMod("buffberries", "1.1.0"), Assert.Single(s.Mods));
        Assert.Equal(123, s.MaxPlayers); // мастер-сервер отдаёт его строкой
        Assert.True(s.HasSlots);
        Assert.True(s.Whitelisted);
        Assert.Equal("Discord: Here\nRules & lore\n\nSeason 3", s.Description); // пустые строки подряд — одна
    }

    [Fact]
    public void MasterUrl_FromTheGameSettings_OrOfficial()
    {
        Assert.Equal(ServerBrowser.OfficialMasterUrl, ServerBrowser.MasterUrl(_root));
        File.WriteAllText(Path.Combine(_root, "clientsettings.json"), """{ "stringSettings": { "masterserverUrl": "https://example.org/api/v1/servers/" } }""");
        Assert.Equal("https://example.org/api/v1/servers/", ServerBrowser.MasterUrl(_root));
    }

    [Fact]
    public void Favorites_AddReplaceRemove_KeepOtherSettings()
    {
        var file = Path.Combine(_root, "clientsettings.json");
        File.WriteAllText(file, """{ "stringSettings": { "language": "ru" }, "stringListSettings": { "multiplayerservers": [ "home,192.168.31.31," ] } }""");

        GameFavorites.Add(_root, "Oldwood, the best", "1.2.3.4:42420", null);
        GameFavorites.Add(_root, "Oldwood", "1.2.3.4:42420", "pw"); // тот же адрес — заменить
        Assert.Equal(["home", "Oldwood"], PlayTargets.Favorites(_root).Select(f => f.Name));
        Assert.Equal("pw", PlayTargets.Favorites(_root)[1].Password);

        GameFavorites.RemoveAddress(_root, "192.168.31.31");
        Assert.Equal(["Oldwood"], PlayTargets.Favorites(_root).Select(f => f.Name));
        Assert.Equal("ru", JObject.Parse(File.ReadAllText(file))["stringSettings"]!.Value<string>("language"));

        GameFavorites.Add(_root, "a,b", "5.6.7.8", null);
        Assert.Equal("a b", PlayTargets.Favorites(_root).Last().Name); // запятая в имени сломала бы строку
    }
}
