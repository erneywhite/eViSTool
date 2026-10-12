using eViSTool.Presentation;

namespace eViSTool.Presentation.Tests;

/// <summary>Разбор EVISTOOL_SNAPSHOT: окно читает его так.</summary>
public sealed class SnapshotsTests
{
    [Fact]
    public void Parse_FullRequest()
    {
        var r = Snapshots.Parse(@"screen=Server.Config.World; out=C:\snap\a.png; size=1280x720; wait=2.5; profile=Стенд")!;
        Assert.Equal("server.config.world", r.Screen);
        Assert.Equal(@"C:\snap\a.png", r.OutPath);
        Assert.Equal((1280, 720), (r.Width, r.Height));
        Assert.Equal(TimeSpan.FromSeconds(2.5), r.Wait);
        Assert.Equal("Стенд", r.Profile);
    }

    [Fact]
    public void Parse_Defaults()
    {
        var r = Snapshots.Parse("screen=mods;out=a.png")!;
        Assert.Equal((1400, 860), (r.Width, r.Height));
        Assert.Equal(TimeSpan.FromSeconds(4), r.Wait);
        Assert.Null(r.Profile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("screen=mods")]
    [InlineData("out=a.png")]
    public void Parse_Incomplete_IsNull(string? text) => Assert.Null(Snapshots.Parse(text));
}
