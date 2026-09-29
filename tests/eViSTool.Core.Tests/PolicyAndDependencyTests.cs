using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Tests;

public class PolicyAndDependencyTests
{
    private static readonly ModVersion Game = ModVersion.ParseOrNull("1.22.7")!;

    private static ModDbRelease Rel(string version) =>
        new() { ModVersion = version, Tags = ["1.22.0"], MainFile = $"https://cdn/{version}.zip" };

    private static LocalMod Local(string id, string version, string deps = "", bool disabled = false)
    {
        var depMap = deps.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Split('='))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "", StringComparer.OrdinalIgnoreCase);
        return new LocalMod($"C:/Mods/{id}.zip", new ModInfo { ModId = id, OriginalModId = id, Name = id, Version = version, Dependencies = depMap }, null)
        {
            IsDisabled = disabled,
        };
    }

    private static Dictionary<string, ModDbResult> Remote(string id, params string[] versions) =>
        new() { [id] = new(new ModDbMod { Releases = versions.Select(Rel).ToList() }, null) };

    [Fact]
    public void PinnedModIsNotOfferedUpdate()
    {
        var policy = new ModPolicy(new Dictionary<string, string> { ["a"] = "1.0.0" }, new Dictionary<string, List<string>>());
        var r = UpdateChecker.Evaluate([Local("a", "1.0.0")], Remote("a", "1.0.0", "1.1.0"), Game, policy: policy).Single();
        Assert.Equal(ModStatus.Pinned, r.Status);
        Assert.Equal("1.1.0", r.LatestCompatible!.ModVersion); // знаем, что есть, но не предлагаем
    }

    [Fact]
    public void BlockedVersionIsSkipped()
    {
        var policy = new ModPolicy(new Dictionary<string, string>(), new Dictionary<string, List<string>> { ["a"] = ["1.2.0"] });

        var onlyBlocked = UpdateChecker.Evaluate([Local("a", "1.1.0")], Remote("a", "1.1.0", "1.2.0"), Game, policy: policy).Single();
        Assert.Equal(ModStatus.UpToDate, onlyBlocked.Status);

        var nextOne = UpdateChecker.Evaluate([Local("a", "1.1.0")], Remote("a", "1.1.0", "1.2.0", "1.3.0"), Game, policy: policy).Single();
        Assert.Equal(ModStatus.UpdateAvailable, nextOne.Status);
        Assert.Equal("1.3.0", nextOne.LatestCompatible!.ModVersion);
    }

    [Fact]
    public void FindsMissingOutdatedAndDisabledDependencies()
    {
        var mods = new[]
        {
            Local("carryon", "2.0.0", "carryonlib=1.0.0,game=1.22.0"),
            Local("xskills", "1.1.0", "xlib=1.0.37,vsimgui="),
            Local("xlib", "1.0.30"),
            Local("pack", "1.0", "vsimgui=,footprints="),
            Local("footprints", "1.2.0", disabled: true),
            Local("ignored", "1.0", "missingbutdisabledmod=", disabled: true),
        };

        var issues = Dependencies.FindIssues(mods).ToDictionary(i => i.ModId);

        Assert.True(issues["carryonlib"].IsMissing);
        Assert.True(issues["xlib"].IsOutdated);
        Assert.Equal("1.0.30", issues["xlib"].InstalledVersion);
        Assert.True(issues["footprints"].IsDisabled);
        Assert.Equal(2, issues["vsimgui"].RequiredBy.Count);   // нужен двоим — одна запись
        Assert.False(issues.ContainsKey("game"));              // базовая игра не зависимость
        Assert.False(issues.ContainsKey("missingbutdisabledmod")); // требования выключенных модов не считаются
    }
}
