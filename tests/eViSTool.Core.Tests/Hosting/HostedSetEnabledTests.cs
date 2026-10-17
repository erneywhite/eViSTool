using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>Включение и выключение мода в serverconfig.json сервера у хостера: формат файла, гонки, путь через bak.</summary>
public sealed class HostedSetEnabledTests : IDisposable
{
    private readonly HostedFixture _host = new();

    public HostedSetEnabledTests()
    {
        _host.GPortalLayout();
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
    }

    public void Dispose() => _host.Dispose();

    private string ConfigText => _host.Server.Text("/serverconfig.json");

    private string[] DisabledMods() =>
        (JObject.Parse(ConfigText)["WorldConfig"]?["DisabledMods"] as JArray)?.Select(t => (string)t!).ToArray() ?? [];

    private async Task<LocalMod> AlphaAsync() => (await ModTargets.ScanAsync(_host.Target)).Mods.Single(m => m.Info?.ModId == "alpha");

    private void SetDisabled(params string[] mods)
    {
        var root = JObject.Parse(ConfigText);
        root["WorldConfig"]!["DisabledMods"] = new JArray(mods);
        _host.Server.Put("/serverconfig.json", ModConfigEditor.ToText(root, ConfigText));
    }

    [Fact]
    public async Task Disable_CreatesTheListFromNull_AndChangesNothingElse()
    {
        var before = ConfigText;

        await ModTargets.SetEnabledAsync(_host.Target, await AlphaAsync(), enabled: false);

        Assert.Equal(["alpha"], DisabledMods());
        // всё остальное — как было: те же строки, типы («MaxClients": "4»), переводы строк файла
        var expected = before.Replace("\"DisabledMods\": null", "\"DisabledMods\": [\r\n      \"alpha\"\r\n    ]".ReplaceLineEndings(NewLine(before)));
        Assert.Equal(expected, ConfigText);
        // G-Portal не переименовывает поверх — прежний конфиг ушёл в bak, временного файла не осталось
        Assert.Equal(before, _host.Server.Text("/serverconfig.json.evistool.bak"));
        Assert.False(_host.Server.Has("/serverconfig.json.evistool.tmp"));
        Assert.Equal(before, File.ReadAllText(Path.Combine(ModInfoCache.DirFor(_host.Profile.Id), "serverconfig.prev.json")));
    }

    private static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    [Fact]
    public async Task Enable_RemovesBothForms_AndTheModIsShownEnabled()
    {
        SetDisabled("alpha", "alpha@1.0.0", "other");

        await ModTargets.SetEnabledAsync(_host.Target, await AlphaAsync(), enabled: true);

        Assert.Equal(["other"], DisabledMods());
        Assert.False((await AlphaAsync()).IsDisabled);
    }

    [Fact]
    public async Task NothingToChange_DoesNotWrite()
    {
        SetDisabled("alpha");
        var writes = _host.Server.Writes.Count;

        await ModTargets.SetEnabledAsync(_host.Target, await AlphaAsync(), enabled: false);

        Assert.Equal(writes, _host.Server.Writes.Count);
    }

    [Fact]
    public async Task ServerThatRenamesOver_WritesInPlace_WithoutBak()
    {
        _host.Server.NoRenameOverwrite = false;

        await ModTargets.SetEnabledAsync(_host.Target, await AlphaAsync(), enabled: false);

        Assert.Equal(["alpha"], DisabledMods());
        Assert.False(_host.Server.Has("/serverconfig.json.evistool.bak"));
    }

    [Fact]
    public async Task Race_SomeoneEditsTheConfigBetweenOurReads_BothEditsSurvive()
    {
        var alpha = await AlphaAsync();
        var downloads = 0;
        _host.Server.OnBefore = (op, path) =>
        {
            // второе окно выключило «other», пока мы готовили свою правку
            if (op == "download" && path == "/serverconfig.json" && ++downloads == 3) SetDisabled("other");
        };

        await ModTargets.SetEnabledAsync(_host.Target, alpha, enabled: false);

        Assert.Equal(["other", "alpha"], DisabledMods());
    }

    [Fact]
    public async Task ConfigKeepsChanging_GivesUpWithWords()
    {
        var alpha = await AlphaAsync();
        var n = 0;
        _host.Server.OnBefore = (op, path) =>
        {
            if (op == "download" && path == "/serverconfig.json") SetDisabled("other" + ++n);
        };

        var ex = await Assert.ThrowsAsync<IOException>(() => ModTargets.SetEnabledAsync(_host.Target, alpha, enabled: false));

        HostedTestText.AssertIs("hosted.errConfigBusy", ex.Message);
        Assert.DoesNotContain("alpha", DisabledMods());
    }

