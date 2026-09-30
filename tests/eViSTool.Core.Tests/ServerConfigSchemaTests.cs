using eViSTool.Core.Server.Config;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

public sealed class ServerConfigSchemaTests : IDisposable
{
    private readonly ConfigSamples _samples = new();

    public void Dispose() => _samples.Dispose();

    private static ConfigFieldSpec Field(ServerConfigDocument doc, string path) =>
        ServerConfigSchema.FieldsFor(doc).Single(f => f.Path == path);

    private static ConfigFieldSpec Known(string path) => ServerConfigSchema.Known.Single(f => f.Path == path);

    /// <summary>Все «листья» редактора: поля верхнего уровня (кроме самого WorldConfig) и поля WorldConfig.</summary>
    private static List<string> Leaves(ServerConfigDocument doc) =>
        doc.Root.Properties()
            .SelectMany(p => p.Name == "WorldConfig"
                ? ((JObject)p.Value).Properties().Select(w => "WorldConfig." + w.Name)
                : [p.Name])
            .ToList();

    [Fact]
    public void Schema_ListsAreConsistent()
    {
        Assert.Equal(30, ServerConfigSchema.KnownPrivileges.Count);
        Assert.Equal(30, ServerConfigSchema.KnownPrivileges.Distinct().Count());
        Assert.Equal(9, ServerConfigSchema.StandardRoleCodes.Count);
        Assert.True(ServerConfigSchema.Hidden.SetEquals(["FileEditWarning", "Roles", "DefaultRoleCode", "WorldConfig", "LastLaunchMods"]));

        var known = ServerConfigSchema.Known;
        Assert.Equal(known.Count, known.Select(f => f.Path).Distinct().Count());
        Assert.All(known, f => Assert.True(f.Known));
        Assert.All(known, f => Assert.DoesNotContain(f.Path, ServerConfigSchema.Hidden));
        Assert.DoesNotContain(known, f => f.Path == ServerConfigSchema.WorldSettingsPath);
        Assert.All(known.Where(f => f.Kind == ConfigValueKind.Choice), f => Assert.NotEmpty(f.Choices));
        Assert.All(known.Where(f => f.Kind != ConfigValueKind.Choice), f => Assert.Empty(f.Choices));
        Assert.All(known.Where(f => f.Min is not null && f.Max is not null), f => Assert.True(f.Min <= f.Max, f.Path));
        // границы — только у чисел, «обязательное» — только у текста
        Assert.All(known.Where(f => f.Min is not null || f.Max is not null),
            f => Assert.Contains(f.Kind, new[] { ConfigValueKind.Integer, ConfigValueKind.Decimal }));
        Assert.All(known.Where(f => f.Required), f => Assert.Contains(f.Kind, new[] { ConfigValueKind.Text, ConfigValueKind.Path }));
        // «пусто — только null» и «null нельзя» вместе не бывают
        Assert.DoesNotContain(known, f => f.EmptyIsNull && f.NeverNull);
        Assert.DoesNotContain(known, f => f.NeverNull && f.Nullable);

        var port = Known("Port");
        Assert.Equal((ConfigSections.General, "network", ConfigValueKind.Integer), (port.Section, port.Group, port.Kind));
        Assert.Equal((1m, 65535m), (port.Min!.Value, port.Max!.Value));

        var worldName = Known("WorldConfig.WorldName");
        Assert.Equal("WorldName", worldName.Name);
        Assert.Equal("cfg.WorldConfig.WorldName", worldName.LabelKey);
        Assert.Equal("cfg.WorldConfig.WorldName.hint", worldName.HintKey);
        Assert.Equal("cfg.group.world", worldName.GroupKey);
        Assert.Equal(ConfigSections.World, worldName.Section);

        var whitelist = Known("WhitelistMode");
        Assert.True(whitelist.NumericChoice);
        Assert.Equal(["0", "1", "2"], whitelist.Choices.Select(c => c.Value));
        Assert.Equal("cfg.WhitelistMode.choice.1", whitelist.Choices[1].LabelKey);
        Assert.Equal("cfg.WorldConfig.PlayStyle.choice.surviveandbuild", Known("WorldConfig.PlayStyle").Choices[0].LabelKey);
    }

