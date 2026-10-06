using eViSTool.Core.Profiles;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests;

/// <summary>Связанные профили («ставить моды заодно»): связь двусторонняя, развязка — с обеих сторон.</summary>
public sealed class ProfileLinkTests
{
    [Fact]
    public void Link_IsSeenFromBothSides_AndUnlinkClearsBoth()
    {
        var server = new GameProfile { Name = "Server", Kind = ProfileKind.Server };
        var client = new GameProfile { Name = "Client", Kind = ProfileKind.Client };
        var other = new GameProfile { Name = "Other", Kind = ProfileKind.Client };

        server.SetLinked(client, true);
        Assert.True(server.IsLinkedTo(client));
        Assert.True(client.IsLinkedTo(server));
        Assert.False(server.IsLinkedTo(other));

        // развязали со стороны клиента — запись у сервера тоже ушла
        client.SetLinked(server, false);
        Assert.False(server.IsLinkedTo(client));
        Assert.Empty(server.LinkedProfiles);

        // повторная связь не плодит дубли
        server.SetLinked(client, true);
        server.SetLinked(client, true);
        Assert.Single(server.LinkedProfiles);
    }

    [Fact]
    public void Links_SurviveSaving()
    {
        var server = new GameProfile { Name = "Server", Kind = ProfileKind.Server };
        var client = new GameProfile { Name = "Client", Kind = ProfileKind.Client };
        server.SetLinked(client, true);

        var back = JsonConvert.DeserializeObject<GameProfile>(JsonConvert.SerializeObject(server))!;
        Assert.True(back.IsLinkedTo(client));
    }
}
