using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using eViSTool.Core.AppUpdate;
using eViSTool.Core.Versioning;

namespace eViSTool.Core.Tests;

public sealed class AppUpdaterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;

    public AppUpdaterTests()
    {
        _app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        File.WriteAllText(Path.Combine(_app, "eViSTool.exe"), "old app");
        File.WriteAllText(Path.Combine(_app, "eViSTool.Agent.exe"), "old agent");
        Directory.CreateDirectory(Path.Combine(_app, "data"));
        File.WriteAllText(Path.Combine(_app, "data", "settings.json"), "my settings");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ModVersion V(string text) => ModVersion.ParseOrNull(text)!;

    private const string Releases = """
    [
      { "tag_name": "v0.2.0", "draft": true, "prerelease": false, "assets": [ { "name": "eViSTool-0.2.0-win-x64.zip", "browser_download_url": "u", "digest": "sha256:AA" } ] },
      { "tag_name": "v0.1.0", "prerelease": false, "html_url": "https://github.com/x/releases/v0.1.0", "body": "notes",
        "assets": [ { "name": "eViSTool-0.1.0-win-x64.zip", "browser_download_url": "https://dl/0.1.0.zip", "size": 10, "digest": "sha256:ABCDEF" } ] },
      { "tag_name": "v0.1.0-alpha.2", "prerelease": true,
        "assets": [ { "name": "eViSTool-0.1.0-alpha.2-win-x64.zip", "browser_download_url": "https://dl/a2.zip" } ] },
      { "tag_name": "v0.1.0-alpha.3", "prerelease": true, "assets": [ { "name": "source.tar.gz", "browser_download_url": "x" } ] },
      { "tag_name": "не версия", "assets": [] }
    ]
    """;

    [Fact]
    public void ParseReleases_SkipsDraftsAndReleasesWithoutArchive()
    {
        var list = AppUpdater.ParseReleases(Releases);

        Assert.Equal(["v0.1.0", "v0.1.0-alpha.2"], list.Select(r => r.Tag));
        var stable = list[0];
        Assert.False(stable.Prerelease);
        Assert.Equal("abcdef", stable.Sha256);
        Assert.Equal(("https://dl/0.1.0.zip", 10L, "https://github.com/x/releases/v0.1.0", "notes"),
            (stable.AssetUrl, stable.AssetSize, stable.PageUrl, stable.Notes));
        Assert.True(list[1].Prerelease);
        Assert.Null(list[1].Sha256);
    }

    [Fact]
    public void PickUpdate_OffersPrereleasesOnlyToPrereleaseUsers()
    {
        var list = AppUpdater.ParseReleases(Releases);

        Assert.Equal("v0.1.0", AppUpdater.PickUpdate(list, V("0.1.0-alpha.1"))!.Tag); // стабильная старше любой своей альфы
        Assert.Null(AppUpdater.PickUpdate(list, V("0.1.0")));
        Assert.Null(AppUpdater.PickUpdate(list, V("0.3.0")));

        var onlyAlphas = list.Where(r => r.Prerelease).ToList();
        Assert.Equal("v0.1.0-alpha.2", AppUpdater.PickUpdate(onlyAlphas, V("0.1.0-alpha.1"))!.Tag);
        Assert.Null(AppUpdater.PickUpdate(onlyAlphas, V("0.0.9"))); // со стабильной на альфу не уводим
    }

    private string Zip(Dictionary<string, string> files)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }

    [Fact]
    public void Install_SwapsFiles_KeepsData_AndLeavesOldForCleanup()
    {
        AppUpdater.Install(Zip(new() { ["eViSTool.exe"] = "new app", ["eViSTool.Agent.exe"] = "new agent", ["README.md"] = "readme" }), _app);

        Assert.Equal("new app", File.ReadAllText(Path.Combine(_app, "eViSTool.exe")));
        Assert.Equal("new agent", File.ReadAllText(Path.Combine(_app, "eViSTool.Agent.exe")));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(_app, "README.md")));
        Assert.Equal("my settings", File.ReadAllText(Path.Combine(_app, "data", "settings.json")));
        Assert.Equal(2, Directory.GetFiles(_app, "*.old").Length);
        Assert.False(Directory.Exists(Path.Combine(_app, ".update")));

        AppUpdater.CleanupOld(_app);
        Assert.Empty(Directory.GetFiles(_app, "*.old"));
    }

    [Fact]
    public void Install_AcceptsArchiveWithAFolderInside()
    {
        AppUpdater.Install(Zip(new() { ["eViSTool-0.2.0/eViSTool.exe"] = "new app" }), _app);
        Assert.Equal("new app", File.ReadAllText(Path.Combine(_app, "eViSTool.exe")));
        Assert.Equal("old agent", File.ReadAllText(Path.Combine(_app, "eViSTool.Agent.exe")));
    }

    [Fact]
    public void Install_RefusesForeignArchive_AndRollsBackOnFailure()
    {
        Assert.Throws<InvalidOperationException>(() => AppUpdater.Install(Zip(new() { ["other.exe"] = "x" }), _app));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(_app, "eViSTool.exe")));

        // на месте одного из файлов — папка: поставить его нельзя, уже заменённые файлы возвращаются
        Directory.CreateDirectory(Path.Combine(_app, "zzz.txt"));
        Assert.ThrowsAny<IOException>(() => AppUpdater.Install(
            Zip(new() { ["eViSTool.exe"] = "new app", ["eViSTool.Agent.exe"] = "new agent", ["zzz.txt"] = "x" }), _app));
        Assert.Equal("old app", File.ReadAllText(Path.Combine(_app, "eViSTool.exe")));
        Assert.Equal("old agent", File.ReadAllText(Path.Combine(_app, "eViSTool.Agent.exe")));
        Assert.Empty(Directory.GetFiles(_app, "*.old"));
        Assert.False(Directory.Exists(Path.Combine(_app, ".update")));
    }

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task Download_ChecksTheHash()
    {
        var body = "archive bytes"u8.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(body));
        var updater = new AppUpdater(new HttpClient(new FakeHandler(body)));
        AppRelease Release(string? sha) => new() { Version = V("0.2.0"), Tag = "v0.2.0", AssetName = "e-win-x64.zip", AssetUrl = "https://dl/e.zip", Sha256 = sha };
        var downloads = Path.Combine(_root, "dl");

        var path = await updater.DownloadAsync(Release(hash), downloads);
        Assert.Equal(body, await File.ReadAllBytesAsync(path));

        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAsync(Release(new string('0', 64)), downloads));
        Assert.False(File.Exists(path)); // подменённый файл не остаётся
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAsync(Release(null), downloads));
    }

    private const string Feed = """
    <?xml version="1.0" encoding="UTF-8"?>
    <feed xmlns="http://www.w3.org/2005/Atom">
      <entry>
        <id>tag:github.com,2008:Repository/1/v0.1.0-alpha.9</id>
        <link rel="alternate" type="text/html" href="https://github.com/erneywhite/eViSTool/releases/tag/v0.1.0-alpha.9"/>
        <title>eViSTool 0.1.0-alpha.9</title>
      </entry>
      <entry>
        <link rel="alternate" type="text/html" href="https://github.com/erneywhite/eViSTool/releases/tag/v0.2.0"/>
      </entry>
      <entry>
        <link rel="alternate" type="text/html" href="https://github.com/erneywhite/eViSTool/releases/tag/не-версия"/>
      </entry>
    </feed>
    """;

    [Fact]
    public void ParseFeed_GivesReleasesWithDirectLinks()
    {
        var list = AppUpdater.ParseFeed(Feed);
        Assert.Equal(["v0.1.0-alpha.9", "v0.2.0"], list.Select(r => r.Tag));
        var alpha = list[0];
        Assert.True(alpha.Prerelease);
        Assert.False(list[1].Prerelease);
        Assert.Equal("eViSTool-0.1.0-alpha.9-win-x64.zip", alpha.AssetName);
        Assert.Equal("https://github.com/erneywhite/eViSTool/releases/download/v0.1.0-alpha.9/eViSTool-0.1.0-alpha.9-win-x64.zip", alpha.AssetUrl);
        Assert.Equal(alpha.AssetUrl + ".sha256", alpha.ChecksumUrl);
        Assert.Null(alpha.Sha256);
    }

    private sealed class RoutedHandler(Func<string, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(route(request.RequestUri!.ToString()));
    }

    [Fact]
    public async Task ApiRateLimited_FallsBackToFeed_AndChecksumFile()
    {
        var body = "archive bytes"u8.ToArray();
        var sum = Convert.ToHexStringLower(SHA256.HashData(body));
        var badSum = false;
        var updater = new AppUpdater(new HttpClient(new RoutedHandler(url =>
            url.StartsWith("https://api.github.com") ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : url.EndsWith("releases.atom") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed) }
            : url.EndsWith(".sha256") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent((badSum ? new string('0', 64) : sum) + "  file.zip\n") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) })));

        // лимит API исчерпан — релизы из ленты: самый новый — стабильный 0.2.0
        var found = await updater.FindUpdateAsync(V("0.1.0-alpha.8"));
        Assert.Equal("v0.2.0", found!.Tag);

        // контрольная сумма — из файла рядом с архивом
        var downloads = Path.Combine(_root, "dl-feed");
        var path = await updater.DownloadAsync(found, downloads);
        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        badSum = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAsync(found, downloads));
    }
}
