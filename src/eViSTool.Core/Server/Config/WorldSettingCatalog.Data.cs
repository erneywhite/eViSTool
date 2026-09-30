// Данные каталога настроек мира: базовая игра 1.22.7 (мод survival). Список собран скриптом из данных
// исследования игры; дальше это обычный код — правь руками, когда настройки в игре поменяются.

namespace eViSTool.Core.Server.Config;

public static partial class WorldSettingCatalog
{
    // категории — как их называет игра
    private const string
        SpawnAndDeath = "spawnndeath",
        Challenges = "survivalchallenges",
        Temporal = "temporalstability",
        WorldGen = "worldgen",
        Multiplayer = "multiplayer";

    private static string[] CreateCategories() => [SpawnAndDeath, Challenges, Temporal, WorldGen, Multiplayer];

    /// <summary>Настройки в порядке игры; у Choice первым идёт значение по умолчанию, за ним — все допустимые.</summary>
    private static WorldSettingInfo[] CreateAll() =>
    [
        // ---- появление и смерть игрока
        Choice("gameMode", SpawnAndDeath, "survival", ["survival", "creative"]),
        Choice("playerlives", SpawnAndDeath, "-1", ["1", "2", "3", "4", "5", "10", "20", "-1"]),
        Choice("startingClimate", SpawnAndDeath, "temperate", ["hot", "warm", "temperate", "cool", "icy"], once: true),
        Choice("spawnRadius", SpawnAndDeath, "50", ["10000", "5000", "2500", "1000", "500", "250", "100", "50", "25", "0"]),
        Choice("graceTimer", SpawnAndDeath, "0", ["10", "5", "4", "3", "2", "1", "0"], once: true),
        Choice("deathPunishment", SpawnAndDeath, "drop", ["drop", "keep"]),
        Choice("droppedItemsTimer", SpawnAndDeath, "600", ["300", "600", "1200", "1800", "3600"]),

        // ---- испытания на выживание
        Choice("seasons", Challenges, "enabled", ["enabled", "spring", "summer", "fall", "winter"]),
        Choice("daysPerMonth", Challenges, "9", ["30", "20", "12", "9", "6", "3"]),
        Choice("harshWinters", Challenges, "true", ["true", "false"]),
        Choice("blockGravity", Challenges, "sandgravel", ["sandgravel", "sandgravelsoil"]),
        Choice("caveIns", Challenges, "off", ["off", "on"]),
        Bool("allowFallingBlocks", Challenges, true),
        Bool("allowFireSpread", Challenges, true),
        Bool("lightningFires", Challenges, false),
        Bool("allowUndergroundFarming", Challenges, false),
        Bool("noLiquidSourceTransport", Challenges, false),
        Choice("playerHealthPoints", Challenges, "15", ["5", "10", "15", "20", "25", "30", "35"]),
        Choice("playerHealthRegenSpeed", Challenges, "1", ["2", "1.5", "1.25", "1", "0.75", "0.5", "0.25"]),
        Choice("playerHungerSpeed", Challenges, "1", ["2", "1.5", "1.25", "1", "0.75", "0.5", "0.25"]),
        Choice("lungCapacity", Challenges, "40000", ["10000", "20000", "30000", "40000", "60000", "120000", "3600000"]),
        Choice("bodyTemperatureResistance", Challenges, "0", ["-40", "-30", "-25", "-20", "-15", "-10", "-5", "0", "5", "10", "15", "20"]),
        Choice("playerMoveSpeed", Challenges, "1.5", ["2", "1.75", "1.5", "1.25", "1", "0.75"]),
        Choice("creatureHostility", Challenges, "aggressive", ["aggressive", "passive", "off"]),
        Choice("creatureStrength", Challenges, "1", ["4", "2", "1.5", "1", "0.5", "0.25"]),
        Choice("creatureSwimSpeed", Challenges, "2", ["0.5", "0.75", "1", "1.25", "1.5", "1.75", "2", "3"]),
        Choice("foodSpoilSpeed", Challenges, "1", ["4", "3", "2", "1.5", "1.25", "1", "0.75", "0.5", "0.25"]),
        Choice("saplingGrowthRate", Challenges, "1", ["16", "8", "4", "2", "1.5", "1", "0.75", "0.5", "0.25"]),
        Choice("toolDurability", Challenges, "1", ["4", "3", "2", "1.5", "1.25", "1", "0.75", "0.5"]),
        Choice("toolMiningSpeed", Challenges, "1", ["3", "2", "1.5", "1.25", "1", "0.75", "0.5", "0.25"]),
        Choice("propickNodeSearchRadius", Challenges, "0", ["0", "2", "4", "6", "8"]),
        Choice("microblockChiseling", Challenges, "stonewood", ["off", "stonewood", "all"]),
        Bool("allowCoordinateHud", Challenges, true),
        Bool("allowMap", Challenges, true),
        Bool("colorAccurateWorldmap", Challenges, false),
        Bool("loreContent", Challenges, true),
        Choice("clutterObtainable", Challenges, "ifrepaired", ["ifrepaired", "yes", "no"]),

        // ---- темпоральная стабильность
        Bool("temporalStability", Temporal, true),
        Choice("temporalStorms", Temporal, "sometimes", ["off", "veryrare", "rare", "sometimes", "often", "veryoften"]),
        Choice("tempstormDurationMul", Temporal, "1", ["2", "1.5", "1.25", "1", "0.75", "0.5", "0.25"]),
        Choice("temporalRifts", Temporal, "visible", ["off", "invisible", "visible"]),
        Choice("temporalGearRespawnUses", Temporal, "1", ["-1", "20", "10", "5", "4", "3", "2", "1"]),
        Choice("temporalStormSleeping", Temporal, "1", ["0", "1"]),

        // ---- генерация мира
        Choice("worldClimate", WorldGen, "realistic", ["realistic", "patchy"], once: true),
        Choice("landcover", WorldGen, "0.975", ["0", "0.1", "0.2", "0.3", "0.4", "0.5", "0.6", "0.7", "0.8", "0.9", "0.95", "0.975", "1"], once: true),
        Choice("oceanscale", WorldGen, "5", ["0.1", "0.25", "0.5", "0.75", "1", "1.25", "1.5", "1.75", "2", "3", "4", "5"], once: true),
        Choice("upheavelCommonness", WorldGen, "0.3", ["0", "0.1", "0.2", "0.3", "0.4", "0.5", "0.6", "0.7", "0.8", "0.9", "1"], once: true),
        Choice("geologicActivity", WorldGen, "0.05", ["0", "0.05", "0.1", "0.2", "0.4"], once: true),
        Choice("landformScale", WorldGen, "1.0", ["0.2", "0.4", "0.6", "0.8", "1.0", "1.2", "1.4", "1.6", "1.8", "2", "3"], once: true),
        Choice("worldWidth", WorldGen, "1024000", ["8192000", "4096000", "2048000", "1024000", "600000", "512000", "384000", "256000", "102400",
            "51200", "25600", "10240", "5120", "1024", "512", "384", "256", "128", "64", "32"], once: true),
        Choice("worldLength", WorldGen, "1024000", ["8192000", "4096000", "2048000", "1024000", "600000", "512000", "384000", "256000", "102400",
            "51200", "25600", "10240", "5120", "1024", "512", "384", "256", "128", "64", "32"], once: true),
        Choice("worldEdge", WorldGen, "traversable", ["blocked", "traversable"]),
        Choice("polarEquatorDistance", WorldGen, "50000", ["800000", "400000", "200000", "100000", "50000", "25000", "15000", "10000", "5000"], once: true),
        Choice("storyStructuresDistScaling", WorldGen, "1", ["0.15", "0.25", "0.5", "0.75", "1", "1.5", "2", "3"], once: true),
        Choice("globalTemperature", WorldGen, "1", ["4", "2", "1.5", "1", "0.75", "0.5", "0.25"], once: true),
        Choice("globalPrecipitation", WorldGen, "1", ["4", "2", "1.5", "1", "0.5", "0.25", "0.1"], once: true),
        Choice("globalForestation", WorldGen, "0", ["1", "0.9", "0.75", "0.5", "0.25", "0", "-0.25", "-0.5", "-0.75", "-0.9", "-1"], once: true),
        Choice("globalDepositSpawnRate", WorldGen, "1", ["3", "2", "1.8", "1.6", "1.4", "1.2", "1", "0.8", "0.6", "0.4", "0.2"]),
        Choice("surfaceCopperDeposits", WorldGen, "0.12", ["1", "0.5", "0.2", "0.12", "0.05", "0.015", "0"]),
        Choice("surfaceTinDeposits", WorldGen, "0.007", ["0.5", "0.25", "0.12", "0.03", "0.014", "0.007", "0"]),
        Choice("snowAccum", WorldGen, "true", ["true", "false"]),

        // ---- сетевая игра
        Bool("allowLandClaiming", Multiplayer, true),
        Bool("classExclusiveRecipes", Multiplayer, true),
        Bool("auctionHouse", Multiplayer, true),
    ];

