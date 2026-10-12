using eViSTool.Core.Mods;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App.Tests;

/// <summary>Копия миров перед изменением мода: клиентский мод мир не меняет — без копии.</summary>
public sealed class WorldCopyBySideTests
{
    [Theory]
    [InlineData("Client", false)]
    [InlineData("client", false)]
    [InlineData("Server", true)]
    [InlineData("Universal", true)]
    [InlineData(null, true)]
    public void NeedsWorldCopy_ByModInfoSide(string? side, bool expected) =>
        Assert.Equal(expected, ModsViewModel.NeedsWorldCopy(ModSides.Parse(side)));
}
