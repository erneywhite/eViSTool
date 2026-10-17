using System.IO.Compression;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>
/// Кэш modinfo вместе с чтением архивов: тёплый скан не ходит в сеть, изменённый файл перечитывается. Основные
/// попадания и промахи, Retain и битый файл — в <see cref="HostedContractsTests"/>; здесь — то, что вокруг них.
/// </summary>
public sealed class ModInfoCacheTests : IDisposable
{
    private readonly string _profileId = "test-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { Directory.Delete(ModInfoCache.DirFor(_profileId), recursive: true); } catch (IOException) { }
    }

    private static readonly DateTime Utc = new(2026, 10, 17, 8, 23, 41, DateTimeKind.Utc);

    private static RemoteEntry File(string path, long size, DateTime? modified = null) =>
        new(path, RemotePath.Name(path), false, size, modified ?? Utc);

    /// <summary>Архив в памяти, считает обращения к сети.</summary>
    private sealed class Counting(byte[] data) : IRangeSource
    {
        public int Calls { get; private set; }
        public long Length => data.Length;

        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(data.AsSpan((int)offset, (int)Math.Min(count, data.Length - offset)).ToArray());
        }

        public Task<Stream> OpenAllAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
        }
    }

    private static byte[] Mod(string modid, string version)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("modinfo.json").Open());
            w.Write($$"""{ "type": "code", "modid": "{{modid}}", "name": "{{modid}}", "version": "{{version}}" }""");
        }
        return buffer.ToArray();
    }

    /// <summary>Скан, как его делает хост: сначала кэш, иначе архив по сети и запись в кэш.</summary>
    private static async Task<LocalMod> Scan(ModInfoCache cache, RemoteEntry file, Counting source)
    {
        if (!cache.TryGet(file, out var cached))
        {
            cached = await ZipProbe.ReadModInfoAsync(source, CancellationToken.None);
            cache.Put(file, cached);
        }
        return ModScanner.FromCached(file.Path, cached);
    }

    [Fact]
    public async Task WarmScan_DoesNotTouchTheNetwork_ChangedFileIsReadAgain()
    {
        var v1 = Mod("carryon", "1.8.0");
        var file = File("/Mods/carryon.zip", v1.Length);
        var cache = ModInfoCache.ForProfile(_profileId);

        var source = new Counting(v1);
        Assert.Equal("1.8.0", (await Scan(cache, file, source)).Info!.Version);
        Assert.Equal(1, source.Calls);
        cache.Save();

        // другое окно (или следующий запуск) — тот же кэш с диска, сеть не нужна
        var warm = ModInfoCache.ForProfile(_profileId);
        var untouched = new Counting(v1);
        Assert.Equal("carryon", (await Scan(warm, file, untouched)).Info!.ModId);
        Assert.Equal(0, untouched.Calls);

        // файл перезалили новой версией — размер и время другие, архив читается заново
        var v2 = Mod("carryon", "1.9.0");
        var updated = new Counting(v2);
        var mod = await Scan(warm, File("/Mods/carryon.zip", v2.Length, Utc.AddMinutes(5)), updated);
        Assert.Equal("1.9.0", mod.Info!.Version);
        Assert.Equal(1, updated.Calls);
    }

    [Fact]
    public async Task ErrorKeys_AreCachedToo()
    {
        var broken = "PK definitely not a zip"u8.ToArray();
        var file = File("/Mods/broken.zip", broken.Length);
        var cache = ModInfoCache.ForProfile(_profileId);

        Assert.NotNull((await Scan(cache, file, new Counting(broken))).Error);
        cache.Save();

        var again = new Counting(broken);
        var mod = await Scan(ModInfoCache.ForProfile(_profileId), file, again);
        Assert.Equal(0, again.Calls);
        HostedTestText.AssertIs("scan.corrupt", mod.Error!);
    }

    [Fact]
    public void UtcTimeFromMlsd_RoundTrips()
    {
        var cache = ModInfoCache.ForProfile(_profileId);
        cache.Put(File("/Mods/a.zip", 10), new CachedModInfo("{}", null));
        cache.Save();

        Assert.True(ModInfoCache.ForProfile(_profileId).TryGet(File("/Mods/a.zip", 10), out _));
        Assert.False(ModInfoCache.ForProfile(_profileId).TryGet(File("/Mods/a.zip", 10, Utc.AddSeconds(1)), out _));
    }

    [Fact]
    public void NonAsciiPaths_AndPutOverwrites()
    {
        var cache = ModInfoCache.ForProfile(_profileId);
        var path = "/Mods/Карта мира 1.0.zip";
        cache.Put(File(path, 10), new CachedModInfo(null, "scan.noModInfo"));
        cache.Put(File(path, 20), new CachedModInfo("""{"modid":"map"}""", null));
        cache.Save();

        var again = ModInfoCache.ForProfile(_profileId);
        Assert.False(again.TryGet(File(path, 10), out _)); // прежняя запись заменена
        Assert.True(again.TryGet(File(path, 20), out var hit));
        Assert.Equal(new CachedModInfo("""{"modid":"map"}""", null), hit);
    }

    [Fact]
    public void SaveWithoutChanges_WritesNothing()
    {
        var cache = ModInfoCache.ForProfile(_profileId);
        Assert.True(cache.Save());
        Assert.False(System.IO.File.Exists(Path.Combine(ModInfoCache.DirFor(_profileId), ModInfoCache.FileName)));

        cache.Put(File("/Mods/a.zip", 1), new CachedModInfo("{}", null));
        cache.Retain(["/Mods/a.zip"]); // ничего не выбросил
        Assert.True(cache.Save());
        Assert.True(System.IO.File.Exists(Path.Combine(ModInfoCache.DirFor(_profileId), ModInfoCache.FileName)));
    }

    [Fact]
    public void NullEntriesInFile_AreSkipped()
    {
        // файл кэша дописан руками или более новой версией: пустые записи пропускаются, остальное годно
        var dir = ModInfoCache.DirFor(_profileId);
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(Path.Combine(dir, ModInfoCache.FileName),
            """{ "/Mods/x.zip": null, "/Mods/a.zip": { "Size": 10, "Modified": "2026-10-17T08:23:41Z", "Json": "{}", "Future": 1 } }""");

        var cache = ModInfoCache.ForProfile(_profileId);
        Assert.True(cache.TryGet(File("/Mods/a.zip", 10), out var hit));
        Assert.Equal("{}", hit.ModInfoJson);
        Assert.False(cache.TryGet(File("/Mods/x.zip", 10), out _));
    }

    [Fact]
    public void ParallelPuts_AllLand()
    {
        var cache = ModInfoCache.ForProfile(_profileId);
        Parallel.For(0, 200, i => cache.Put(File($"/Mods/m{i}.zip", i), new CachedModInfo("{}", null)));
        cache.Save();

        var again = ModInfoCache.ForProfile(_profileId);
        Assert.All(Enumerable.Range(0, 200), i => Assert.True(again.TryGet(File($"/Mods/m{i}.zip", i), out _)));
        Assert.Empty(Directory.GetFiles(ModInfoCache.DirFor(_profileId), "*.tmp"));
    }
}
