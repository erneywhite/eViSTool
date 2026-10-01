using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

public class ModSideTests
{
    [Theory]
    [InlineData("Universal", ModSide.Both)]
    [InlineData("universal", ModSide.Both)]
    [InlineData("Client", ModSide.Client)]
    [InlineData("server", ModSide.Server)]
    [InlineData("both", ModSide.Both)] // так пишет модбаза
    [InlineData(null, ModSide.Both)]   // не указано — игра считает мод для обеих сторон
    [InlineData("", ModSide.Both)]
    public void ParsesModInfoAndModDb(string? side, ModSide expected) => Assert.Equal(expected, ModSides.Parse(side));

    [Fact]
    public void OnlyTheOtherSideIsUnneeded()
    {
        Assert.True(ModSides.IsUnneeded(ModSide.Client, ProfileKind.Server));
        Assert.False(ModSides.IsUnneeded(ModSide.Server, ProfileKind.Client)); // у игрока он нужен одиночной игре
        Assert.False(ModSides.IsUnneeded(ModSide.Client, ProfileKind.Client));
        Assert.False(ModSides.IsUnneeded(ModSide.Server, ProfileKind.Server));
        Assert.False(ModSides.IsUnneeded(ModSide.Both, ProfileKind.Server));
        Assert.False(ModSides.IsUnneeded(ModSide.Both, ProfileKind.Client));
    }
}
