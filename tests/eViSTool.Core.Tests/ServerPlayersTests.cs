using eViSTool.Core.Server;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

/// <summary>Игроки сервера по его файлам — формат как у настоящего сервера 1.22.7.</summary>
public sealed class ServerPlayersTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "evistool-players-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 8, 18, 0, 0, DateTimeKind.Local);

    public ServerPlayersTests()
    {
        Directory.CreateDirectory(Path.Combine(_data, "Playerdata"));
        File.WriteAllText(Path.Combine(_data, "serverconfig.json"), """
            { "WhitelistMode": 2, "DefaultRoleCode": "suplayer", "OnlyWhitelisted": false,
              "Roles": [ { "Code": "suplayer", "Name": "Survival Player" }, { "Code": "admin", "Name": "Admin" } ] }
            """);
        File.WriteAllText(Path.Combine(_data, "Playerdata", "playerdata.json"), """
            [
              { "PlayerUID": "UID-ANNA", "RoleCode": "suplayer", "LastKnownPlayername": "Anna", "PermaPrivileges": [], "DeniedPrivileges": [],
                "FirstJoinDate": "2026-09-01T10:00:00+03:00", "LastJoinDate": "2026-10-07T21:00:00+03:00", "ExtraLandClaimAllowance": 0 },
              { "PlayerUID": "UID-BORIS", "RoleCode": "admin", "LastKnownPlayername": "Boris", "PermaPrivileges": [], "DeniedPrivileges": [],
                "FirstJoinDate": "2026-09-02T10:00:00+03:00", "LastJoinDate": "2026-09-20T10:00:00+03:00", "ExtraLandClaimAllowance": 5 }
            ]
            """);
        // формат — как записал сервер на /whitelist add и /ban
        File.WriteAllText(Path.Combine(_data, "Playerdata", "playerswhitelisted.json"), """
            [ { "PlayerUID": "UID-ANNA", "PlayerName": "Anna", "UntilDate": "2076-10-08T17:54:00.7277368+03:00", "Reason": "friend", "IssuedByPlayerName": "Console" },
              { "PlayerUID": "UID-NEW", "PlayerName": "Newbie", "UntilDate": "2076-10-08T17:54:00+03:00", "Reason": "", "IssuedByPlayerName": "Console" } ]
            """);
        File.WriteAllText(Path.Combine(_data, "Playerdata", "playersbanned.json"), """
            [ { "PlayerUID": "UID-BORIS", "PlayerName": "Boris", "UntilDate": "2026-10-09T17:54:01.5671264+03:00", "Reason": "griefing", "IssuedByPlayerName": "Console Admin" },
              { "PlayerUID": "UID-OLD", "PlayerName": "Old", "UntilDate": "2026-01-01T00:00:00+03:00", "Reason": "expired", "IssuedByPlayerName": "Console" } ]
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Read_PlayersListsRolesAndMode()
    {
        var v = new ServerPlayers(_data).Read(Now);

        Assert.Equal(["Anna", "Boris"], v.Players.Select(p => p.Name)); // свежий вход — выше
        var anna = v.Players[0];
        Assert.True(anna.Whitelisted);
        Assert.Null(anna.Ban);
        var boris = v.Players[1];
        Assert.Equal("admin", boris.Role);
        Assert.Equal("griefing", boris.Ban!.Reason);
        Assert.False(boris.Ban.IsPermanent);
        Assert.True(v.Whitelist.Single(w => w.Uid == "UID-ANNA").IsPermanent);

        Assert.Equal(["UID-ANNA", "UID-NEW"], v.Whitelist.Select(w => w.Uid)); // в списке и тот, кто ещё не заходил
        Assert.Equal(["UID-BORIS"], v.Bans.Select(b => b.Uid));             // истёкший бан не считается
        Assert.Equal(WhitelistMode.On, v.WhitelistMode);
        Assert.Equal(["suplayer", "admin"], v.Roles.Select(r => r.Code));
        Assert.Equal("suplayer", v.DefaultRole);
    }

    [Fact]
    public void Apply_EditsFiles_AndKeepsUnknownFields()
    {
        var players = new ServerPlayers(_data);
        players.Apply(new PlayerFileEdit("UID-ANNA", Role: "admin", Whitelisted: false));
        players.Apply(new PlayerFileEdit("UID-BORIS", Whitelisted: true, Unban: true, Mode: WhitelistMode.Off));

        var v = players.Read(Now);
        Assert.Equal("admin", v.Players.Single(p => p.Name == "Anna").Role);
        Assert.False(v.Players.Single(p => p.Name == "Anna").Whitelisted);
        Assert.True(v.Players.Single(p => p.Name == "Boris").Whitelisted);
        Assert.Empty(v.Bans);
        Assert.Equal(WhitelistMode.Off, v.WhitelistMode);

        // поля, которых мы не знаем, — на месте
        var raw = JArray.Parse(File.ReadAllText(Path.Combine(_data, "Playerdata", "playerdata.json")));
        Assert.Equal(5, raw.OfType<JObject>().Single(p => p.Value<string>("PlayerUID") == "UID-BORIS").Value<int>("ExtraLandClaimAllowance"));
        Assert.False(JObject.Parse(File.ReadAllText(Path.Combine(_data, "serverconfig.json"))).Value<bool>("OnlyWhitelisted"));
        Assert.Equal(2, JArray.Parse(File.ReadAllText(Path.Combine(_data, "Playerdata", "playerswhitelisted.json"))).Count); // Newbie + Boris
    }

    [Fact]
    public void Apply_UnknownPlayer_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => new ServerPlayers(_data).Apply(new PlayerFileEdit("UID-NOBODY", Role: "admin")));
        Assert.Throws<InvalidOperationException>(() => new ServerPlayers(_data).Apply(new PlayerFileEdit("UID-NOBODY", Whitelisted: true)));
    }

    [Fact]
    public void NoFilesYet_EmptyView()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_data, "empty")).FullName;
        var v = new ServerPlayers(empty).Read(Now);
        Assert.Empty(v.Players);
        Assert.Equal(WhitelistMode.Default, v.WhitelistMode);
    }

    [Fact]
    public void Commands_MatchTheServerSyntax()
    {
        Assert.Equal("/player Anna role admin", PlayerCommands.Role("Anna", "admin"));
        Assert.Equal("/whitelist add Anna friend", PlayerCommands.WhitelistAdd("Anna", "friend"));
        Assert.Equal("/ban Boris 1 day griefing", PlayerCommands.Ban("Boris", 1, "day", "griefing"));
        Assert.Equal("/player Anna allowcharselonce", PlayerCommands.AllowCharSelOnce("Anna"));
        Assert.Equal("/executeas Anna announcenear 2 hi", PlayerCommands.TellNear("Anna", "hi"));
        Assert.False(PlayerCommands.IsValidName("two words"));
    }
}