    [Fact]
    public void Schema_Layout_SectionsAndGroups()
    {
        var known = ServerConfigSchema.Known;

        // 89 полей игры: 5 скрытых и словарь настроек мира — не поля редактора
        Assert.Equal(83, known.Count);
        Assert.Equal(19, known.Count(f => f.Section == ConfigSections.General));
        Assert.Equal(11, known.Count(f => f.Section == ConfigSections.World));
        Assert.Equal(53, known.Count(f => f.Section == ConfigSections.Advanced));

        Assert.Equal(["identity", "network", "players", "gameplay", "startup"], ServerConfigSchema.GroupsOf(ConfigSections.General));
        Assert.Equal(["world", "map"], ServerConfigSchema.GroupsOf(ConfigSections.World));
        Assert.Equal(
            ["network", "performance", "mods", "antiabuse", "safety", "logging", "hosting", "legacy", "service", "other"],
            ServerConfigSchema.GroupsOf(ConfigSections.Advanced));
        Assert.Equal(ServerConfigSchema.OtherGroup, known[^1].Group); // за известными «прочими» встают поля вне схемы
        Assert.Empty(ServerConfigSchema.GroupsOf("nosuchsection"));

        // поля одной группы идут подряд
        var runs = new List<(string Section, string Group)>();
        foreach (var f in known)
            if (runs.Count == 0 || runs[^1] != (f.Section, f.Group))
                runs.Add((f.Section, f.Group));
        Assert.Equal(runs.Count, runs.Distinct().Count());

        Assert.Equal(["AllowPvP", "PassTimeWhenEmpty"], known.Where(f => f.Group == "gameplay").Select(f => f.Path));
        Assert.Equal(
            ["AllowFireSpread", "AllowFallingBlocks", "OnlyWhitelisted", "DefaultSpawn"],
            known.Where(f => f.Group == "legacy").Select(f => f.Path));
        Assert.All(known.Where(f => f.Group == "legacy"), f => Assert.True(f.Legacy));
        // и ещё два поля сервер, похоже, не читает — они на своих местах, но с пометкой
        Assert.Equal(
            ["GroupChatHistorySize", "WorldConfig.AllowCreativeMode"],
            known.Where(f => f.Legacy && f.Group != "legacy").Select(f => f.Path).Order());
    }

    [Fact]
    public void Schema_ReadOnly_WorldCreationOnly_AndEmptinessRules()
    {
        var known = ServerConfigSchema.Known;

        // сервер ведёт сам — только показать
        var readOnly = known.Where(f => f.Kind == ConfigValueKind.ReadOnly).ToList();
        Assert.Equal(
            ["ConfigVersion", "LastLaunchPlaystyle", "NextPlayerGroupUid", "RepairMode", "ServerIdentifier"],
            readOnly.Select(f => f.Path).Order());
        Assert.All(readOnly, f => Assert.Equal((ConfigSections.Advanced, "service"), (f.Section, f.Group)));
        Assert.Equal(ConfigValueKind.Bool, Known("WorldConfig.RepairMode").Kind); // настоящий переключатель — в WorldConfig

        // читается только при создании мира: размеры карты и WorldConfig.*, кроме трёх полей
        string[] everyStart = ["WorldConfig.SaveFileLocation", "WorldConfig.DisabledMods", "WorldConfig.RepairMode"];
        Assert.All(known, f => Assert.Equal(
            f.Path is "MapSizeX" or "MapSizeY" or "MapSizeZ" || (f.Path.StartsWith("WorldConfig.") && !everyStart.Contains(f.Path)),
            f.WorldCreationOnly));
        Assert.Equal(11, known.Count(f => f.WorldCreationOnly));

        // «пусто» — только null
        Assert.Equal(["Ip", "ModIdBlackList", "ModIdWhiteList"], known.Where(f => f.EmptyIsNull).Select(f => f.Path).Order());
        // null роняет сервер
        Assert.Equal(["ModPaths", "WelcomeMessage"], known.Where(f => f.NeverNull).Select(f => f.Path).Order());

        Assert.Equal(
            ["ServerName", "WorldConfig.SaveFileLocation", "WorldConfig.WorldName"],
            known.Where(f => f.Required).Select(f => f.Path).Order());
    }

