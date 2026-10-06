using System.IO.Compression;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Packs;
using eViSTool.Core.Profiles;

namespace eViSTool.Core.Tests;

/// <summary>Содержимое пака сверяется с его описанием до любых изменений профиля (аудит, пункт 12).</summary>
public sealed class PackCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-packcheck-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ResolvedProfile Profile(string name)
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(data, "Mods"));
        File.WriteAllText(Path.Combine(data, "clientsettings.json"), """{ "stringListSettings": { "disabledMods": [], "modPaths": [] } }""");
        return ProfileResolver.Resolve(new GameProfile { Kind = ProfileKind.Client, DataDir = data });
    }

    /// <summary>Zip мода с таким modinfo (соль — чтобы различались контрольные суммы).</summary>
    private static string ModZip(string dir, string file, string id, string version, string salt = "")
    {
        var path = Path.Combine(Directory.CreateDirectory(dir).FullName, file);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open()))
            w.Write($$"""{ "modid": "{{id}}", "name": "{{id}}", "version": "{{version}}" }""");
        using (var w = new StreamWriter(zip.CreateEntry("salt.txt").Open())) w.Write(salt);
        return path;
    }

    /// <summary>Пак руками: описание и (для вложенных) файлы как есть — в том числе «не те».</summary>
    private string HandPack(params (PackMod Mod, string? File)[] entries)
    {
        var path = Path.Combine(_root, "hand-" + Guid.NewGuid().ToString("N")[..6] + ".evpack");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (mod, file) in entries.Where(e => e.File is not null))
            zip.CreateEntryFromFile(file!, PackManifest.ModsFolder + mod.FileName);
        var manifest = new PackManifest { Name = "hand", Mods = entries.Select(e => e.Mod).ToList() };
        using var w = new StreamWriter(zip.CreateEntry(PackManifest.FileName).Open());
        w.Write(Newtonsoft.Json.JsonConvert.SerializeObject(manifest));
        return path;
    }

    private static PackMod Entry(string id, string version, string file, string sha, bool bundled = true) =>
        new() { ModId = id, Name = id, Version = version, FileName = Path.GetFileName(file), Sha256 = sha, Bundled = bundled };

    private async Task<PackImportResult> Import(string packPath, ResolvedProfile dst,
        Func<PackMod, CancellationToken, Task<string>>? download = null, Func<IReadOnlyList<string>, bool>? confirm = null)
    {
        using var pack = PackFile.Open(packPath);
        var plan = PackImporter.Plan(pack.Manifest, dst, ModUpdateService.ScanLocal(dst));
        using var db = new ModDbClient();
        var importer = new PackImporter(db, new ModUpdater(db, Path.Combine(_root, "dl"))) { Download = download };
        return await importer.ApplyAsync(pack, plan, dst, mirror: false, applyConfig: false,
            new ModBackupStore(Path.Combine(_root, "backups")), confirmChanged: confirm);
    }

    private static string[] Files(ResolvedProfile p) => Directory.GetFiles(p.InstallDir!).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();

    private string Src => Path.Combine(_root, "src-files");

    [Fact]
    public async Task AnotherModUnderTheName_IsNotInstalled_AndTheExistingOneIsNotReplaced()
    {
        var beta = ModZip(Src, "alpha_1.0.0.zip", "beta", "9.0.0"); // в описании alpha, внутри — beta
        var dst = Profile("dst");
        ModZip(dst.InstallDir!, "beta_1.0.0.zip", "beta", "1.0.0"); // у игрока уже есть beta — её не должны заменить

        var result = await Import(HandPack((Entry("alpha", "1.0.0", beta, PackBuilder.Sha256Of(beta)), beta)), dst);

        Assert.Empty(result.Done);
        Assert.Contains(result.Problems, p => p.Contains("beta 9.0.0"));
        Assert.Equal(["beta_1.0.0.zip"], Files(dst));
    }

    [Fact]
    public async Task AnotherVersion_IsNotInstalled_ButTheSameVersionWrittenDifferentlyIs()
    {
        var wrong = ModZip(Src, "alpha_1.0.0.zip", "alpha", "2.0.0");
        var right = ModZip(Src, "gamma_1.0.zip", "gamma", "1.0.0"); // в описании «1.0» — это та же версия
        var dst = Profile("dst");

        var result = await Import(HandPack(
            (Entry("alpha", "1.0.0", wrong, PackBuilder.Sha256Of(wrong)), wrong),
            (Entry("gamma", "1.0", right, PackBuilder.Sha256Of(right)), right)), dst);

        Assert.Equal(["gamma 1.0.0"], result.Done); // отчёт — по самому архиву
        Assert.Single(result.Problems, p => p.Contains("alpha 2.0.0"));
        Assert.Equal(["gamma_1.0.zip"], Files(dst));
    }

    [Fact]
    public async Task DamagedBundledFile_IsNotInstalled()
    {
        var file = ModZip(Src, "alpha_1.0.0.zip", "alpha", "1.0.0", salt: "подменён");
        var dst = Profile("dst");

        var result = await Import(HandPack((Entry("alpha", "1.0.0", file, new string('0', 64)), file)), dst);

        Assert.Empty(result.Done);
        Assert.Single(result.Problems);
        Assert.Empty(Files(dst));
    }

    [Fact]
    public async Task ChangedFileOnModDb_IsInstalledOnlyIfTheUserAgrees_AndAskedBeforeAnythingChanges()
    {
        var dl = Path.Combine(_root, "dl-src");
        var good = ModZip(dl, "beta_1.0.0.zip", "beta", "1.0.0");
        var reup = ModZip(dl, "alpha_1.0.0.zip", "alpha", "1.0.0", salt: "перезалит");
        var packPath = HandPack(
            (Entry("alpha", "1.0.0", reup, new string('0', 64), bundled: false), null),
            (Entry("beta", "1.0.0", good, PackBuilder.Sha256Of(good), bundled: false), null));
        // «скачивание» — копия, как настоящая загрузка (импорт её потом убирает)
        Task<string> Fake(PackMod m, CancellationToken _)
        {
            var copy = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "fetched", Guid.NewGuid().ToString("N"))).FullName, m.ModId + "_1.0.0.zip");
            File.Copy(m.ModId == "alpha" ? reup : good, copy);
            return Task.FromResult(copy);
        }

        // «нет» — перезалитый пропущен, остальное поставлено; к моменту вопроса профиль ещё не тронут
        var dst = Profile("no");
        string[]? filesWhenAsked = null;
        var declined = await Import(packPath, dst, Fake, _ => { filesWhenAsked = Files(dst); return false; });
        Assert.Empty(filesWhenAsked!);
        Assert.Equal(["beta 1.0.0"], declined.Done);
        Assert.Single(declined.Problems, p => p.Contains("alpha"));
        Assert.Equal(["beta_1.0.0.zip"], Files(dst));

        // «да» — поставлены оба
        var dst2 = Profile("yes");
        IReadOnlyList<string>? asked = null;
        var agreed = await Import(packPath, dst2, Fake, mods => { asked = mods; return true; });
        Assert.Equal(["alpha 1.0.0"], asked);
        Assert.Equal(2, agreed.Done.Count);
        Assert.Empty(agreed.Problems);
    }

    [Fact]
    public async Task RepeatedEntry_IsShownAsSkipped_AndOnlyTheFirstIsInstalled()
    {
        var first = ModZip(Src, "alpha_1.0.0.zip", "alpha", "1.0.0");
        var second = ModZip(Path.Combine(Src, "2"), "alpha_2.0.0.zip", "alpha", "2.0.0");
        var dst = Profile("dst");
        var packPath = HandPack(
            (Entry("alpha", "1.0.0", first, PackBuilder.Sha256Of(first)), first),
            (Entry("alpha", "2.0.0", second, PackBuilder.Sha256Of(second)), second));

        using (var pack = PackFile.Open(packPath))
            Assert.Equal([PackItemAction.Install, PackItemAction.Duplicate], PackImporter.Plan(pack.Manifest, dst, []).Items.Select(i => i.Action));
        var result = await Import(packPath, dst);

        Assert.Equal(["alpha 1.0.0"], result.Done);
        Assert.Single(result.Problems, p => p.Contains("alpha 2.0.0"));
        Assert.Equal(["alpha_1.0.0.zip"], Files(dst));
    }

    [Fact]
    public async Task MissingFile_IsAProblem_AndTheRestIsInstalled()
    {
        var ok = ModZip(Src, "beta_1.0.0.zip", "beta", "1.0.0");
        var dst = Profile("dst");

        var result = await Import(HandPack(
            (Entry("alpha", "1.0.0", "alpha_1.0.0.zip", "", bundled: true), null), // описан как вложенный, файла нет — и скачать неоткуда
            (Entry("beta", "1.0.0", ok, PackBuilder.Sha256Of(ok)), ok)), dst,
            download: (_, _) => throw new HttpRequestException("нет сети"));

        Assert.Equal(["beta 1.0.0"], result.Done);
        Assert.Single(result.Problems, p => p.Contains("alpha"));
        Assert.Equal(["beta_1.0.0.zip"], Files(dst));
    }
}
