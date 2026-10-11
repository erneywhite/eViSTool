using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

public sealed class ServerConsoleTests
{
    [Fact]
    public void HugeLine_IsCut_OrdinaryLinesStayWhole()
    {
        var console = new ServerConsole();
        var huge = console.Add(ConsoleLineKind.Output, new string('x', 5_000_000)); // мод вывел мегабайты одной строкой
        var normal = console.Add(ConsoleLineKind.Output, "Handling Console Command /time");

        Assert.Equal(ServerConsole.MaxLineLength + 1, huge.Text.Length);
        Assert.EndsWith("…", huge.Text);
        Assert.Equal("Handling Console Command /time", normal.Text);
    }

    [Fact]
    public void OldLines_AreDropped_OverCapacity()
    {
        var console = new ServerConsole(capacity: 3);
        for (var i = 1; i <= 5; i++) console.Add(ConsoleLineKind.Output, $"line {i}");
        Assert.Equal(["line 3", "line 4", "line 5"], console.GetSince(0).Select(l => l.Text));
    }
}
