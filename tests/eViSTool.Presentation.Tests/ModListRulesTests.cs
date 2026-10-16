using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Presentation.ViewModels;
using eViSTool.Presentation.ViewModels.Mods;

namespace eViSTool.Presentation.Tests;

/// <summary>Таблица «Моих модов»: какие строки проходят чип над таблицей и поиск, в каком порядке они идут.</summary>
public sealed class ModListRulesTests
{
    private static ModRowViewModel Row(string modId, string name, ModStatus status = ModStatus.UpToDate, bool disabled = false,
        bool pinned = false, string? side = null, ProfileKind kind = ProfileKind.Client, bool canUpdate = false)
    {
        var local = new LocalMod($"/mods/{modId}.zip", new ModInfo { ModId = modId, Name = name, Version = "1.0.0", Side = side }, null)
        {
            IsDisabled = disabled,
        };
        var latest = canUpdate ? new ModDbRelease { ModVersion = "2.0.0", MainFile = "https://mods.example/x.zip" } : null;
        return new ModRowViewModel(new ModCheckResult { Local = local, Status = status, LatestCompatible = latest }, [], pinned, kind);
    }

    // по одному моду на каждый чип; клиентский мод «не нужен» только серверному профилю
    private static readonly ModRowViewModel[] Rows =
    [
        Row("alpha", "Alpha"),
        Row("beta", "Beta", ModStatus.UpdateAvailable, canUpdate: true),
        Row("gamma", "Gamma", ModStatus.NoCompatibleRelease),
        Row("delta", "Delta", disabled: true),
        Row("eps", "Eps", pinned: true),
        Row("zeta", "Zeta", side: "Client", kind: ProfileKind.Server),
    ];

    [Theory]
    [InlineData(ModFilter.All, "Alpha,Beta,Gamma,Delta,Eps,Zeta")]
    [InlineData(ModFilter.History, "Alpha,Beta,Gamma,Delta,Eps,Zeta")] // таблицу закрывает история — сама строка не прячется
    [InlineData(ModFilter.Updates, "Beta")]
    [InlineData(ModFilter.Problems, "Gamma")]
    [InlineData(ModFilter.Pinned, "Eps")]
    [InlineData(ModFilter.Disabled, "Delta")]
    [InlineData(ModFilter.Unneeded, "Zeta")]
    public void Chip_ShowsItsRows(ModFilter filter, string expected)
    {
        var shown = Rows.Where(r => ModListRules.Matches(r, filter, "")).Select(r => r.Name);

        Assert.Equal(expected.Split(','), shown);
    }

    // ищется и по названию, и по modid; регистр не важен, в том числе у кириллицы
    [Theory]
    [InlineData("CARRY", true)]
    [InlineData("перенос", true)]
    [InlineData("ВЕЩ", true)]
    [InlineData("xyz", false)]
    public void Search_ByNameOrModId_IgnoringCase(string search, bool expected)
    {
        var row = Row("carryon", "Перенос вещей");

        Assert.Equal(expected, ModListRules.Matches(row, ModFilter.All, search));
    }

    [Fact]
    public void Search_AndChip_BothApply()
    {
        var beta = Rows.Single(r => r.ModId == "beta");

        Assert.True(ModListRules.Matches(beta, ModFilter.Updates, "bet"));
        Assert.False(ModListRules.Matches(beta, ModFilter.Updates, "alpha")); // чип пропускает, поиск — нет
        Assert.False(ModListRules.Matches(beta, ModFilter.Problems, "bet"));  // поиск пропускает, чип — нет
    }

    // «Сначала важное»: проблемы, обновления, остальные, выключенные; внутри каждой группы — по названию
    [Fact]
    public void Important_ProblemsUpdatesRestDisabled_ByNameInside()
    {
        var rows = new[]
        {
            Row("d2", "Ящик", disabled: true),
            Row("n2", "Bravo"),
            Row("u1", "Zulu", ModStatus.UpdateAvailable, canUpdate: true),
            Row("p1", "Yankee", ModStatus.NoCompatibleRelease),
            Row("n1", "Alpha"),
            Row("d1", "Альфа", disabled: true),
        }.ToList();

        rows.Sort(ModListRules.Order(0));

        Assert.Equal(["Yankee", "Zulu", "Alpha", "Bravo", "Альфа", "Ящик"], rows.Select(r => r.Name));
    }

    [Fact]
    public void ByName_IgnoresImportance()
    {
        var rows = new[]
        {
            Row("c", "Charlie", ModStatus.NoCompatibleRelease),
            Row("a", "Alpha", disabled: true),
            Row("b", "Bravo", ModStatus.UpdateAvailable, canUpdate: true),
        }.ToList();

        rows.Sort(ModListRules.Order(1));

        Assert.Equal(["Alpha", "Bravo", "Charlie"], rows.Select(r => r.Name));
    }
}
