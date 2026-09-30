using eViSTool.Core.Localization;
using eViSTool.Core.Server.Config;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

public sealed class RoleCatalogTests : IDisposable
{
    private readonly ConfigSamples _samples = new();

    public void Dispose() => _samples.Dispose();

    // ---- цвета

    [Theory]
    [InlineData("White", 255, 255, 255)]
    [InlineData("lightgreen", 0x90, 0xEE, 0x90)]
    [InlineData("  LIGHTBLUE ", 0xAD, 0xD8, 0xE6)]
    [InlineData("#FF8800", 255, 0x88, 0)]
    [InlineData("#ff8800", 255, 0x88, 0)]
    [InlineData("10, 20, 30", 10, 20, 30)]
    [InlineData("10,20,30", 10, 20, 30)]
    [InlineData("255, 10, 20, 30", 10, 20, 30)] // A, R, G, B — прозрачность образцу не нужна
    [InlineData("0, 0, 0", 0, 0, 0)]
    public void TryParseColor_UnderstandsWhatTheServerUnderstands(string text, byte r, byte g, byte b)
    {
        Assert.True(RoleCatalog.TryParseColor(text, out var rgb));
        Assert.Equal(new RoleRgb(r, g, b), rgb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Blurple")]
    [InlineData("Light Green")]
    [InlineData("#FF88")]
    [InlineData("#FF88001")]
    [InlineData("#GG8800")]
    [InlineData("# FF8800")]
    [InlineData("FF8800")]
    [InlineData("10, 20")]
    [InlineData("10, 20, 30, 40, 50")]
    [InlineData("10, 20, 300")]
    [InlineData("10, -20, 30")]
    [InlineData("10, , 30")]
    [InlineData("10; 20; 30")]
    [InlineData("42")]
    public void TryParseColor_RejectsEverythingElse(string? text)
    {
        Assert.False(RoleCatalog.TryParseColor(text, out var rgb));
        Assert.Equal(default, rgb);
    }

    [Fact]
    public void Colors_AllNamesParse_AndAreUnique()
    {
        Assert.Equal(141, RoleCatalog.Colors.Count);
        Assert.Equal(141, RoleCatalog.Colors.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var color in RoleCatalog.Colors)
        {
            Assert.True(RoleCatalog.TryParseColor(color.Name, out var byName), color.Name);
            Assert.Equal(color.Rgb, byName);
            Assert.True(RoleCatalog.TryParseColor(color.Name.ToUpperInvariant(), out var upper), color.Name);
            Assert.Equal(color.Rgb, upper);
            // шестнадцатеричная запись — тот же цвет
            Assert.True(RoleCatalog.TryParseColor(color.Rgb.Hex, out var byHex), color.Name);
            Assert.Equal(color.Rgb, byHex);
            Assert.Equal(color.Name, color.ToString());
        }
    }

    [Fact]
    public void Colors_KnownValues()
    {
        Assert.Equal("#FFFFFF", RoleCatalog.FindColor("white")!.Rgb.Hex);
        Assert.Equal("#ADD8E6", RoleCatalog.FindColor("LightBlue")!.Rgb.Hex);
        Assert.Equal("#663399", RoleCatalog.FindColor(" RebeccaPurple ")!.Rgb.Hex);
        Assert.Equal("LightGreen", RoleCatalog.FindColor("LIGHTGREEN")!.Name);
        Assert.Null(RoleCatalog.FindColor("#FFFFFF"));
        Assert.Null(RoleCatalog.FindColor(null));
    }

    [Fact]
    public void Colors_CoverEveryColorOfTheSampleRoles()
    {
        foreach (var sample in new[] { ConfigSamples.Real, ConfigSamples.Default })
            Assert.All(_samples.Open(sample).Roles, role => Assert.NotNull(RoleCatalog.FindColor(role.Color)));
    }

    // ---- режимы игры и привилегии

    [Fact]
    public void GameModes_AreTheFourOfTheGame()
    {
        Assert.Equal([0, 1, 2, 3], RoleCatalog.GameModes);
        Assert.Equal("gamemode.2", RoleCatalog.GameModeLabelKey(2));
        Assert.All(_samples.Open().Roles, role => Assert.Contains(role.DefaultGameMode, RoleCatalog.GameModes));
    }

    [Fact]
    public void Privileges_KnownThenExtra_NoOverlap()
    {
        Assert.Equal(["ignoremaxclients", "staffentitlement", "denybreakreinforced"], RoleCatalog.ExtraPrivileges);
        Assert.Equal([.. ServerConfigSchema.KnownPrivileges, .. RoleCatalog.ExtraPrivileges], RoleCatalog.AllPrivileges);
        Assert.Equal(RoleCatalog.AllPrivileges.Count, RoleCatalog.AllPrivileges.Distinct().Count());

        Assert.All(RoleCatalog.AllPrivileges, code => Assert.True(RoleCatalog.IsKnownPrivilege(code)));
        Assert.False(RoleCatalog.IsKnownPrivilege("modprivilege"));
        Assert.False(RoleCatalog.IsKnownPrivilege("Build")); // регистр важен

        Assert.True(RoleCatalog.IsRestriction("denybreakreinforced"));
        Assert.DoesNotContain(ServerConfigSchema.KnownPrivileges, RoleCatalog.IsRestriction);
    }

    [Fact]
    public void PrivilegesToShow_KeepsUnknownOnesFromTheFile()
    {
        Assert.Equal(RoleCatalog.AllPrivileges, RoleCatalog.PrivilegesToShow([]));
        Assert.Equal(RoleCatalog.AllPrivileges, RoleCatalog.PrivilegesToShow(["chat", "build", "ignoremaxclients"]));

        var shown = RoleCatalog.PrivilegesToShow(["chat", "modb", "build", "moda", "modb", "", " ", "Build"]);
        Assert.Equal([.. RoleCatalog.AllPrivileges, "modb", "moda", "Build"], shown);
    }

    [Fact]
    public void EveryPrivilegeAndGameMode_HasTextsInAllLanguages()
    {
        // подписи собираются в коде («priv.» + код), общий тест локализации таких ключей не видит
        foreach (var (language, _) in Loc.Available)
        {
            var keys = Loc.Keys(language).ToHashSet();
            var missing = RoleCatalog.AllPrivileges
                .SelectMany(code => new[] { RoleCatalog.PrivilegeLabelKey(code), RoleCatalog.PrivilegeHintKey(code) })
                .Concat(RoleCatalog.GameModes.Select(RoleCatalog.GameModeLabelKey))
                .Where(key => !keys.Contains(key))
                .ToList();
            Assert.True(missing.Count == 0, $"{language}: нет {string.Join(", ", missing)}");
        }

        Assert.Equal("priv.build", RoleCatalog.PrivilegeLabelKey("build"));
        Assert.Equal("priv.build.hint", RoleCatalog.PrivilegeHintKey("build"));
    }

    // ---- код роли

    [Theory]
    [InlineData("vip", RoleCodeProblem.None)]
    [InlineData("VIP_2", RoleCodeProblem.None)]
    [InlineData(null, RoleCodeProblem.Empty)]
    [InlineData("", RoleCodeProblem.Empty)]
    [InlineData("   ", RoleCodeProblem.Empty)]
    [InlineData("v ip", RoleCodeProblem.Whitespace)]
    [InlineData(" vip", RoleCodeProblem.Whitespace)]
    [InlineData("vip\t", RoleCodeProblem.Whitespace)]
    [InlineData("admin", RoleCodeProblem.Taken)]
    [InlineData("ADMIN", RoleCodeProblem.Taken)]
    [InlineData("SuPlayer", RoleCodeProblem.Taken)]
    public void CheckCode_EmptySpacesTaken(string? code, RoleCodeProblem expected) =>
        Assert.Equal(expected, RoleCatalog.CheckCode(code, ServerConfigSchema.StandardRoleCodes));

    // ---- добавленное в RoleEntry

    [Fact]
    public void RuntimePrivileges_AreReadAsInFile_AndNeverTouched()
    {
        var doc = _samples.OpenText("""
            { "Roles": [
              { "Code": "a", "Privileges": ["chat"], "RuntimePrivileges": ["modpriv", 5, "other"] },
              { "Code": "b", "RuntimePrivileges": null },
              { "Code": "c" }
            ] }
            """);
        var roles = doc.Roles;

        Assert.Equal(["modpriv", "other"], roles[0].RuntimePrivileges);
        Assert.Empty(roles[1].RuntimePrivileges);
        Assert.Empty(roles[2].RuntimePrivileges);

        // выдача и отзыв обычных привилегий список сервера не задевают
        roles[0].SetPrivilege("modpriv", true);
        roles[0].SetPrivilege("other", false);
        Assert.Equal(["chat", "modpriv"], roles[0].Privileges);
        Assert.Equal(3, ((JArray)roles[0].Json["RuntimePrivileges"]!).Count);

        Assert.All(_samples.Open().Roles, role => Assert.Empty(role.RuntimePrivileges));
    }

    [Fact]
    public void HasPrivilege_IsCaseSensitive_AndFollowsEdits()
    {
        var role = _samples.Open().Roles[0]; // suvisitor: только chat

        Assert.True(role.HasPrivilege("chat"));
        Assert.False(role.HasPrivilege("Chat"));
        Assert.False(role.HasPrivilege("build"));

        role.SetPrivilege("build", true);
        role.SetPrivilege("chat", false);
        Assert.True(role.HasPrivilege("build"));
        Assert.False(role.HasPrivilege("chat"));
    }
}
