using eViSTool.Core.Server.Config;

namespace eViSTool.Core.Tests;

public sealed class WorldSettingCatalogTests : IDisposable
{
    private readonly ConfigSamples _samples = new();

    public void Dispose() => _samples.Dispose();

    [Fact]
    public void Catalog_HasAllSettingsOfTheBaseGame()
    {
        var all = WorldSettingCatalog.All;

        Assert.Equal(64, all.Count);
        // игра сравнивает ключи без учёта регистра — и так они тоже разные
        Assert.Equal(64, all.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, s => Assert.False(string.IsNullOrWhiteSpace(s.Key)));
        Assert.All(all, s => Assert.DoesNotContain('.', s.Key));

        Assert.Equal(51, all.Count(s => s.Type == WorldSettingType.Choice));
        Assert.Equal(13, all.Count(s => s.Type == WorldSettingType.Bool));
        // «нельзя менять после создания мира»: стартовый климат, фора и почти вся генерация мира
        Assert.Equal(15, all.Count(s => s.OnlyAtWorldCreation));
        Assert.Equal(13, all.Count(s => s.OnlyAtWorldCreation && s.Category == "worldgen"));
    }

    [Fact]
    public void Catalog_ValuesAndDefaultsAreConsistent()
    {
        Assert.All(WorldSettingCatalog.All, s =>
        {
            Assert.NotNull(s.Default);
            Assert.Equal(s.Values.Count, s.Values.Distinct().Count());
            Assert.Contains(s.Default, s.Values);
            if (s.Type == WorldSettingType.Bool)
                Assert.Equal(["true", "false"], s.Values);
            else
                Assert.True(s.Values.Count >= 2, s.Key);
        });

        var copper = WorldSettingCatalog.Find("surfaceCopperDeposits")!;
        Assert.Equal(WorldSettingType.Choice, copper.Type);
        Assert.Equal("0.12", copper.Default); // значения — числа строкой
        Assert.Equal(["1", "0.5", "0.2", "0.12", "0.05", "0.015", "0"], copper.Values);
        Assert.Equal("worldgen", copper.Category);
        Assert.False(copper.OnlyAtWorldCreation);

        var width = WorldSettingCatalog.Find("worldWidth")!;
        Assert.True(width.OnlyAtWorldCreation);
        Assert.Equal("1024000", width.Default);

        var map = WorldSettingCatalog.Find("colorAccurateWorldmap")!;
        Assert.Equal((WorldSettingType.Bool, "false", "survivalchallenges"), (map.Type, map.Default, map.Category));
        Assert.Equal("ws.colorAccurateWorldmap", map.LabelKey);
        Assert.Equal("ws.colorAccurateWorldmap.hint", map.HintKey);
        Assert.Equal("ws.category.survivalchallenges", map.CategoryKey);
        Assert.Equal("ws.landcover.value.0.975", WorldSettingCatalog.Find("landcover")!.ValueLabelKey("0.975"));
    }

    [Fact]
    public void Catalog_CategoriesGoInDisplayOrder()
    {
        Assert.Equal(
            ["spawnndeath", "survivalchallenges", "temporalstability", "worldgen", "multiplayer"],
            WorldSettingCatalog.Categories);

        // настройки идут по категориям подряд, в том же порядке
        Assert.Equal(WorldSettingCatalog.Categories, WorldSettingCatalog.All.Select(s => s.Category).Distinct());
        var runs = WorldSettingCatalog.All.Select(s => s.Category).Aggregate(new List<string>(), (list, c) =>
        {
            if (list.Count == 0 || list[^1] != c) list.Add(c);
            return list;
        });
        Assert.Equal(WorldSettingCatalog.Categories, runs);
    }

