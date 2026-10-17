using System.IO.Compression;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Packs;
using eViSTool.Core.Profiles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>Модпак — на сервер у хостера: моды по FTP, включение как в паке, настройки модов — строкой в отчёте.</summary>
public sealed class PackToHostedTests : IDisposable
{
    private readonly HostedFixture _host = new();

    public PackToHostedTests() => _host.GPortalLayout();

    public void Dispose() => _host.Dispose();

    private string Pack()
    {
        var alpha = _host.Zip("alpha_1.1.0.zip", "alpha", "1.1.0");
        var fresh = _host.Zip("fresh_1.0.0.zip", "fresh", "1.0.0");
        var path = Path.Combine(_host.Dir, "test" + PackManifest.Extension);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(alpha, PackManifest.ModsFolder + "alpha_1.1.0.zip");
        zip.CreateEntryFromFile(fresh, PackManifest.ModsFolder + "fresh_1.0.0.zip");
        using (var w = new StreamWriter(zip.CreateEntry(PackManifest.ConfigFolder + "AlphaConfig.json").Open())) w.Write("{ \"Speed\": 2 }");
        var manifest = new PackManifest
        {
            Name = "hosted", IncludesModConfig = true, Kind = ProfileKind.Server,
            Mods =
            [
                new() { ModId = "alpha", Name = "alpha", Version = "1.1.0", FileName = "alpha_1.1.0.zip", Bundled = true, Sha256 = PackBuilder.Sha256Of(alpha) },
                new() { ModId = "fresh", Name = "fresh", Version = "1.0.0", FileName = "fresh_1.0.0.zip", Bundled = true, Enabled = false, Sha256 = PackBuilder.Sha256Of(fresh) },
            ],
        };
        using (var w = new StreamWriter(zip.CreateEntry(PackManifest.FileName).Open())) w.Write(JsonConvert.SerializeObject(manifest));
        return path;
    }

    [Fact]
    public async Task PackGoesToTheServer_EnabledAsInThePack_ModConfigIsReported()
    {
        _host.PutMod("/Mods/alpha_1.0.0.zip", "alpha", "1.0.0");
        _host.PutMod("/Mods/other_2.0.0.zip", "other", "2.0.0");
        var target = _host.Target;
        using var pack = PackFile.Open(Pack());
        var (resolved, locals) = await ModTargets.ScanAsync(target);
        var plan = PackImporter.Plan(pack.Manifest, resolved, locals);
        Assert.Equal(PackItemAction.Update, plan.Items.Single(i => i.Mod.ModId == "alpha").Action);
        using var db = new ModDbClient();
        var importer = new PackImporter(db, new ModUpdater(db, Path.Combine(_host.Dir, "dl-pack")));

        var result = await importer.ApplyToTargetAsync(pack, plan, target, mirror: true, applyConfig: true);

        Assert.Equal(["alpha_1.1.0.zip", "fresh_1.0.0.zip", "other_2.0.0.zip"],
            _host.Server.Names("/Mods").Where(n => n.EndsWith(".zip")).Order(StringComparer.Ordinal));
        var disabled = (JObject.Parse(_host.Server.Text("/serverconfig.json"))["WorldConfig"]!["DisabledMods"] as JArray)!.Select(t => (string)t!).ToList();
        Assert.Contains("fresh", disabled);
        Assert.Contains("other", disabled); // «как в паке»: лишний выключен
        Assert.DoesNotContain("alpha", disabled);
        Assert.Contains(result.Problems, p => HostedTestText.Of("pack.hostedConfigSkipped").Contains(p));
        Assert.Single(ModBackupStore.ForProfile(_host.Profile).List("alpha"));
    }

    [Fact]
    public async Task SameVersionInThePack_IsNotComparedByAFileOnThisComputer()
    {
        _host.PutMod("/Mods/alpha_1.1.0.zip", "alpha", "1.1.0");
        using var pack = PackFile.Open(Pack());
        var (resolved, locals) = await ModTargets.ScanAsync(_host.Target);

        // путь «/Mods/alpha_1.1.0.zip» на этой машине не читается (на Windows это был бы C:\Mods\…)
        var plan = PackImporter.Plan(pack.Manifest, resolved, locals);

        Assert.Equal(PackItemAction.Same, plan.Items.Single(i => i.Mod.ModId == "alpha").Action);
    }

    [Fact]
    public async Task LocalTarget_IsRefusedByApplyToTarget()
    {
        using var db = new ModDbClient();
        var importer = new PackImporter(db, new ModUpdater(db, Path.Combine(_host.Dir, "dl-pack")));
        using var pack = PackFile.Open(Pack());
        var local = new ModTarget(new GameProfile { Kind = ProfileKind.Server, DataDir = _host.Dir }, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            importer.ApplyToTargetAsync(pack, new PackImportPlan(pack.Manifest, [], [], null), local, mirror: false, applyConfig: false));
    }
}
