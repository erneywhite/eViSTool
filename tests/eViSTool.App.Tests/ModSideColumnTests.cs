using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App.Tests;

/// <summary>Колонка «Сторона» в «Моих модах» и пояснение под modid: в колонке — сторона, под modid — что это значит здесь.</summary>
public sealed class ModSideColumnTests
{
    private static ModRowViewModel Row(string? side, ProfileKind kind)
    {
        Loc.Instance.SetLanguage("ru");
        var mod = new LocalMod("/mods/m.zip", new ModInfo { ModId = "m", OriginalModId = "m", Name = "M", Version = "1.0.0", Side = side }, null);
        var result = UpdateChecker.Evaluate([mod], new Dictionary<string, ModDbResult>(), ModVersion.ParseOrNull("1.22.7")!).Single();
        return new ModRowViewModel(result, [], false, kind, ModVersion.ParseOrNull("1.22.7"));
    }

    [Theory]
    [InlineData("Client", ProfileKind.Client, ModSide.Client, "клиент", "")]
    [InlineData("Client", ProfileKind.Server, ModSide.Client, "клиент", " · серверу не нужен")]
    [InlineData("Server", ProfileKind.Client, ModSide.Server, "сервер", " · для одиночной игры")]
    [InlineData("Server", ProfileKind.Server, ModSide.Server, "сервер", "")]
    [InlineData("Universal", ProfileKind.Client, ModSide.Both, "оба", "")]
    [InlineData(null, ProfileKind.Server, ModSide.Both, "оба", "")]
    public void Side_GoesToTheColumn_AndOnlyTheMeaningStaysUnderModId(string? side, ProfileKind kind, ModSide expected, string text, string note)
    {
        var row = Row(side, kind);
        Assert.Equal(expected, row.Side);
        Assert.Equal(text, row.SideText);
        Assert.Equal(note, row.SideNote);
    }
}
