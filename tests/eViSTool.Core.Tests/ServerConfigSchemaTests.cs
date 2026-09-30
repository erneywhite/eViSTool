using eViSTool.Core.Server.Config;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

public sealed class ServerConfigSchemaTests : IDisposable
{
    private readonly ConfigSamples _samples = new();

    public void Dispose() => _samples.Dispose();

    private static ConfigFieldSpec Field(ServerConfigDocument doc, string path) =>
        ServerConfigSchema.FieldsFor(doc).Single(f => f.Path == path);

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
        Assert.All(known.Where(f => f.Kind == ConfigValueKind.Choice), f => Assert.NotEmpty(f.Choices));

        var port = known.Single(f => f.Path == "Port");
        Assert.Equal((ConfigSections.General, "network", ConfigValueKind.Integer), (port.Section, port.Group, port.Kind));
        Assert.Equal((1m, 65535m), (port.Min!.Value, port.Max!.Value));

        var worldName = known.Single(f => f.Path == "WorldConfig.WorldName");
        Assert.Equal("WorldName", worldName.Name);
        Assert.Equal("cfg.WorldConfig.WorldName", worldName.LabelKey);
        Assert.Equal("cfg.WorldConfig.WorldName.hint", worldName.HintKey);
        Assert.Equal("cfg.group.world", worldName.GroupKey);
        Assert.Equal(ConfigSections.World, worldName.Section);

