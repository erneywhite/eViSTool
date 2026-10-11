using System.IO.Compression;
using eViSTool.Core.Mods;
using eViSTool.Core.Packs;

namespace eViSTool.Core.Tests;

/// <summary>
/// «Враждебные» моды и паки: modid и имена файлов пишет чужой автор, а eViSTool строит по ним пути — выйти за свои папки
/// они не должны (ревью 2026-10-10, пп. 1.2 и 1.3).
/// </summary>
public sealed class HostileModTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-hostile-" + Guid.NewGuid().ToString("N"));

    public HostileModTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("carryon")]
    [InlineData("vsimgui")]
    [InlineData("fsa_carry-lib.v2")]
    [InlineData("1kmod")]
    public void SafeModIds_Pass(string modId) => Assert.True(PathRules.IsSafeModId(modId));

    [Theory]
    [InlineData("..\\..\\Documents")]
    [InlineData("../../etc")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData(".hidden")]
    [InlineData("C:\\Windows")]
    [InlineData("sub/mod")]
    [InlineData("")]
    [InlineData("Upper")]
    public void HostileModIds_AreRefused(string modId) => Assert.False(PathRules.IsSafeModId(modId));

    [Fact]
    public void Scanner_KeepsAModWithHostileModId_AsBrokenWithoutInfo()
    {
        var zip = Path.Combine(_root, "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("modinfo.json").Open()))
            w.Write("""{ "modid": "..\\..\\Documents", "name": "Evil", "version": "1.0.0" }""");

        var mod = ModScanner.ReadZip(zip);
        Assert.Null(mod.Info); // без modinfo мод не ставится, не обновляется и не уходит в хранилище
        Assert.NotNull(mod.Error);
    }

    [Fact]
    public void BackupStore_RefusesAModIdOutsideItself()
    {
        var store = new ModBackupStore(Path.Combine(_root, "backups"));
        Assert.Throws<InvalidDataException>(() => store.DirFor("..\\..\\Documents"));
        Assert.Equal(Path.Combine(_root, "backups", "carryon"), store.DirFor("carryon"));
    }

    [Theory]
    [InlineData("..\\..\\evil.zip")]
    [InlineData("../evil.zip")]
    [InlineData("sub/evil.zip")]
    [InlineData("C:\\evil.zip")]
    public void Pack_WithAPathInTheModFileName_IsRefused(string fileName)
    {
        var pack = Path.Combine(_root, "evil.evpack");
        using (var z = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry(PackManifest.FileName).Open()))
                w.Write(Newtonsoft.Json.JsonConvert.SerializeObject(new PackManifest
                {
                    Mods = [new PackMod { ModId = "evil", Name = "Evil", FileName = fileName, Bundled = true }],
                }));
            using var e = new StreamWriter(z.CreateEntry(PackManifest.ModsFolder + fileName).Open());
            e.Write("x");
        }

        var temp = Path.Combine(_root, "temp", "pack");
        using var file = PackFile.Open(pack);
        Assert.Throws<InvalidDataException>(() => file.ExtractMod(file.Manifest.Mods[0], temp));
        Assert.Empty(Directory.EnumerateFiles(_root, "evil.zip", SearchOption.AllDirectories));
    }
}