    [Fact]
    public void Schema_TypesBoundsAndChoices_FollowTheGame()
    {
        Assert.Equal(ConfigValueKind.Decimal, Known("TickTime").Kind);
        Assert.Equal(ConfigValueKind.Decimal, Known("SpawnCapPlayerScaling").Kind);
        Assert.Equal(ConfigValueKind.Decimal, Known("AntiAbuseBlockBurstAbuseBanDays").Kind);
        Assert.Equal(ConfigValueKind.StringList, Known("ModPaths").Kind);
        Assert.Equal(ConfigValueKind.StringList, Known("WorldConfig.DisabledMods").Kind);
        Assert.Equal(ConfigValueKind.Json, Known("DefaultSpawn").Kind);
        Assert.Equal(ConfigValueKind.Text, Known("WorldConfig.PlayStyleLangCode").Kind);
        Assert.Equal(ConfigValueKind.Path, Known("WorldConfig.SaveFileLocation").Kind);
        Assert.Equal(ConfigValueKind.Multiline, Known("StartupCommands").Kind);

        static (decimal? Min, decimal? Max) Bounds(string path) => (Known(path).Min, Known(path).Max);
        Assert.Equal((0, 2047), Bounds("DieBelowDiskSpaceMb"));
        Assert.Equal((0, 16384), Bounds("MapSizeY"));
        Assert.Equal((0, 16384), Bounds("WorldConfig.MapSizeY"));
        Assert.Equal((0, 67108864), Bounds("MapSizeX"));
        Assert.Equal((0, 67108864), Bounds("MapSizeZ"));
        Assert.Equal((0, uint.MaxValue), Bounds("LogFileSplitAfterLine"));
        Assert.Equal((1, null), Bounds("TickTime"));
        Assert.Equal((1, null), Bounds("MaxClients"));
        Assert.Equal((null, null), Bounds("ServerName"));

        var language = Known("ServerLanguage");
        Assert.Equal(ConfigValueKind.Choice, language.Kind);
        Assert.False(language.NumericChoice);
        Assert.Equal(33, language.Choices.Count);
        Assert.Equal(33, language.Choices.Select(c => c.Value).Distinct().Count());
        Assert.Equal("en", language.Choices[0].Value);
        Assert.Contains(language.Choices, c => c is { Value: "ru", LabelKey: "cfg.ServerLanguage.choice.ru" });
        Assert.Contains(language.Choices, c => c.Value == "pt-br");

        var antiAbuse = Known("AntiAbuse");
        Assert.True(antiAbuse.NumericChoice);
        Assert.Equal(["0", "1", "2"], antiAbuse.Choices.Select(c => c.Value));

        Assert.Equal(
            ["surviveandbuild", "exploration", "wildernesssurvival", "homosapiens", "creativebuilding"],
            Known("WorldConfig.PlayStyle").Choices.Select(c => c.Value));
        Assert.Equal(["standard", "superflat"], Known("WorldConfig.WorldType").Choices.Select(c => c.Value));
    }

