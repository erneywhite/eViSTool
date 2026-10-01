using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

public class ServerCommandsTests
{
    private static readonly ServerCommand[] All =
    [
        new("tp", "<source> <target>", "Teleport a player or entity to a location"),
        new("time", "", "Get or set world time or time speed"),
        new("timeswitch", "", "Timeswitch and dimensions switching commands"),
        new("tpwp", "<name>", "Teleport yourself to a waypoint"),
        new("worldconfig", "[key] [value]", "Modify the world config"),
        new("worldconfigcreate", "<bool/double/float/int/string> <key> <value>", "Add a new world config value"),
    ];

    [Fact]
    public void ParsesHelpLine_FromServerOutput()
    {
        var raw = "<code>/tp <i>&lt;source&gt;</i> <i>&lt;target&gt;</i> </code> :  Teleport a player or entity to a location";
        Assert.True(ServerCommands.TryParseHelpLine(raw, out var c));
        Assert.Equal(new ServerCommand("tp", "<source> <target>", "Teleport a player or entity to a location"), c);
        Assert.Equal("/tp <source> <target>", c.Usage);
    }

    [Fact]
    public void ParsesCommandWithoutArgs_AndTakesFirstLineOfDescription()
    {
        var raw = "<code>/we </code> :  Creative mode world editing tools.<br> If you want to enable the old commands you can do so with "
                  + "<a href=\"chattype:///worldconfigcreate bool legacywecommands true\">/worldconfigcreate bool legacywecommands true</a>";
        Assert.True(ServerCommands.TryParseHelpLine(raw, out var c));
        Assert.Equal(("we", "", "Creative mode world editing tools."), (c.Name, c.Args, c.Description));
    }

    [Theory]
    [InlineData("2.10.2026 01:42:52 [Server Notification] Available commands:")]
    [InlineData("2.10.2026 01:42:52 [Server Notification] Handling Console Command /help")]
    [InlineData("")]
    public void IgnoresOtherLines(string raw) => Assert.False(ServerCommands.TryParseHelpLine(raw, out _));

    [Fact]
    public void Suggests_ByPrefix_ExactFirst()
    {
        Assert.Equal(["tp", "tpwp"], ServerCommands.Suggest(All, "/tp").Select(c => c.Name));
        Assert.Equal(["time", "timeswitch"], ServerCommands.Suggest(All, "/TI").Select(c => c.Name));
        Assert.Empty(ServerCommands.Suggest(All, "/tp Erney"));   // дальше аргументы
        Assert.Empty(ServerCommands.Suggest(All, "say hi"));      // не команда
        Assert.Equal(6, ServerCommands.Suggest(All, "/").Count);
    }

    [Fact]
    public void Tab_CompletesCommonPrefix_OrTheWholeCommand()
    {
        Assert.Equal("/worldconfig", ServerCommands.Complete(ServerCommands.Suggest(All, "/wor"), "/wor"));
        Assert.Equal("/tpwp ", ServerCommands.Complete(ServerCommands.Suggest(All, "/tpw"), "/tpw"));
        Assert.Null(ServerCommands.Complete(ServerCommands.Suggest(All, "/t"), "/t")); // «tp», «time» — общего продолжения нет
        Assert.Null(ServerCommands.Complete([], "/zzz"));
    }

    [Fact]
    public void FindsTypedCommand_ForArgsHint()
    {
        Assert.Equal("tp", ServerCommands.Typed(All, "/tp Erney ")?.Name);
        Assert.Equal("tp", ServerCommands.Typed(All, "/TP")?.Name);
        Assert.Null(ServerCommands.Typed(All, "/teleport x"));
    }

    [Fact]
    public void SavesAndLoads()
    {
        var file = Path.Combine(Path.GetTempPath(), "evistool-cmds-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            ServerCommands.Save(file, All);
            Assert.Equal(All.OrderBy(c => c.Name), ServerCommands.Load(file));
            Assert.Empty(ServerCommands.Load(file + ".missing"));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