    [Fact]
    public async Task RenameFailsAfterBak_TheOldConfigIsPutBack()
    {
        var before = ConfigText;
        var alpha = await AlphaAsync();
        _host.Server.FailOn = (op, path) => op == "rename" && path == "/serverconfig.json.evistool.tmp" && _host.Server.Has("/serverconfig.json.evistool.bak");

        await Assert.ThrowsAsync<RemoteFilesException>(() => ModTargets.SetEnabledAsync(_host.Target, alpha, enabled: false));

        Assert.Equal(before, ConfigText);
        Assert.False(_host.Server.Has("/serverconfig.json.evistool.tmp"));
    }

    [Fact]
    public async Task WithoutConfig_TogglingIsRefusedWithWords()
    {
        File.Delete(Path.Combine(_host.Server.Root, "serverconfig.json"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ModTargets.SetEnabledAsync(_host.Target, await AlphaAsync(), enabled: false));

        HostedTestText.AssertIs("hosted.noConfig", ex.Message);
    }

    [Fact]
    public async Task TheHostersMod_CannotBeDisabled()
    {
        var mods = (await ModTargets.ScanAsync(_host.Target)).Mods;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ModTargets.SetEnabledAsync(_host.Target, mods.Single(m => m.Path.EndsWith(".cs")), enabled: false));
    }

    // ---------- запуски сервера и «хостер переписал конфиг»

    private void ServerStarted(string lastLaunch, params string[]? disabled)
    {
        var root = JObject.Parse(ConfigText);
        root["LastLaunchMods"] = lastLaunch;
        if (disabled is not null) root["WorldConfig"]!["DisabledMods"] = disabled.Length == 0 ? JValue.CreateNull() : new JArray(disabled);
        _host.Server.Put("/serverconfig.json", ModConfigEditor.ToText(root, ConfigText));
    }

    [Fact]
    public async Task HostRewroteTheConfigOnStart_IsReported_Once()
    {
        await ModTargets.SetEnabledAsync(_host.Target, await AlphaAsync(), enabled: false);
        ServerStarted("""[{"id":"game","version":"1.22.7"},{"id":"alpha","version":"1.0.0"}]""", []); // DisabledMods снова null

        var (profile, _) = await ModTargets.ScanAsync(_host.Target);
        Assert.Contains(profile.Warnings, w => HostedTestText.Of("hosted.configReverted").Contains(w));

        var (again, _) = await ModTargets.ScanAsync(_host.Target);
        Assert.DoesNotContain(again.Warnings, w => HostedTestText.Of("hosted.configReverted").Contains(w));
    }

    [Fact]
    public async Task HostKeptTheDisabledMod_NoWarning_AndTheLaunchSplitsHistory()
    {
        await ModTargets.ScanAsync(_host.Target); // запомнили LastLaunchMods
        await ModTargets.InstallAsync(_host.Target, _host.Zip("beta_1.0.0.zip", "beta", "1.0.0"));
        await ModTargets.SetEnabledAsync(_host.Target, await AlphaAsync(), enabled: false);
        ServerStarted("""[{"id":"game","version":"1.22.7"},{"id":"beta","version":"1.0.0"}]""", null);

        var (profile, _) = await ModTargets.ScanAsync(_host.Target);

        Assert.DoesNotContain(profile.Warnings, w => HostedTestText.Of("hosted.configReverted").Contains(w));
        var entries = ModHistory.ReadEntries(ModHistory.FileFor(_host.Profile.Id));
        Assert.True(entries[^1].IsLaunch);
        Assert.Equal("beta", entries[0].ModId);
    }

    [Fact]
    public async Task HostPutBackTheOldConfig_TheSameModsLoaded_IsStillReported()
    {
        // alpha грузился при прошлом запуске; хостер при старте вернул конфиг, сервер загрузил тот же набор — и
        // LastLaunchMods не изменился (сервер пишет его, только когда набор другой)
        const string launch = """[{"id":"game","version":"1.22.7"},{"id":"alpha","version":"1.0.0"}]""";
        ServerStarted(launch, null);
        await ModTargets.ScanAsync(_host.Target);
        await ModTargets.SetEnabledAsync(_host.Target, await AlphaAsync(), enabled: false);

        var (untouched, _) = await ModTargets.ScanAsync(_host.Target);
        Assert.DoesNotContain(untouched.Warnings, w => HostedTestText.Of("hosted.configReverted").Contains(w));

        ServerStarted(launch, []); // DisabledMods снова null, LastLaunchMods тот же
        var (profile, _) = await ModTargets.ScanAsync(_host.Target);
        Assert.Contains(profile.Warnings, w => HostedTestText.Of("hosted.configReverted").Contains(w));
    }