    [Theory]
    [InlineData(ConfigSamples.Real)]
    [InlineData(ConfigSamples.Default)]
    public void FieldsFor_EveryFieldLandsInExactlyOnePlace_AndIsKnown(string sample)
    {
        var doc = _samples.Open(sample);
        var leaves = Leaves(doc);
        var fields = ServerConfigSchema.FieldsFor(doc);
        var paths = fields.Select(f => f.Path).ToList();

        Assert.Equal(paths.Count, paths.Distinct().Count());
        Assert.DoesNotContain(paths, p => ServerConfigSchema.Hidden.Contains(p));
        Assert.DoesNotContain(ServerConfigSchema.WorldSettingsPath, paths);

        // поля редактора + скрытые + настройки мира = всё, что есть в файле
        var covered = paths.Concat(ServerConfigSchema.Hidden).Append(ServerConfigSchema.WorldSettingsPath).ToHashSet();
        Assert.Empty(leaves.Except(covered));
        Assert.Empty(paths.Except(leaves));
        Assert.Equal(leaves.Count - 4 - 1, paths.Count); // FileEditWarning, Roles, DefaultRoleCode, LastLaunchMods и WorldConfiguration

        // всё, что пишет сама игра, программа знает: у каждого поля своё место, и в «прочее» без подписи не попало ничего
        Assert.All(fields, f => Assert.True(f.Known, f.Path));
        Assert.Equal(ServerConfigSchema.Known.Select(f => f.Path), paths);
        Assert.All(fields, f =>
        {
            var spec = Known(f.Path);
            Assert.Equal((spec.Section, spec.Group, spec.Kind), (f.Section, f.Group, f.Kind));
        });
    }

    [Theory]
    [InlineData(ConfigSamples.Real)]
    [InlineData(ConfigSamples.Default)]
    public void FieldsFor_KnownFirstInSchemaOrder_ThenTheRestInFileOrder(string sample)
    {
        var doc = _samples.Open(sample);
        // поля, которых игра не знает: от мода или будущей версии
        doc.Set("ModSetting", 5);
        doc.Set("WorldConfig.ModFlag", true);
        doc.Set("ModNote", "x");
        doc.Root.AddFirst(new JProperty("ModFirst", 1.5m));

        var fields = ServerConfigSchema.FieldsFor(doc);
        var known = ServerConfigSchema.Known.Select(f => f.Path).ToList();

        // в образцах есть все поля схемы
        Assert.Equal(known, fields.Take(known.Count).Select(f => f.Path));

        var rest = fields.Skip(known.Count).ToList();
        Assert.Equal(["ModFirst", "WorldConfig.ModFlag", "ModSetting", "ModNote"], rest.Select(f => f.Path));
        Assert.Equal(Leaves(doc).Where(p => rest.Any(f => f.Path == p)), rest.Select(f => f.Path));
        Assert.All(rest, f =>
        {
            Assert.Equal(ConfigSections.Advanced, f.Section);
            Assert.Equal("other", f.Group);
            Assert.False(f.Known);
            Assert.NotEqual(ConfigValueKind.ReadOnly, f.Kind);
            Assert.False(f.WorldCreationOnly || f.Legacy || f.Nullable || f.Required);
        });
    }

    [Fact]
    public void FieldsFor_TypesUnknownFieldsByJson()
    {
        var doc = _samples.Open();
        doc.Set("ModBool", false);
        doc.Set("ModInt", 0);
        doc.Set("ModFloat", 33.333332m);
        doc.Set("ModText", "https://mods.example.com/");
        doc.Set("ModNull", null);
        doc.Set("ModStrings", new JArray("Mods", "More"));
        doc.Set("ModList", new JArray());
        doc.Set("ModMixed", new JArray(1, "a"));
        doc.Set("ModObject", new JObject { ["a"] = 1 });
        doc.Set("WorldConfig.ModText", "x");
        doc.Set("WorldConfig.ModNull", null);
        doc.Set("WorldConfig.ModBool", true);
        var kinds = ServerConfigSchema.FieldsFor(doc).Where(f => !f.Known).ToDictionary(f => f.Path, f => f.Kind);

        Assert.Equal(12, kinds.Count);
        Assert.Equal(ConfigValueKind.Bool, kinds["ModBool"]);
        Assert.Equal(ConfigValueKind.Integer, kinds["ModInt"]);
        Assert.Equal(ConfigValueKind.Decimal, kinds["ModFloat"]);
        Assert.Equal(ConfigValueKind.Text, kinds["ModText"]);
        Assert.Equal(ConfigValueKind.Json, kinds["ModNull"]); // null
        Assert.Equal(ConfigValueKind.StringList, kinds["ModStrings"]);
        Assert.Equal(ConfigValueKind.StringList, kinds["ModList"]);
        Assert.Equal(ConfigValueKind.Json, kinds["ModMixed"]);
        Assert.Equal(ConfigValueKind.Json, kinds["ModObject"]);
        Assert.Equal(ConfigValueKind.Text, kinds["WorldConfig.ModText"]);
        Assert.Equal(ConfigValueKind.Json, kinds["WorldConfig.ModNull"]); // null
        Assert.Equal(ConfigValueKind.Bool, kinds["WorldConfig.ModBool"]);

        // а у полей схемы тип свой, что бы ни лежало в файле: null в живом конфиге — всё равно текст и список
        Assert.Equal((ConfigValueKind.Text, true), (Field(doc, "MasterserverUrl").Kind, Field(doc, "MasterserverUrl").Nullable));
        Assert.Equal((ConfigValueKind.StringList, true), (Field(doc, "WorldConfig.DisabledMods").Kind, Field(doc, "WorldConfig.DisabledMods").Nullable));
        Assert.Equal(ConfigValueKind.Json, Field(doc, "DefaultSpawn").Kind);
        Assert.Equal(ConfigValueKind.Text, Field(_samples.Open(ConfigSamples.Default), "MasterserverUrl").Kind);
    }

