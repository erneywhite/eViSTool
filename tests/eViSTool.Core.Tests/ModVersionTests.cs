using eViSTool.Core.Versioning;

namespace eViSTool.Core.Tests;

public class ModVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1.2.3.4", "1.2.3", 1)]
    [InlineData("v2.0.0", "1.9.9", 1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]
    [InlineData("1.0.0-pre.1", "1.0.0-rc.1", -1)]
    [InlineData("1.500.7", "1.500.6", 1)]
    [InlineData("1.0.0+build5", "1.0.0", 0)]
    public void Compare(string a, string b, int expected)
    {
        var va = ModVersion.ParseOrNull(a)!;
        var vb = ModVersion.ParseOrNull(b)!;
        Assert.Equal(expected, Math.Sign(va.CompareTo(vb)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.x.2")]
    public void RejectsGarbage(string s) => Assert.False(ModVersion.TryParse(s, out _));

    [Fact]
    public void SameBranch()
    {
        var game = ModVersion.ParseOrNull("1.22.7")!;
        Assert.True(ModVersion.ParseOrNull("1.22.0-rc.3")!.SameBranch(game));
        Assert.False(ModVersion.ParseOrNull("1.21.7")!.SameBranch(game));
    }
}
