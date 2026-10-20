using eViSTool.Core.ModDb;

namespace eViSTool.Core.Tests;

/// <summary>
/// Мод в каталоге и установленные: у мода, сменившего modid в новой версии, модбаза отдаёт оба (GiMap: gimap, gimapcore).
/// Установленным считается любой из них — иначе после установки GiMap 3.0.0 (gimapcore) каталог снова предлагал
/// «Установить», а нажатие только моргало.
/// </summary>
public sealed class ModDbListItemTests
{
    private static readonly ModDbListItem GiMap = new() { Name = "GiMap", ModIdStrs = ["gimap", "gimapcore"] };

    [Fact]
    public void Any_of_the_modids_counts_as_installed()
    {
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["gimapcore"] = "3.0.0" };
        Assert.Equal("gimapcore", GiMap.InstalledModId(installed));
    }

    [Fact]
    public void The_first_modid_wins_when_both_are_installed()
    {
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["GIMAP"] = "2.1.0", ["gimapcore"] = "3.0.0" };
        Assert.Equal("gimap", GiMap.InstalledModId(installed));
    }

    [Fact]
    public void Not_installed_is_null() =>
        Assert.Null(GiMap.InstalledModId(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["carryon"] = "1.0" }));
}
