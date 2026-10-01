using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

public class ConsoleMarkupTests
{
    [Fact]
    public void HelpLine_BecomesPlainText()
    {
        var line = "<code>/tp <i>&lt;source&gt;</i> <i>&lt;target&gt;</i> </code> :  Teleport a player or entity to a location";
        Assert.Equal("/tp <source> <target>  :  Teleport a player or entity to a location", ConsoleMarkup.ToPlain(line));
    }

    [Fact]
    public void LinksAndLineBreaks()
    {
        var line = "Creative tools.<br> Enable them with <a href=\"chattype:///worldconfigcreate bool x true\">/worldconfigcreate bool x true</a>";
        Assert.Equal("Creative tools.\nEnable them with /worldconfigcreate bool x true", ConsoleMarkup.ToPlain(line));
    }

    [Theory]
    [InlineData("System.Collections.Generic.Dictionary<string, int>.get_Item(String key)")]
    [InlineData(@"at Foo.Bar<T>() in C:\mods\a.cs:line 5")]
    [InlineData("1.10.2026 03:28:24 [Server Event] Dedicated Server now running on Port 42420 and all ips!")]
    public void OrdinaryLines_StayAsTheyAre(string line) => Assert.Equal(line, ConsoleMarkup.ToPlain(line));
}