    /// <summary>Что задаёт каждый стиль игры (WorldConfig.PlayStyle) — поверх значений по умолчанию.</summary>
    private static Dictionary<string, IReadOnlyDictionary<string, string>> CreatePresets() =>
        new(StringComparer.Ordinal)
        {
            ["surviveandbuild"] = Preset(("worldClimate", "realistic"), ("gameMode", "survival"), ("temporalStability", "true"),
                ("temporalStorms", "sometimes"), ("graceTimer", "0"), ("microblockChiseling", "stonewood"), ("polarEquatorDistance", "100000"),
                ("lungCapacity", "40000"), ("harshWinters", "true"), ("daysPerMonth", "9"), ("saplingGrowthRate", "1"),
                ("propickNodeSearchRadius", "6"), ("allowUndergroundFarming", "false"), ("allowFallingBlocks", "true"), ("allowFireSpread", "true"),
                ("temporalGearRespawnUses", "20"), ("temporalStormSleeping", "0"), ("clutterObtainable", "ifrepaired")),
            ["exploration"] = Preset(("worldClimate", "realistic"), ("gameMode", "survival"), ("microblockChiseling", "all"),
                ("deathPunishment", "keep"), ("graceTimer", "5"), ("creatureHostility", "passive"), ("playerHealthPoints", "20"),
                ("playerHungerSpeed", "0.5"), ("playerHealthRegenSpeed", "1"), ("foodSpoilSpeed", "0.5"), ("lungCapacity", "120000"),
                ("toolDurability", "2"), ("saplingGrowthRate", "0.5"), ("playerMoveSpeed", "1.25"), ("temporalStability", "false"),
                ("temporalStorms", "off"), ("surfaceCopperDeposits", "0.2"), ("surfaceTinDeposits", "0.03"), ("globalDepositSpawnRate", "1.6"),
                ("propickNodeSearchRadius", "8"), ("polarEquatorDistance", "50000"), ("harshWinters", "false"), ("allowUndergroundFarming", "true"),
                ("allowFallingBlocks", "true"), ("allowFireSpread", "true"), ("temporalGearRespawnUses", "-1"), ("temporalStormSleeping", "1"),
                ("classExclusiveRecipes", "false"), ("clutterObtainable", "yes")),
            ["wildernesssurvival"] = Preset(("worldClimate", "realistic"), ("gameMode", "survival"), ("microblockChiseling", "off"),
                ("deathPunishment", "drop"), ("bodyTemperatureResistance", "10"), ("blockGravity", "sandgravelsoil"), ("caveIns", "on"),
                ("allowFallingBlocks", "true"), ("allowFireSpread", "true"), ("creatureHostility", "aggressive"), ("playerHealthPoints", "10"),
                ("creatureStrength", "1.5"), ("playerHungerSpeed", "1.25"), ("playerHealthRegenSpeed", "1"), ("lungCapacity", "20000"),
                ("foodSpoilSpeed", "1.25"), ("graceTimer", "0"), ("allowCoordinateHud", "false"), ("allowMap", "false"),
                ("allowLandClaiming", "false"), ("surfaceCopperDeposits", "0.05"), ("surfaceTinDeposits", "0"), ("saplingGrowthRate", "2"),
                ("temporalStability", "true"), ("temporalStorms", "often"), ("polarEquatorDistance", "100000"), ("harshWinters", "true"),
                ("daysPerMonth", "9"), ("spawnRadius", "5000"), ("allowUndergroundFarming", "false"), ("noLiquidSourceTransport", "true"),
                ("temporalGearRespawnUses", "3"), ("temporalStormSleeping", "0"), ("clutterObtainable", "ifrepaired"), ("lightningFires", "true")),
            ["homosapiens"] = Preset(("worldClimate", "realistic"), ("gameMode", "survival"), ("deathPunishment", "drop"),
                ("bodyTemperatureResistance", "5"), ("blockGravity", "sandgravelsoil"), ("allowFallingBlocks", "true"), ("allowFireSpread", "true"),
                ("creatureHostility", "aggressive"), ("playerHealthPoints", "10"), ("creatureStrength", "1.5"), ("playerHungerSpeed", "1"),
                ("playerHealthRegenSpeed", "1"), ("lungCapacity", "30000"), ("foodSpoilSpeed", "1.25"), ("graceTimer", "0"),
                ("allowCoordinateHud", "false"), ("allowMap", "false"), ("allowLandClaiming", "false"), ("surfaceCopperDeposits", "0.05"),
                ("surfaceTinDeposits", "0"), ("saplingGrowthRate", "2"), ("temporalStorms", "off"), ("polarEquatorDistance", "200000"),
                ("harshWinters", "true"), ("daysPerMonth", "9"), ("allowUndergroundFarming", "false"), ("noLiquidSourceTransport", "true"),
                ("spawnRadius", "5000"), ("temporalGearRespawnUses", "0"), ("temporalStormSleeping", "0"), ("temporalRifts", "off"),
                ("temporalStability", "false"), ("loreContent", "false"), ("clutterObtainable", "no")),
            ["creativebuilding"] = Preset(("worldClimate", "superflat"), ("gameMode", "creative"), ("hoursPerDay", "2400"), ("cloudypos", "0.5"),
                ("temporalStability", "false"), ("temporalStorms", "off"), ("snowAccum", "false"), ("colorAccurateWorldmap", "true"),
                ("temporalRifts", "off"), ("loreContent", "false")),
        };
}
