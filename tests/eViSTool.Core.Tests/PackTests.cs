using System.IO.Compression;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Packs;
using eViSTool.Core.Profiles;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Tests;

public sealed class PackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-pack-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ResolvedProfile Profile(string name, string disabled = "")
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(data, "Mods"));
        File.WriteAllText(Path.Combine(data, "clientsettings.json"),
            $$"""{ "stringListSettings": { "disabledMods": [{{disabled}}], "modPaths": [] } }""");
        return ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Client, DataDir = data });
    }

    private static void Mod(ResolvedProfile p, string id, string version)
    {
        using var zip = ZipFile.Open(Path.Combine(p.InstallDir!, $"{id}_{version}.zip"), ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
        w.Write($$"""{ "modid": "{{id}}", "name": "{{id}}", "version": "{{version}}" }""");
    }

    private string BuildPack(ResolvedProfile src, bool bundle, bool includeDisabled = false)
    {
        var path = Path.Combine(_root, "test.evpack");
        PackBuilder.Build(src, ModUpdateService.ScanLocal(src),
            new PackExportOptions { Name = "Duo", BundleFiles = bundle, IncludeDisabled = includeDisabled },
            onModDb: new HashSet<string> { "alpha" }, path);
        return path;
    }

    [Fact]
    public void BuildSkipsDisabledAndBundlesModsNotOnModDb()
    {
        var src = Profile("src", "\"beta\"");
        Mod(src, "alpha", "1.0.0");
        Mod(src, "beta", "2.0.0");   // выключен
        Mod(src, "gamma", "3.0.0");  // нет в модбазе
        src = ProfileResolver.Resolve(src.Profile);

        using var pack = PackFile.Open(BuildPack(src, bundle: false));
        var mods = pack.Manifest.Mods.ToDictionary(m => m.ModId);

        Assert.Equal(["alpha", "gamma"], mods.Keys.Order());
        Assert.False(mods["alpha"].Bundled); // есть в модбазе — скачается при импорте
        Assert.True(mods["gamma"].Bundled);  // скачать неоткуда — внутри
        Assert.True(pack.HasFile(mods["gamma"]));
        Assert.Equal(64, mods["alpha"].Sha256.Length);
    }

    [Fact]
    public async Task ImportsBundledPackIntoAnotherProfileAndMirrors()
    {
        var src = Profile("src", "\"beta\"");
        Mod(src, "alpha", "1.0.0");
        Mod(src, "beta", "2.0.0");
        src = ProfileResolver.Resolve(src.Profile);
        var packPath = BuildPack(src, bundle: true, includeDisabled: true);

        var dst = Profile("dst");
        Mod(dst, "alpha", "0.9.0"); // старее — обновится
        Mod(dst, "extra", "1.0.0"); // нет в паке — при «как в паке» выключится
        dst = ProfileResolver.Resolve(dst.Profile);

        using var pack = PackFile.Open(packPath);
        var plan = PackImporter.Plan(pack.Manifest, dst, ModUpdateService.ScanLocal(dst));
        Assert.Equal(PackItemAction.Update, plan.Items.Single(i => i.Mod.ModId == "alpha").Action);
        Assert.Equal(PackItemAction.Install, plan.Items.Single(i => i.Mod.ModId == "beta").Action);
        Assert.Equal(["extra"], plan.NotInPack.Select(l => l.Info!.ModId));

        using var db = new ModDbClient();
        var importer = new PackImporter(db, new ModUpdater(db, Path.Combine(_root, "dl")));
        var result = await importer.ApplyAsync(pack, plan, dst, mirror: true, applyConfig: false,
            new ModBackupStore(Path.Combine(_root, "backups")));

        Assert.Empty(result.Problems);
        var files = Directory.GetFiles(dst.InstallDir!).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["alpha_1.0.0.zip", "beta_2.0.0.zip", "extra_1.0.0.zip"], files);

        var disabled = JObject.Parse(File.ReadAllText(dst.ConfigPath!))["stringListSettings"]!["disabledMods"]!.Values<string>().Order();
        Assert.Equal(["beta", "extra"], disabled); // beta выключен как в паке, extra — зеркалирование
    }

    [Fact]
    public void RejectsZipWithoutManifest()
    {
        var bad = Path.Combine(Directory.CreateDirectory(_root).FullName, "bad.evpack");
        using (var z = ZipFile.Open(bad, ZipArchiveMode.Create)) z.CreateEntry("readme.txt");
        Assert.Throws<InvalidDataException>(() => PackFile.Open(bad));
    }
}
