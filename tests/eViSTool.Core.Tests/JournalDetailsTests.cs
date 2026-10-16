using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Подробности журнала: что поменялось в JSON настроек и в списках игроков; секреты не попадают в журнал.</summary>
public sealed class JournalDetailsTests
{
    private static string[] Show(IEnumerable<JournalDetail> details) =>
        [.. details.Select(d => d.Kind + ": " + string.Join(" | ", d.Args))];

    [Fact]
    public void Json_ChangedAddedRemoved_WithPaths()
    {
        var before = """{ "ServerName": "Дуо", "MaxClients": 16, "Roles": [ { "Code": "admin", "Privileges": ["a"] } ], "Old": true }""";
        var after = """{ "ServerName": "Дуо 2", "MaxClients": 16, "Roles": [ { "Code": "admin", "Privileges": ["a", "b"] } ], "New": null }""";

        Assert.Equal(
        [
            "field: ServerName | Дуо | Дуо 2",
            "fieldRemoved: Old | true",
            "fieldAdded: Roles[0].Privileges[1] | b",
            "fieldAdded: New | null",
        ], Show(JournalDetails.Json(before, after)));
    }

    [Fact]
    public void Json_SecretsAreNeverWritten()
    {
        var before = """{ "Password": "old-pass", "Channels": [ { "Token": "123:abc", "WebhookUrl": "https://x/1" } ] }""";
        var after = """{ "Password": "new-pass", "Channels": [ { "Token": "456:def", "WebhookUrl": "https://x/2" } ] }""";

        var shown = string.Join("\n", Show(JournalDetails.Json(before, after)));

        Assert.Equal("secret: Password\nsecret: Channels[0].Token\nsecret: Channels[0].WebhookUrl", shown);
        Assert.DoesNotContain("pass", shown.Replace("Password", ""));
        Assert.DoesNotContain("abc", shown);
    }

    [Fact]
    public void Json_NothingChanged_OrNotJson_IsEmpty()
    {
        Assert.Empty(JournalDetails.Json("""{ "a": 1 }""", """{ "a": 1 }"""));
        Assert.Empty(JournalDetails.Json("не json", """{ "a": 1 }"""));
        Assert.Empty(JournalDetails.Json(null, """{ "a": 1 }"""));
    }

    [Fact]
    public void Json_LongValuesAreShortened_AndListIsCapped()
    {
        var longText = new string('x', 200);
        var detail = Assert.Single(JournalDetails.Json("""{ "Motd": "" }""", $$"""{ "Motd": "{{longText}}" }"""));
        Assert.True(detail.Args[2].Length <= 60);
        Assert.EndsWith("…", detail.Args[2]);

        var many = "{" + string.Join(",", Enumerable.Range(0, 300).Select(i => $"\"k{i}\": {i}")) + "}";
        Assert.Equal(JournalDetails.Max, JournalDetails.Json("{}", many).Count);
    }

    private static ServerPlayersView View(WhitelistMode mode = WhitelistMode.Default) => new(
        [
            new ServerPlayer("u1", "Anna", "suplayer", null, null, Whitelisted: false, Ban: null),
            new ServerPlayer("u2", "Bob", null, null, null, Whitelisted: true, Ban: new PlayerListEntry("u2", "Bob", "admin", "гриф", null)),
        ],
        Whitelist: [new PlayerListEntry("u2", "Bob", null, null, null)],
        Bans: [new PlayerListEntry("u2", "Bob", "admin", "гриф", null)],
        Roles: [], DefaultRole: "suplayer", WhitelistMode: mode);

    [Fact]
    public void Players_EachChangeWithTheName()
    {
        Assert.Equal(["playerWhitelist: Anna | on"], Show(JournalDetails.Players(View(), new PlayerFileEdit("u1", Whitelisted: true))));
        Assert.Equal(["playerWhitelist: Bob | off"], Show(JournalDetails.Players(View(), new PlayerFileEdit("u2", Whitelisted: false))));
        Assert.Equal(["playerRole: Anna | suplayer | admin"], Show(JournalDetails.Players(View(), new PlayerFileEdit("u1", Role: "admin"))));
        // роли нет в записи игрока — значит, роль по умолчанию
        Assert.Equal(["playerRole: Bob | suplayer | admin"], Show(JournalDetails.Players(View(), new PlayerFileEdit("u2", Role: "admin"))));
        Assert.Equal(["playerUnban: Bob"], Show(JournalDetails.Players(View(), new PlayerFileEdit("u2", Unban: true))));
        Assert.Equal(["whitelistMode: Default | On"], Show(JournalDetails.Players(View(), new PlayerFileEdit("", Mode: WhitelistMode.On))));
    }

    [Fact]
    public void Players_NoRealChange_NoDetails_UnknownUidStaysUid()
    {
        Assert.Empty(JournalDetails.Players(View(), new PlayerFileEdit("u2", Whitelisted: true)));
        Assert.Empty(JournalDetails.Players(View(), new PlayerFileEdit("u1", Unban: true)));
        Assert.Empty(JournalDetails.Players(View(WhitelistMode.On), new PlayerFileEdit("", Mode: WhitelistMode.On)));
        Assert.Equal(["playerWhitelist: u9 | on"], Show(JournalDetails.Players(View(), new PlayerFileEdit("u9", Whitelisted: true))));
    }

    [Fact]
    public void Entry_WithoutDetails_IsWrittenAsBefore_WithDetails_RoundTrips()
    {
        var file = Path.Combine(Path.GetTempPath(), "evistool-jd-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var at = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            ServerJournal.Append(file, new JournalEntry(at, "stop", [], "PC", null, false));
            ServerJournal.Append(file, new JournalEntry(at.AddMinutes(1), "players", [], "PC", "10.0.0.2", true,
                [new JournalDetail("playerWhitelist", ["Anna", "on"])]));

            Assert.DoesNotContain("Details", File.ReadAllLines(file)[0]);
            var read = ServerJournal.Read(file, at.AddMinutes(2));
            Assert.Equal(["playerWhitelist: Anna | on"], Show(read[0].Details!));
            Assert.Null(read[1].Details);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
