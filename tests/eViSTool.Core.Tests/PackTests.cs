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

    private static void Mod(ResolvedProfile p, string id, string version, string? side = null)
    {
        using var zip = ZipFile.Open(Path.Combine(p.InstallDir!, $"{id}_{version}.zip"), ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
        var sideJson = side is null ? "" : $$""", "side": "{{side}}" """;
        w.Write($$"""{ "modid": "{{id}}", "name": "{{id}}", "version": "{{version}}"{{sideJson}} }""");
    }

    /// <summary>Серверный профиль: папка данных с Mods (моды ставятся туда).</summary>
    private ResolvedProfile Server(string name)
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(data, "Mods"));
        return ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Server, DataDir = data });
    }

    // пак клиента ставится на сервер: клиентский мод пропускается (по стороне из пака, а у пака старой версии — из
    // самого файла), галочка «всё равно поставить» ставит и его
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportToServer_SkipsClientOnlyMods(bool oldPackWithoutSide)
    {
        var src = Profile("src");
        Mod(src, "alpha", "1.0.0");
        Mod(src, "hud", "2.0.0", side: "Client");
        Mod(src, "tweaks", "1.0.0", side: "Server");
        src = ProfileResolver.Resolve(src.Profile);
        var packPath = BuildPack(src, bundle: true);
        if (oldPackWithoutSide)
            using (var zip = ZipFile.Open(packPath, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry(PackManifest.FileName)!;
                JObject json;
                using (var r = new StreamReader(entry.Open())) json = JObject.Parse(r.ReadToEnd());
                foreach (var m in (JArray)json["Mods"]!) ((JObject)m).Remove("Side");
                entry.Delete();
                using var w = new StreamWriter(zip.CreateEntry(PackManifest.FileName).Open());
                w.Write(json.ToString());
            }

        var dst = Server("srv");
        using var pack = PackFile.Open(packPath);
        var plan = PackImporter.Plan(pack.Manifest, dst, ModUpdateService.ScanLocal(dst));
        Assert.True(plan.ForServer && plan.SkipClientOnly);
        Assert.Equal(!oldPackWithoutSide, plan.Items.Single(i => i.Mod.ModId == "hud").ClientOnly);
        Assert.False(plan.Items.Single(i => i.Mod.ModId == "tweaks").ClientOnly);

        using var db = new ModDbClient();
        var importer = new PackImporter(db, new ModUpdater(db, Path.Combine(_root, "dl")));
        var result = await importer.ApplyAsync(pack, plan, dst, mirror: false, applyConfig: false, new ModBackupStore(Path.Combine(_root, "backups")));
        Assert.Empty(result.Problems);
        Assert.Contains(result.Done, d => d.Contains("hud"));
        Assert.Equal(["alpha_1.0.0.zip", "tweaks_1.0.0.zip"], Directory.GetFiles(dst.InstallDir!).Select(Path.GetFileName).Order());

        // «всё равно поставить»
        var all = await importer.ApplyAsync(pack, plan with { SkipClientOnly = false }, ProfileResolver.Resolve(dst.Profile), mirror: false,
            applyConfig: false, new ModBackupStore(Path.Combine(_root, "backups")));
        Assert.Contains("hud_2.0.0.zip", Directory.GetFiles(dst.InstallDir!).Select(Path.GetFileName));
    }

    // в клиентский профиль клиентские моды ставятся как всегда
    [Fact]
    public void ImportToClient_KeepsClientMods()
    {
        var src = Profile("src");
        Mod(src, "hud", "2.0.0", side: "Client");
        src = ProfileResolver.Resolve(src.Profile);
        using var pack = PackFile.Open(BuildPack(src, bundle: true));
        Assert.Equal("Client", pack.Manifest.Mods.Single().Side);
        var dst = Profile("dst");
        var plan = PackImporter.Plan(pack.Manifest, dst, ModUpdateService.ScanLocal(dst));
        Assert.False(plan.ForServer);
        Assert.True(plan.Installs(plan.Items.Single()));
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