        var whitelist = known.Single(f => f.Path == "WhitelistMode");
        Assert.True(whitelist.NumericChoice);
        Assert.Equal(["0", "1", "2"], whitelist.Choices.Select(c => c.Value));
        Assert.Equal("cfg.WhitelistMode.choice.1", whitelist.Choices[1].LabelKey);
        Assert.Equal("cfg.WorldConfig.PlayStyle.choice.surviveandbuild",
            known.Single(f => f.Path == "WorldConfig.PlayStyle").Choices[0].LabelKey);
    }

    [Theory]
    [InlineData(ConfigSamples.Real)]
    [InlineData(ConfigSamples.Default)]
    public void FieldsFor_EveryFieldLandsInExactlyOnePlace(string sample)
    {
        var doc = _samples.Open(sample);
        var leaves = Leaves(doc);
        var paths = ServerConfigSchema.FieldsFor(doc).Select(f => f.Path).ToList();

        Assert.Equal(paths.Count, paths.Distinct().Count());
        Assert.DoesNotContain(paths, p => ServerConfigSchema.Hidden.Contains(p));
        Assert.DoesNotContain(ServerConfigSchema.WorldSettingsPath, paths);

        // поля редактора + скрытые + настройки мира = всё, что есть в файле
        var covered = paths.Concat(ServerConfigSchema.Hidden).Append(ServerConfigSchema.WorldSettingsPath).ToHashSet();
        Assert.Empty(leaves.Except(covered));
        Assert.Empty(paths.Except(leaves));
        Assert.Equal(leaves.Count - 4 - 1, paths.Count); // FileEditWarning, Roles, DefaultRoleCode, LastLaunchMods и WorldConfiguration
    }

    [Theory]
    [InlineData(ConfigSamples.Real)]
    [InlineData(ConfigSamples.Default)]
    public void FieldsFor_KnownFirstInSchemaOrder_ThenTheRestInFileOrder(string sample)
    {
        var doc = _samples.Open(sample);
        var fields = ServerConfigSchema.FieldsFor(doc);
        var known = ServerConfigSchema.Known.Select(f => f.Path).ToList();

        // в образцах есть все поля схемы
        Assert.Equal(known, fields.Take(known.Count).Select(f => f.Path));

        var rest = fields.Skip(known.Count).ToList();
        Assert.Equal(Leaves(doc).Where(p => rest.Any(f => f.Path == p)), rest.Select(f => f.Path));
        Assert.All(rest, f =>
        {
            Assert.Equal(ConfigSections.Advanced, f.Section);
            Assert.Equal("other", f.Group);
            Assert.False(f.Known);
            Assert.NotEqual(ConfigValueKind.ReadOnly, f.Kind);
        });
        Assert.Contains(rest, f => f.Path == "WorldConfig.RepairMode");
        Assert.Contains(rest, f => f.Path == "RepairMode");
    }

    [Fact]
    public void FieldsFor_TypesUnknownFieldsByJson()
    {
        var doc = _samples.Open();
        doc.Set("ModList", new JArray());
        doc.Set("ModMixed", new JArray(1, "a"));
        doc.Set("ModObject", new JObject { ["a"] = 1 });
        doc.Set("WorldConfig.ModText", "x");
        var kinds = ServerConfigSchema.FieldsFor(doc).ToDictionary(f => f.Path, f => f.Kind);

        Assert.Equal(ConfigValueKind.Bool, kinds["OnlyWhitelisted"]);
        Assert.Equal(ConfigValueKind.Integer, kinds["AntiAbuse"]);
        Assert.Equal(ConfigValueKind.Decimal, kinds["TickTime"]);
        Assert.Equal(ConfigValueKind.Decimal, kinds["AntiAbuseBlockBurstAbuseBanDays"]);
        Assert.Equal(ConfigValueKind.Text, kinds["ModDbUrl"]);
        Assert.Equal(ConfigValueKind.StringList, kinds["ModPaths"]);
        Assert.Equal(ConfigValueKind.Json, kinds["MasterserverUrl"]); // null
        Assert.Equal(ConfigValueKind.Json, kinds["WorldConfig.DisabledMods"]); // null
        Assert.Equal(ConfigValueKind.Bool, kinds["WorldConfig.RepairMode"]);
        Assert.Equal(ConfigValueKind.StringList, kinds["ModList"]);
        Assert.Equal(ConfigValueKind.Json, kinds["ModMixed"]);
        Assert.Equal(ConfigValueKind.Json, kinds["ModObject"]);
        Assert.Equal(ConfigValueKind.Text, kinds["WorldConfig.ModText"]);

        // в свежем конфиге то же поле — строка
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

        var style = Field(doc, "WorldConfig.PlayStyle");
        Assert.Equal(new ConfigChoice("modstyle", "modstyle"), style.Choices[^1]);
        Assert.Equal(6, style.Choices.Count);

        var whitelist = Field(doc, "WhitelistMode");
        Assert.Equal(new ConfigChoice("7", "7"), whitelist.Choices[^1]);
        Assert.True(whitelist.NumericChoice);

        // сама схема не изменилась, и знакомое значение ничего не добавляет
        Assert.Equal(5, ServerConfigSchema.Known.Single(f => f.Path == "WorldConfig.PlayStyle").Choices.Count);
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

        // null там, где схема его не ждёт
        fresh.Set("ServerLanguage", null);
        fresh.Set("Port", null);
        fresh.Set("Upnp", null);
        fresh.Set("WorldConfig.WorldType", null);
        Assert.True(Field(fresh, "ServerLanguage").Nullable);
        Assert.Equal((ConfigValueKind.Integer, true), (Field(fresh, "Port").Kind, Field(fresh, "Port").Nullable));
        Assert.Equal(ConfigValueKind.Json, Field(fresh, "Upnp").Kind);
        var worldType = Field(fresh, "WorldConfig.WorldType");
        Assert.True(worldType.Nullable);
        Assert.Equal(["", "standard", "superflat"], worldType.Choices.Select(c => c.Value));
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

        var port = Field(doc, "Port");
        Assert.Equal((ConfigValueKind.Text, ConfigSections.General, "network", true), (port.Kind, port.Section, port.Group, port.Known));
        Assert.Null(port.Min);
        Assert.Equal(ConfigValueKind.Integer, Field(doc, "Upnp").Kind);
        Assert.Equal(ConfigValueKind.Text, Field(doc, "WhitelistMode").Kind);
        Assert.Empty(Field(doc, "WhitelistMode").Choices);
        Assert.Equal(ConfigValueKind.StringList, Field(doc, "ServerName").Kind);
        Assert.False(Field(doc, "ServerName").Required);

        // число больше int уже лежит в файле — поле его принимает
        var big = Field(doc, "BigNumber");
        Assert.True(ConfigValueCodec.TryParse("5000000000", big, out var token, out _, out _));
        Assert.Equal(5_000_000_000, (long)token!);
    }
}