    [Fact]
    public async Task RenameOverRefusedAsDenied_StillGoesThroughBak()
    {
        // адаптер перевёл «550 path already exists» как Denied, а не Exists
        var alpha = await AlphaAsync();
        _host.Server.FailOn = (op, path) => op == "rename" && path == "/serverconfig.json.evistool.tmp" && _host.Server.Has("/serverconfig.json");

        await ModTargets.SetEnabledAsync(_host.Target, alpha, enabled: false);

        Assert.Equal(["alpha"], DisabledMods());
        Assert.False(_host.Server.Has("/serverconfig.json.evistool.tmp"));
    }

    [Fact]
    public async Task RenameRefusedForReal_TheConfigStaysAsItWas()
    {
        var before = ConfigText;
        var alpha = await AlphaAsync();
        // переименовывать не даёт ничего: попытка через bak тоже срывается, конфиг остаётся как был
        _host.Server.FailOn = (op, _) => op == "rename";

        await Assert.ThrowsAsync<RemoteFilesException>(() => ModTargets.SetEnabledAsync(_host.Target, alpha, enabled: false));

        Assert.Equal(before, ConfigText);
        Assert.False(_host.Server.Has("/serverconfig.json.evistool.bak"));
        Assert.False(_host.Server.Has("/serverconfig.json.evistool.tmp"));
    }

    [Fact]
    public async Task BothRenamesFail_TheNextScanPutsTheConfigBack()
    {
        var before = ConfigText;
        var alpha = await AlphaAsync();
        _host.Server.FailOn = (op, path) => op == "rename"
            && (path == "/serverconfig.json.evistool.tmp" && _host.Server.Has("/serverconfig.json.evistool.bak") || path == "/serverconfig.json.evistool.bak");

        await Assert.ThrowsAsync<RemoteFilesException>(() => ModTargets.SetEnabledAsync(_host.Target, alpha, enabled: false));
        Assert.False(_host.Server.Has("/serverconfig.json")); // остался только bak
        _host.Server.FailOn = null;

        var (profile, _) = await ModTargets.ScanAsync(_host.Target);

        Assert.Equal(before, ConfigText);
        Assert.False(_host.Server.Has("/serverconfig.json.evistool.bak"));
        Assert.Contains(profile.Warnings, w => HostedTestText.Of("hosted.configRestored").Contains(w));
        Assert.Null(HostedJournal.ConfigSwap(_host.Profile.Id));
        Assert.DoesNotContain(profile.Warnings, w => HostedTestText.Of("hosted.noConfig").Contains(w));
    }

    // ---------- ToText

    [Fact]
    public void ToText_RoundTripsTheRealGPortalConfig_ByteForByte()
    {
        foreach (var sample in new[] { "gportal-serverconfig-after-start.json", "gportal-serverconfig-template.json" })
            foreach (var newLine in new[] { "\r\n", "\n" })
            {
                var text = File.ReadAllText(HostedFixture.Sample(sample)).ReplaceLineEndings(newLine);
                Assert.Equal(text, ModConfigEditor.ToText(ModConfigEditor.ParseText(text), text));
            }
    }

    [Fact]
    public void ToText_KeepsTabsAndFourSpaces()
    {
        const string tabs = "{\n\t\"A\": 1,\n\t\"B\": [\n\t\t\"x\"\n\t]\n}";
        Assert.Equal(tabs, ModConfigEditor.ToText(ModConfigEditor.ParseText(tabs), tabs));
        const string four = "{\r\n    \"A\": \"1\",\r\n    \"B\": 2.50\r\n}\r\n";
        Assert.Equal(four, ModConfigEditor.ToText(ModConfigEditor.ParseText(four), four));
    }

    [Fact]
    public void SetEnabledOnTheTree_WorksWithoutWorldConfig()
    {
        var root = JObject.Parse("""{ "WorldConfig": null }""");
        var mod = ModInfo.Parse("""{ "modid": "Alpha", "name": "Alpha", "version": "1.0.0" }""");

        Assert.True(ModConfigEditor.SetEnabled(root, ProfileKind.Server, mod, enabled: false));
        Assert.Equal(["Alpha"], ProfileResolver.ReadServerConfig(root).Disabled);
        Assert.False(ModConfigEditor.SetEnabled(root, ProfileKind.Server, mod, enabled: false));
        Assert.True(ModConfigEditor.SetEnabled(root, ProfileKind.Server, mod, enabled: true));
        Assert.Empty(ProfileResolver.ReadServerConfig(root).Disabled);
        Assert.Empty(ProfileResolver.ReadServerConfig(JObject.Parse("""{ "ModPaths": null, "WorldConfig": null }""")).ModPaths);
    }
}