    [Fact]
    public void FieldsFor_SkipsKnownFieldsMissingFromFile()
    {
        var doc = _samples.Open();
        doc.Root.Remove("Upnp");
        ((JObject)doc.Root["WorldConfig"]!).Remove("Seed");

        var paths = ServerConfigSchema.FieldsFor(doc).Select(f => f.Path).ToList();

        Assert.DoesNotContain("Upnp", paths);
        Assert.DoesNotContain("WorldConfig.Seed", paths);
        Assert.Contains("Port", paths);

        // WorldConfig не объект — полей мира нет, но и ошибки нет
        doc.Set("WorldConfig", null);
        Assert.DoesNotContain(ServerConfigSchema.FieldsFor(doc), f => f.Path.StartsWith("WorldConfig"));
    }

    [Fact]
    public void FieldsFor_KeepsChoiceValueThatIsNotInTheList()
    {
        var doc = _samples.Open();
        doc.Set("WorldConfig.PlayStyle", "modstyle");
        doc.Set("WhitelistMode", 7);
        doc.Set("ServerLanguage", "tlh");

        var style = Field(doc, "WorldConfig.PlayStyle");
        Assert.Equal(new ConfigChoice("modstyle", "modstyle"), style.Choices[^1]);
        Assert.Equal(6, style.Choices.Count);

        var whitelist = Field(doc, "WhitelistMode");
        Assert.Equal(new ConfigChoice("7", "7"), whitelist.Choices[^1]);
        Assert.True(whitelist.NumericChoice);

        // язык, которого нет в списке игры (свой перевод), остаётся выбранным
        var language = Field(doc, "ServerLanguage");
        Assert.Equal(new ConfigChoice("tlh", "tlh"), language.Choices[^1]);
        Assert.Equal(34, language.Choices.Count);
        Assert.True(ConfigValueCodec.TryParse("tlh", language, out var token, out _, out _));
        Assert.Equal("tlh", (string?)token);

        // сама схема не изменилась, и знакомое значение ничего не добавляет
        Assert.Equal(5, Known("WorldConfig.PlayStyle").Choices.Count);
        Assert.Equal(33, Known("ServerLanguage").Choices.Count);
        Assert.Equal(2, Field(doc, "WorldConfig.WorldType").Choices.Count);
    }

