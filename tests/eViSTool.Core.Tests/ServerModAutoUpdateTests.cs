using System.IO.Compression;
using System.Net;
using System.Text;
using eViSTool.Core.Game;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Server;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests;

/// <summary>Обновление модов сервера перед перезапуском по расписанию — против поддельной модбазы.</summary>
public sealed class ServerModAutoUpdateTests : IDisposable
{

    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-autoupd-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly string _data;
    private readonly string _mods;
    private readonly string _branch;

    public ServerModAutoUpdateTests()
    {
        // «папка игры» — поддельный сервер под именем настоящего: версия игры берётся из его файла
        _game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        FakeServer.InstallAs(_game);
        var game = GameInstall.DetectVersion(_game)!;
        _branch = $"{game.Major}.{game.Minor}";
        _data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        _mods = Directory.CreateDirectory(Path.Combine(_data, "Mods")).FullName;
        File.WriteAllText(Path.Combine(_data, "serverconfig.json"),
            "{ \"ModPaths\": [\"Mods\", " + JsonConvert.ToString(_mods) + "], \"WorldConfig\": { \"DisabledMods\": [\"sleepy\"] } }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static byte[] Zip(string modId, string version, string? needsGame = null)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var deps = needsGame is null ? "" : $", \"dependencies\": {{ \"game\": \"{needsGame}\" }}";
            using var w = new StreamWriter(z.CreateEntry("modinfo.json").Open());
            w.Write($"{{ \"type\": \"code\", \"modid\": \"{modId}\", \"name\": \"{modId}\", \"version\": \"{version}\", \"side\": \"universal\"{deps} }}");
        }
        return ms.ToArray();
    }

    private void Local(string modId, string version) => File.WriteAllBytes(Path.Combine(_mods, $"{modId}_{version}.zip"), Zip(modId, version));

    /// <summary>Поддельная модбаза: /mod/&lt;id&gt; — релизы, /files/&lt;имя&gt; — архивы, /mods — пустой каталог.</summary>
    private sealed class FakeModDb : HttpMessageHandler
    {
        public Dictionary<string, (string Version, byte[] File)> Latest { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Branch { get; init; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/mod/"))
            {
                var id = Uri.UnescapeDataString(path["/api/mod/".Length..]);
                if (!Latest.TryGetValue(id, out var rel)) return Json(new { statuscode = "404" });
                return Json(new
                {
                    statuscode = "200",
                    mod = new
                    {
                        modid = 1, name = id, urlalias = id,
                        releases = new[]
                        {
                            new { releaseid = 2, modidstr = id, modversion = rel.Version, mainfile = $"https://fake/files/{id}_{rel.Version}.zip",
                                  filename = $"{id}_{rel.Version}.zip", tags = new[] { $"v{Branch}.0" } },
                        },
                    },
                });
            }
            if (path.StartsWith("/files/"))
            {
                var name = path["/files/".Length..];
                var rel = Latest.Values.First(v => name.EndsWith($"_{v.Version}.zip"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(rel.File) });
            }
            if (path == "/api/mods") return Json(new { statuscode = "200", mods = Array.Empty<object>() });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(object o) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(o), Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task UpdatesWhatIsDue_KeepsPinnedAndDisabled_SkipsNewerGame()
    {
        Local("alpha", "1.0.0");   // обновится
        Local("pinned", "1.0.0");  // закреплён — не трогать
        Local("future", "1.0.0");  // новому релизу нужна игра новее
        Local("sleepy", "1.0.0");  // выключен — обновится, но останется выключенным

        var fake = new FakeModDb { Branch = _branch };
        fake.Latest["alpha"] = ("1.1.0", Zip("alpha", "1.1.0"));
        fake.Latest["pinned"] = ("2.0.0", Zip("pinned", "2.0.0"));
        fake.Latest["future"] = ("1.5.0", Zip("future", "1.5.0", needsGame: $"{_branch}.999"));
        fake.Latest["sleepy"] = ("1.2.0", Zip("sleepy", "1.2.0"));

        using var db = new ModDbClient(new HttpClient(fake) { BaseAddress = new Uri(ModDbClient.ApiBase) });
        var updater = new ModUpdater(db, Path.Combine(_root, "dl"));
        var mods = new ServerMods("srv-autoupd-" + Guid.NewGuid().ToString("N")[..6], _game, _data);
        var policy = new ModPolicy(new Dictionary<string, string> { ["pinned"] = "1.0.0" }, new Dictionary<string, List<string>>());

        var result = await mods.UpdateAllAsync(policy, allowUnstable: false, updater, db);

        Assert.Equal(["alpha: 1.0.0 → 1.1.0", "sleepy: 1.0.0 → 1.2.0"], result.Updated.Order());
        Assert.Contains(result.Skipped, s => s.Contains("future"));
        Assert.Empty(result.Failed);

        var now = ModUpdateService.ScanLocal(mods.Resolve()).ToDictionary(m => m.Info!.ModId, m => m);
        Assert.Equal("1.1.0", now["alpha"].Info!.Version);
        Assert.Equal("1.0.0", now["pinned"].Info!.Version);
        Assert.Equal("1.0.0", now["future"].Info!.Version);
        Assert.Equal("1.2.0", now["sleepy"].Info!.Version);
        Assert.Contains("sleepy", mods.Resolve().DisabledMods);
    }

    [Fact]
    public async Task ModDbDown_NothingChanges()
    {
        Local("alpha", "1.0.0");
        using var db = new ModDbClient(new HttpClient(new DownHandler()) { BaseAddress = new Uri(ModDbClient.ApiBase) });
        var mods = new ServerMods("srv-autoupd-down", _game, _data);

        var result = await mods.UpdateAllAsync(ModPolicy.Empty, false, new ModUpdater(db, Path.Combine(_root, "dl")), db);

        Assert.Empty(result.Updated);
        Assert.True(File.Exists(Path.Combine(_mods, "alpha_1.0.0.zip")));
    }

    private sealed class DownHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("нет сети");
    }
}
