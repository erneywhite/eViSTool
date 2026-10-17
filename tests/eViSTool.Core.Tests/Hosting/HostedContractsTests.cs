using System.IO.Compression;
using System.Text;
using eViSTool.Core.Hosting;
using eViSTool.Core.Mods;

namespace eViSTool.Core.Tests.Hosting;

/// <summary>Рабочие части контрактов P0: кэш modinfo, запасное чтение архива целиком, фабрика транспортов, закрепление путей.</summary>
public sealed class HostedContractsTests : IDisposable
{
    private readonly string _profileId = "test-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { Directory.Delete(ModInfoCache.DirFor(_profileId), recursive: true); } catch (IOException) { }
    }

    private static readonly DateTime When = new(2026, 10, 17, 10, 23, 0, DateTimeKind.Utc);

    private static RemoteEntry File(string path, long size = 100, DateTime? modified = null) =>
        new(path, RemotePath.Name(path), false, size, modified ?? When);

    // ---------- кэш modinfo

    [Fact]
    public void Cache_HitsOnlyOnSameSizeAndTime_AndSurvivesReload()
    {
        var cache = ModInfoCache.ForProfile(_profileId);
        var mod = File("/Mods/carryon.zip");
        Assert.False(cache.TryGet(mod, out _));

        cache.Put(mod, new CachedModInfo("""{"modid":"carryon"}""", null));
        cache.Put(File("/Mods/broken.zip"), new CachedModInfo(null, "scan.corrupt"));
        cache.Save();

        var again = ModInfoCache.ForProfile(_profileId);
        Assert.True(again.TryGet(mod, out var hit));
        Assert.Equal("""{"modid":"carryon"}""", hit.ModInfoJson);
        Assert.True(again.TryGet(File("/Mods/broken.zip"), out var broken));
        Assert.Equal(new CachedModInfo(null, "scan.corrupt"), broken);

        Assert.False(again.TryGet(mod with { Size = 101 }, out _));                   // файл заменили
        Assert.False(again.TryGet(mod with { Modified = When.AddMinutes(1) }, out _)); // перезалили
        Assert.False(again.TryGet(mod with { Modified = null }, out _));              // время неизвестно — не доверяем
        Assert.False(again.TryGet(File("/mods/carryon.zip"), out _));                  // регистр в пути важен
    }

    [Fact]
    public void Cache_LocalServerTime_RoundTrips()
    {
        // LIST отдаёт местное время сервера без пояса — после записи и чтения оно должно совпасть
        var local = new DateTime(2026, 10, 17, 12, 23, 0, DateTimeKind.Unspecified);
        var cache = ModInfoCache.ForProfile(_profileId);
        cache.Put(File("/Mods/a.zip", modified: local), new CachedModInfo("{}", null));
        cache.Save();
        Assert.True(ModInfoCache.ForProfile(_profileId).TryGet(File("/Mods/a.zip", modified: local), out _));
    }

    [Fact]
    public void Cache_SaveFailure_DoesNotThrow_AndKeepsChangesForNextSave()
    {
        var cache = ModInfoCache.ForProfile(_profileId);
        cache.Put(File("/Mods/a.zip"), new CachedModInfo("{}", null));
        var dir = ModInfoCache.DirFor(_profileId);
        var blocker = Path.Combine(dir, ModInfoCache.FileName);
        Directory.CreateDirectory(blocker); // на месте файла кэша — папка: записать не выйдет

        Assert.False(cache.Save());
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        Directory.Delete(blocker);
        Assert.True(cache.Save()); // изменения не потерялись, хотя новых Put не было
        Assert.True(ModInfoCache.ForProfile(_profileId).TryGet(File("/Mods/a.zip"), out _));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Cache_RetainDropsGoneFiles_BrokenFileIsEmpty_DeleteRemoves()
    {
        var cache = ModInfoCache.ForProfile(_profileId);
        cache.Put(File("/Mods/a.zip"), new CachedModInfo("{}", null));
        cache.Put(File("/Mods/b.zip"), new CachedModInfo("{}", null));
        cache.Retain(["/Mods/b.zip"]);
        cache.Save();
        var reloaded = ModInfoCache.ForProfile(_profileId);
        Assert.False(reloaded.TryGet(File("/Mods/a.zip"), out _));
        Assert.True(reloaded.TryGet(File("/Mods/b.zip"), out _));

        var file = Path.Combine(ModInfoCache.DirFor(_profileId), ModInfoCache.FileName);
        System.IO.File.WriteAllText(file, "{ не json");
        Assert.False(ModInfoCache.ForProfile(_profileId).TryGet(File("/Mods/b.zip"), out _));

        ModInfoCache.Delete(_profileId);
        Assert.False(System.IO.File.Exists(file));
        ModInfoCache.Delete(_profileId); // второй раз — без ошибки
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Cache_RejectsProfileIdWithPath(string id) => Assert.Throws<ArgumentException>(() => ModInfoCache.ForProfile(id));

    // ---------- архив целиком (запасной путь ZipProbe)

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(true));
                w.Write(text);
            }
        return buffer.ToArray();
    }

    private sealed class Bytes(byte[] data, bool seekable = true) : IRangeSource
    {
        public int Opened { get; private set; }
        public long Length => data.Length;
        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) =>
            Task.FromResult(data.Skip((int)offset).Take(count).ToArray());
        public Task<Stream> OpenAllAsync(CancellationToken ct)
        {
            Opened++;
            Stream s = seekable ? new MemoryStream(data, writable: false) : new NonSeekable(data);
            return Task.FromResult(s);
        }
    }

    private sealed class NonSeekable(byte[] data) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;
    }

    private sealed class Failing : IRangeSource
    {
        public long Length => 1000;
        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) => throw new RemoteFilesException(RemoteFailure.Connect, "gone");
        public Task<Stream> OpenAllAsync(CancellationToken ct) => throw new RemoteFilesException(RemoteFailure.Connect, "gone");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_ReadsRootModInfo_WithBom(bool seekable)
    {
        var json = """{ "type": "code", "modid": "carryon", "name": "Carry On", "version": "1.8.0" }""";
        var source = new Bytes(Zip(("assets/x.json", "{}"), ("ModInfo.json", json)), seekable);

        var info = await ZipProbe.ReadModInfoAsync(source, CancellationToken.None);

        Assert.Equal(json, info.ModInfoJson); // BOM снят
        Assert.Null(info.ErrorKey);
        var mod = ModScanner.FromCached("/Mods/carryon.zip", info);
        Assert.Equal("carryon", mod.Info!.ModId);
        Assert.Equal("carryon.zip", mod.FileName);
    }

    [Fact]
    public async Task Probe_GivesErrorKeys_ForNestedMissingAndBroken()
    {
        Assert.Equal("scan.nestedModInfo",
            (await ZipProbe.ReadModInfoAsync(new Bytes(Zip(("inner/modinfo.json", "{}"))), CancellationToken.None)).ErrorKey);
        Assert.Equal("scan.noModInfo",
            (await ZipProbe.ReadModInfoAsync(new Bytes(Zip(("readme.txt", "x"))), CancellationToken.None)).ErrorKey);
        Assert.Equal("scan.corrupt",
            (await ZipProbe.ReadModInfoAsync(new Bytes("PK not really a zip"u8.ToArray()), CancellationToken.None)).ErrorKey);
    }

    [Fact]
    public async Task Probe_LetsNetworkFailuresThrough()
    {
        // сбой сети — не «битый архив»: в кэш такое попасть не должно
        await Assert.ThrowsAsync<RemoteFilesException>(() => ZipProbe.ReadModInfoAsync(new Failing(), CancellationToken.None));
    }

    [Fact]
    public void FromCached_UsesTheSameChecksAsTheFolderScanner()
    {
        var bad = ModScanner.FromCached("/Mods/x.zip", new CachedModInfo("""{ "modid": "..\\..\\evil", "name": "x", "version": "1" }""", null));
        Assert.Null(bad.Info); // modid с путём — как у локального сканера
        Assert.NotNull(bad.Error);
        var noModInfo = ModScanner.FromCached("/Mods/y.zip", new CachedModInfo(null, "scan.noModInfo"));
        HostedTestText.AssertIs("scan.noModInfo", noModInfo.Error!);
        HostedTestText.AssertIs("scan.nestedModInfo", ModScanner.FromCached("/Mods/n.zip", new CachedModInfo(null, "scan.nestedModInfo")).Error!);
        // незнакомый ключ из кэша (битый файл или более новая версия) — «повреждён», не сырой ключ
        HostedTestText.AssertIs("scan.corrupt", ModScanner.FromCached("/Mods/z.zip", new CachedModInfo(null, "scan.fromTheFuture")).Error!);
        HostedTestText.AssertIs("scan.corrupt", ModScanner.FromCached("/Mods/z.zip", new CachedModInfo(null, null)).Error!);
    }

    [Fact]
    public void ModConfigService_RefusesHostedProfile_InsteadOfFallingBackToLocalData()
    {
        // пустой DataDir у своего профиля — папка данных игры этого компьютера; у хостового туда идти нельзя
        var hosted = new Profiles.GameProfile { Name = "G-Portal", Kind = Profiles.ProfileKind.Server, RemoteCode = Profiles.GameProfile.HostedMarker };
        var ex = Assert.Throws<InvalidOperationException>(() => Profiles.ModConfigService.ForProfile(hosted));
        HostedTestText.AssertIs("mcfg.hostedUnavailable", ex.Message);
    }

    // ---------- фабрика и закрепление путей

    [Fact]
    public void Factory_PrefersHostOverride_AndReportsMissingTransport()
    {
        // протокол, которого нет в перечислении: его транспорт не зарегистрирует ни один соседний тест
        // (тесты Core идут параллельно, а регистрация по протоколу общая на весь процесс)
        var endpoint = new HostedEndpoint((HostedProtocol)int.MaxValue, "fake-" + Guid.NewGuid().ToString("N") + ".test", 22, FtpTls.None, "u", "p", false, null);
        var ex = Assert.Throws<RemoteFilesException>(() => RemoteFilesFactory.Create(endpoint));
        Assert.Equal(RemoteFailure.Unsupported, ex.Kind);
        Assert.DoesNotContain("\"p\"", ex.Message);

        IRemoteFiles? made = null;
        using (RemoteFilesFactory.RegisterHost(endpoint.Host, e => made = new NoFiles()))
        {
            var files = RemoteFilesFactory.Create(endpoint);
            Assert.Same(made, files);
        }
        Assert.Throws<RemoteFilesException>(() => RemoteFilesFactory.Create(endpoint)); // подмена снята
    }

    private sealed class NoFiles : IRemoteFiles
    {
        public RemoteFeatures Features { get; } = new(false, true, false, null);
        public bool IsConnected => false;
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string dir, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteEntry>>([]);
        public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => Task.FromResult<RemoteEntry?>(null);
        public Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct) => Task.FromResult(Array.Empty<byte>());
        public Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task UploadAsync(Stream source, string path, IProgress<long>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task RenameAsync(string from, string to, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteFileAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Pin_StoresPathsRelativeToRoot()
    {
        var settings = new HostedConnection { Host = "198.51.100.7", Root = "/g123456", Password = "enc" };
        var layout = new HostedLayout("/g123456", "/g123456/serverconfig.json", "/g123456", "/g123456",
            ["/g123456/Mods", "/g123456/gportal_mods", "/elsewhere/Mods"], "/g123456/Mods", []);

        var pinned = HostedProbe.Pin(settings, layout);

        Assert.Equal("serverconfig.json", pinned.ConfigPath);
        Assert.Equal(".", pinned.GameDir);
        Assert.Equal("Mods", pinned.InstallDir);
        Assert.Equal(["Mods", "gportal_mods"], pinned.ModDirs!); // путь вне корня не закрепляется
        Assert.Equal("enc", pinned.Password);
        Assert.Null(settings.ConfigPath); // исходные настройки не тронуты
        Assert.Equal("/g123456/Mods", RemotePath.Combine(pinned.Root, pinned.InstallDir!));
    }

    [Fact]
    public async Task TextHelpers_WriteUtf8WithoutBom()
    {
        var files = new MemoryFiles();
        await files.WriteTextAsync("/serverconfig.json", "{ \"ServerName\": \"Сервер\" }", CancellationToken.None);
        Assert.NotEqual(0xEF, files.Last[0]);
        Assert.Equal("{ \"ServerName\": \"Сервер\" }", await files.ReadTextAsync("/serverconfig.json", CancellationToken.None));
    }

    private sealed class MemoryFiles : IRemoteFiles
    {
        public byte[] Last { get; private set; } = [];
        public RemoteFeatures Features { get; } = new(false, true, false, null);
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string dir, CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteEntry>>([]);
        public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => Task.FromResult<RemoteEntry?>(null);
        public Task<byte[]> ReadRangeAsync(string path, long offset, int count, CancellationToken ct) => Task.FromResult(Last);
        public Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct) =>
            destination.WriteAsync(Last, ct).AsTask();
        public async Task UploadAsync(Stream source, string path, IProgress<long>? progress, CancellationToken ct)
        {
            using var copy = new MemoryStream();
            await source.CopyToAsync(copy, ct);
            Last = copy.ToArray();
        }
        public Task RenameAsync(string from, string to, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteFileAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
