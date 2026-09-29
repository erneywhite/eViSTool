using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Tests;

public class UpdateCheckerTests
{
    private static readonly ModVersion Game = ModVersion.ParseOrNull("1.22.7")!;

    private static ModDbRelease Rel(string version, params string[] tags) =>
        new() { ModVersion = version, Tags = tags.Cast<string?>().ToList(), MainFile = $"https://cdn/{version}.zip" };

    private static LocalMod Local(string id, string version) =>
        new($"C:/Mods/{id}_{version}.zip", new ModInfo { ModId = id, Name = id, Version = version }, null);

    [Fact]
    public void PicksByBranchNotExactPatch()
    {
        // автор отметил только 1.22.3, игра 1.22.7 — релиз всё равно подходит
        var releases = new[] { Rel("2.0.0", "1.23.0"), Rel("1.5.0", "1.22.3"), Rel("1.4.0", "1.22.0") };
        Assert.Equal("1.5.0", UpdateChecker.PickLatestCompatible(releases, Game, false)!.ModVersion);
    }

    [Fact]
    public void SkipsUnstableUnlessAllowed()
    {
        var releases = new[] { Rel("1.6.0-rc.1", "1.22.7"), Rel("1.5.0", "1.22.7") };
        Assert.Equal("1.5.0", UpdateChecker.PickLatestCompatible(releases, Game, false)!.ModVersion);
        Assert.Equal("1.6.0-rc.1", UpdateChecker.PickLatestCompatible(releases, Game, true)!.ModVersion);
    }

    [Fact]
    public void EvaluatesStatuses()
    {
        var locals = new[]
        {
            Local("uptodate", "1.5.0"),
            Local("outdated", "1.4.0"),
            Local("oldgame", "0.9.0"),
            Local("missing", "1.0.0"),
            new LocalMod("C:/Mods/broken.zip", null, "В архиве нет modinfo.json"),
        };
        var remote = new Dictionary<string, ModDbResult>
        {
            ["uptodate"] = new(new ModDbMod { Releases = [Rel("1.5.0", "1.22.0")] }, null),
            ["outdated"] = new(new ModDbMod { Releases = [Rel("1.5.0", "1.22.0"), Rel("2.0.0", "1.23.0")] }, null),
            ["oldgame"] = new(new ModDbMod { Releases = [Rel("1.0.0", "1.21.0")] }, null),
            ["missing"] = new(null, null),
        };

        var r = UpdateChecker.Evaluate(locals, remote, Game).ToDictionary(x => x.Local.Info?.ModId ?? "broken");

        Assert.Equal(ModStatus.UpToDate, r["uptodate"].Status);
        Assert.Equal(ModStatus.UpdateAvailable, r["outdated"].Status);
        Assert.Equal("2.0.0", r["outdated"].LatestAny!.ModVersion);
        Assert.Equal(ModStatus.NoCompatibleRelease, r["oldgame"].Status);
        Assert.Equal(ModStatus.NotInModDb, r["missing"].Status);
        Assert.Equal(ModStatus.Unreadable, r["broken"].Status);
    }

    [Fact]
    public void PrereleaseInstalledConsidersPrereleases()
    {
        // стоит 2.0.0-pre.8 — не предлагать «обновление» на стабильную 1.14.3
        var locals = new[] { Local("carryon", "2.0.0-pre.8") };
        var remote = new Dictionary<string, ModDbResult>
        {
            ["carryon"] = new(new ModDbMod { Releases = [Rel("1.14.3", "1.22.0"), Rel("2.0.0-pre.8", "1.22.0"), Rel("2.0.0-pre.9", "1.22.5")] }, null),
        };
        var r = UpdateChecker.Evaluate(locals, remote, Game).Single();
        Assert.Equal(ModStatus.UpdateAvailable, r.Status);
        Assert.Equal("2.0.0-pre.9", r.LatestCompatible!.ModVersion);
    }

    [Fact]
    public void LatestButUntaggedIsUpToDate()
    {
        var locals = new[] { Local("morebags", "1.4.1") };
        var remote = new Dictionary<string, ModDbResult> { ["morebags"] = new(new ModDbMod { Releases = [Rel("1.4.1", "1.21.0")] }, null) };
        var r = UpdateChecker.Evaluate(locals, remote, Game).Single();
        Assert.Equal(ModStatus.UpToDate, r.Status);
        Assert.Null(r.LatestAny);
    }

    [Fact]
    public void FindsInCatalogByName()
    {
        var catalog = new List<ModDbListItem>
        {
            new() { ModId = 4531, Name = "xSkills Gilded", ModIdStrs = ["xskillgilded"] },
            new() { ModId = 1, Name = "xSkills Gilded patch 1.21", ModIdStrs = ["xskillgildedpatch"] },
        };
        var found = ModUpdateService.FindInCatalog(catalog, new ModInfo { ModId = "xskillsgilded", Name = "xSkills Gilded" });
        Assert.Equal(4531, found!.ModId);
    }

    [Fact]
    public void FlagsDuplicates()
    {
        var locals = new[] { Local("a", "1.0"), Local("a", "1.1") };
        var remote = new Dictionary<string, ModDbResult> { ["a"] = new(new ModDbMod { Releases = [Rel("1.1", "1.22.0")] }, null) };
        Assert.All(UpdateChecker.Evaluate(locals, remote, Game), x => Assert.True(x.IsDuplicate));
    }
}
