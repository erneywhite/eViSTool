using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Tests;

/// <summary>Какой релиз предлагает карточка каталога под версию игры профиля (аудит, пункт 10).</summary>
public sealed class CatalogPickTests
{
    private static ModDbRelease R(string version, params string[] games) =>
        new() { ModVersion = version, MainFile = "https://x/" + version, Tags = [.. games] };

    private static readonly ModDbRelease[] Releases =
    [
        R("1.0.0", "1.21.5"),
        R("1.1.0", "1.22.3"),
        R("1.2.0-pre.1", "1.22.7"),
        R("2.0.0-rc.1", "1.23.0"),
    ];

    private static ModVersion V(string v) => ModVersion.ParseOrNull(v)!;

    [Fact]
    public void KnownVersion_PicksTheStableReleaseForItsBranch()
    {
        var choice = CatalogPick.Best(Releases, V("1.22.7"));
        Assert.Equal("1.1.0", choice.Release!.ModVersion);
        Assert.Equal(CatalogFit.Compatible, choice.Fit);

        Assert.Equal("1.0.0", CatalogPick.Best(Releases, V("1.21.1")).Release!.ModVersion); // другой профиль — другая ветка
    }

    [Fact]
    public void AlreadyOnAPrerelease_GetsTheNewestPrereleaseForTheBranch()
    {
        var choice = CatalogPick.Best(Releases, V("1.22.7"), onPrerelease: true);
        Assert.Equal("1.2.0-pre.1", choice.Release!.ModVersion);
        Assert.Equal(CatalogFit.CompatiblePrerelease, choice.Fit);
    }

    [Fact]
    public void OnlyAPrereleaseForTheBranch_IsMarkedSo()
    {
        var choice = CatalogPick.Best(Releases, V("1.23.0"));
        Assert.Equal("2.0.0-rc.1", choice.Release!.ModVersion);
        Assert.Equal(CatalogFit.CompatiblePrerelease, choice.Fit);
    }

    [Fact]
    public void NoReleaseForTheBranch_NothingIsOffered()
    {
        var choice = CatalogPick.Best(Releases, V("1.20.0"));
        Assert.Null(choice.Release);
        Assert.Equal(CatalogFit.NoneForBranch, choice.Fit);
        Assert.Equal("2.0.0-rc.1", choice.Latest!.ModVersion); // для подсказки «последняя — для 1.23»
    }

    [Fact]
    public void UnknownVersion_OffersTheLatestStable_NotAPrerelease_AndSaysItIsUnchecked()
    {
        var choice = CatalogPick.Best(Releases, null);
        Assert.Equal("1.1.0", choice.Release!.ModVersion);
        Assert.Equal(CatalogFit.UnknownGame, choice.Fit);

        // стабильных нет вовсе — тогда уж предварительный
        Assert.Equal("2.0.0-rc.1", CatalogPick.Best([R("2.0.0-rc.1", "1.23.0")], null).Release!.ModVersion);
    }

    [Fact]
    public void Fits_ChecksTheBranch_AndUnknownIsNeverAFit()
    {
        Assert.True(CatalogPick.Fits(Releases[1], V("1.22.7")));
        Assert.False(CatalogPick.Fits(Releases[0], V("1.22.7")));
        Assert.False(CatalogPick.Fits(Releases[1], null));
    }
}