    [Fact]
    public void Find_IgnoresCase_AndDoesNotKnowForeignKeys()
    {
        var setting = WorldSettingCatalog.Find("surfaceCopperDeposits");

        Assert.NotNull(setting);
        Assert.Same(setting, WorldSettingCatalog.Find("SURFACECOPPERDEPOSITS"));
        Assert.Same(setting, WorldSettingCatalog.Find("surfacecopperdeposits"));
        Assert.Equal("surfaceCopperDeposits", setting.Key);

        Assert.Null(WorldSettingCatalog.Find("surfacecopper"));
        Assert.Null(WorldSettingCatalog.Find("surfacetin"));
        Assert.Null(WorldSettingCatalog.Find(""));
        Assert.Null(WorldSettingCatalog.Find(" gameMode"));
        Assert.Null(WorldSettingCatalog.Find("modsetting"));
        Assert.Null(WorldSettingCatalog.Find(null!));
    }

    [Fact]
    public void RealSample_HasKeysTheGameDoesNotKnow()
    {
        // живой конфиг: surfacecopper и surfacetin — не настоящие ключи (игра их молча пропускает), colorAccurateWorldmap — настоящий
        var settings = _samples.Open().WorldSettings;

        Assert.Equal(["surfacecopper", "surfacetin", "colorAccurateWorldmap"], settings.Select(s => s.Key));
        Assert.Equal(["colorAccurateWorldmap"], settings.Where(s => WorldSettingCatalog.Find(s.Key) is not null).Select(s => s.Key));
    }

    [Fact]
    public void PresetDefaults_AreWhatEachPlayStyleSets()
    {
        // у каждого стиля из схемы есть свой набор
        var styles = ServerConfigSchema.Known.Single(f => f.Path == "WorldConfig.PlayStyle").Choices.Select(c => c.Value).ToList();
        Assert.All(styles, style => Assert.NotEmpty(WorldSettingCatalog.PresetDefaults(style)));

        var standard = WorldSettingCatalog.PresetDefaults("surviveandbuild");
        Assert.Equal(18, standard.Count);
        Assert.Equal("0", standard["graceTimer"]);
        Assert.Equal("0", standard["GRACETIMER"]); // ключи — без учёта регистра, как в игре
        Assert.Equal("5", WorldSettingCatalog.PresetDefaults("exploration")["graceTimer"]);
        Assert.Equal("creative", WorldSettingCatalog.PresetDefaults("creativebuilding")["gameMode"]);

        // стиль задаёт и то, чего среди настроек мира нет
        Assert.Equal("2400", WorldSettingCatalog.PresetDefaults("creativebuilding")["hoursPerDay"]);
        Assert.Null(WorldSettingCatalog.Find("hoursPerDay"));

        // остальные ключи наборов — настоящие настройки
        Assert.All(styles.SelectMany(style => WorldSettingCatalog.PresetDefaults(style).Keys).Distinct(), key =>
            Assert.True(key is "hoursPerDay" or "cloudypos" || WorldSettingCatalog.Find(key) is not null, key));

        // незнакомый стиль (от мода), другой регистр, нет стиля — пусто
        Assert.Empty(WorldSettingCatalog.PresetDefaults("modstyle"));
        Assert.Empty(WorldSettingCatalog.PresetDefaults("Exploration"));
        Assert.Empty(WorldSettingCatalog.PresetDefaults(null));
    }

    [Fact]
    public void DefaultFor_PrefersPlayStyleValue()
    {
        Assert.Equal("5", WorldSettingCatalog.DefaultFor("graceTimer", "exploration"));
        Assert.Equal("5", WorldSettingCatalog.DefaultFor("gracetimer", "exploration"));
        Assert.Equal("0", WorldSettingCatalog.DefaultFor("graceTimer", "modstyle"));
        Assert.Equal("0", WorldSettingCatalog.DefaultFor("graceTimer", null));

        // стиль настройку не трогает — умолчание самой настройки
        Assert.Equal("-1", WorldSettingCatalog.DefaultFor("playerlives", "exploration"));
        Assert.Equal("true", WorldSettingCatalog.DefaultFor("auctionHouse", "wildernesssurvival"));

        // ключ вне каталога, но из набора стиля
        Assert.Equal("0.5", WorldSettingCatalog.DefaultFor("cloudypos", "creativebuilding"));
        Assert.Null(WorldSettingCatalog.DefaultFor("cloudypos", "exploration"));
        Assert.Null(WorldSettingCatalog.DefaultFor("surfacecopper", "surviveandbuild"));
    }
}