    [Fact]
    public void FieldsFor_EmptyInputReturnsTheSameEmptinessTheFileHad()
    {
        // живой конфиг: "Password": "", "Seed": "" — пустой ввод остаётся пустой строкой
        var real = _samples.Open();
        Assert.False(Field(real, "Password").Nullable);
        Assert.False(Field(real, "WorldConfig.Seed").Nullable);
        Assert.True(Field(real, "StartupCommands").Nullable);
        Assert.True(Field(real, "ServerUrl").Nullable); // непустое значение — как в схеме

        // свежий конфиг: там же null — пустой ввод остаётся null
        var fresh = _samples.Open(ConfigSamples.Default);
        Assert.True(Field(fresh, "Password").Nullable);
        Assert.True(Field(fresh, "WorldConfig.Seed").Nullable);
        Assert.True(Field(fresh, "Ip").Nullable);

        // пустой список в поле, где схема ждёт null, — тоже остаётся пустым списком
        fresh.Set("WorldConfig.DisabledMods", new JArray());
        Assert.False(Field(fresh, "WorldConfig.DisabledMods").Nullable);

        // null там, где схема его не ждёт
        fresh.Set("ServerLanguage", null);
        fresh.Set("Port", null);
        fresh.Set("Upnp", null);
        fresh.Set("WorldConfig.WorldType", null);
        fresh.Set("ModDbUrl", null);
        Assert.True(Field(fresh, "ServerLanguage").Nullable);
        Assert.Equal("", Field(fresh, "ServerLanguage").Choices[0].Value);
        Assert.Equal((ConfigValueKind.Integer, true), (Field(fresh, "Port").Kind, Field(fresh, "Port").Nullable));
        Assert.Equal(ConfigValueKind.Json, Field(fresh, "Upnp").Kind);
        Assert.Equal((ConfigValueKind.Text, true), (Field(fresh, "ModDbUrl").Kind, Field(fresh, "ModDbUrl").Nullable));
        var worldType = Field(fresh, "WorldConfig.WorldType");
        Assert.True(worldType.Nullable);
        Assert.Equal(["", "standard", "superflat"], worldType.Choices.Select(c => c.Value));
    }

    [Fact]
    public void FieldsFor_FieldsWithOneEmptiness_DoNotFollowTheFile()
    {
        var doc = _samples.Open();
        doc.Set("Ip", "");
        doc.Set("ModIdBlackList", new JArray());
        doc.Set("WelcomeMessage", null);
        doc.Set("ModPaths", null);

        // "" в адресе сервера: пустой ввод всё равно null — именно null значит «все адреса»
        var ip = Field(doc, "Ip");
        Assert.True(ip is { Nullable: true, EmptyIsNull: true });
        Assert.True(ConfigValueCodec.TryParse("", ip, out var token, out _, out _));
        Assert.Equal(JTokenType.Null, token!.Type);
        Assert.True(ConfigValueCodec.TryParse("", ip, doc.Get("Ip"), out token, out _, out _)); // и нетронутое поле тоже
        Assert.Equal(JTokenType.Null, token!.Type);

        var blackList = Field(doc, "ModIdBlackList");
        Assert.True(blackList is { Nullable: true, EmptyIsNull: true });
        Assert.True(ConfigValueCodec.TryParse("", blackList, doc.Get("ModIdBlackList"), out token, out _, out _));
        Assert.Equal(JTokenType.Null, token!.Type);

        // null в приветствии и в папках модов роняет сервер: пустой ввод — "" и [], поле не становится Nullable
        var welcome = Field(doc, "WelcomeMessage");
        Assert.Equal((ConfigValueKind.Multiline, false, true), (welcome.Kind, welcome.Nullable, welcome.NeverNull));
        Assert.True(ConfigValueCodec.TryParse("", welcome, doc.Get("WelcomeMessage"), out token, out _, out _));
        Assert.Equal("", (string?)token);

        var modPaths = Field(doc, "ModPaths");
        Assert.Equal((ConfigValueKind.StringList, false, true), (modPaths.Kind, modPaths.Nullable, modPaths.NeverNull));
        Assert.True(ConfigValueCodec.TryParse("", modPaths, doc.Get("ModPaths"), out token, out _, out _));
        Assert.Equal(JTokenType.Array, token!.Type);
        Assert.Empty(token);
    }

    [Fact]
    public void FieldsFor_ValueOfUnexpectedType_IsEditedByItsJsonType_InItsOwnPlace()
    {
        var doc = _samples.Open();
        doc.Set("Port", "abc");
        doc.Set("Upnp", 1);
        doc.Set("WhitelistMode", "on");
        doc.Set("ServerName", new JArray("a"));
        doc.Set("BigNumber", 5_000_000_000);
        doc.Set("MapSizeX", "wide");
        doc.Set("AllowFireSpread", "yes");
        doc.Set("Ip", 5);
        doc.Set("TickTime", 30);

        var port = Field(doc, "Port");
        Assert.Equal((ConfigValueKind.Text, ConfigSections.General, "network", true), (port.Kind, port.Section, port.Group, port.Known));
        Assert.Null(port.Min);
        Assert.Equal(ConfigValueKind.Integer, Field(doc, "Upnp").Kind);
        Assert.Equal(ConfigValueKind.Text, Field(doc, "WhitelistMode").Kind);
        Assert.Empty(Field(doc, "WhitelistMode").Choices);
        Assert.Equal(ConfigValueKind.StringList, Field(doc, "ServerName").Kind);
        Assert.False(Field(doc, "ServerName").Required);
        Assert.False(Field(doc, "Ip").EmptyIsNull);

        // пометки «только при создании мира» и «устарело» от типа значения не зависят
        var width = Field(doc, "MapSizeX");
        Assert.Equal((ConfigValueKind.Text, true, (decimal?)null), (width.Kind, width.WorldCreationOnly, width.Max));
        Assert.Equal((ConfigValueKind.Text, true), (Field(doc, "AllowFireSpread").Kind, Field(doc, "AllowFireSpread").Legacy));

        // целое в дробном поле — поле остаётся дробным, со своими границами
        var tick = Field(doc, "TickTime");
        Assert.Equal(ConfigValueKind.Decimal, tick.Kind);
        Assert.Equal(1m, tick.Min);
        Assert.True(ConfigValueCodec.TryParse("30", tick, doc.Get("TickTime"), out var same, out _, out _));
        Assert.Equal(JTokenType.Integer, same!.Type);

        // число больше int уже лежит в файле — поле его принимает
        var big = Field(doc, "BigNumber");
        Assert.True(ConfigValueCodec.TryParse("5000000000", big, out var token, out _, out _));
        Assert.Equal(5_000_000_000, (long)token!);
    }

    [Fact]
    public void FieldsFor_ValueOutOfSchemaBounds_IsRejectedOnInput_ButSurvivesUntouched()
    {
        // в живом конфиге DieBelowDiskSpaceMb = 4096: из-за ошибки игры защита при таком значении не работает
        var doc = _samples.Open();
        var spec = Field(doc, "DieBelowDiskSpaceMb");
        var original = doc.Get("DieBelowDiskSpaceMb")!;
        Assert.Equal(4096, (int)original);
        Assert.Equal(ConfigValueKind.Integer, spec.Kind);
        Assert.Equal(2047m, spec.Max);

        // ввести такое нельзя…
        Assert.False(ConfigValueCodec.TryParse("4096", spec, out _, out var errorKey, out var errorArgs));
        Assert.Equal("cfgerr.range", errorKey);
        Assert.Equal([0m, 2047m], errorArgs);
        Assert.False(ConfigValueCodec.TryParse("2048", spec, original, out _, out errorKey, out _));
        Assert.Equal("cfgerr.range", errorKey);

        // …но пока поле не трогали, оно разбирается без ошибки и сохранение не ломает
        Assert.True(ConfigValueCodec.TryParse("4096", spec, original, out var kept, out errorKey, out _));
        Assert.Null(errorKey);
        doc.Set(spec.Path, kept);
        Assert.False(doc.IsDirty);
        doc.Save();
        Assert.Equal(4096, (int)ServerConfigDocument.Load(doc.FilePath).Get("DieBelowDiskSpaceMb")!);

        // исправить на рабочее значение можно
        Assert.True(ConfigValueCodec.TryParse("2047", spec, original, out var edited, out _, out _));
        Assert.Equal(2047, (int)edited!);
        Assert.True(ConfigValueCodec.TryParse("0", spec, original, out edited, out _, out _));
        Assert.Equal(0, (int)edited!);
    }
}
