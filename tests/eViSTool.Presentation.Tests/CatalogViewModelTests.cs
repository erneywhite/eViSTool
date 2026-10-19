using System.Net;
using System.Text;
using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Core.Tests.Hosting;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;
using Newtonsoft.Json;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// «Каталог»: ответы модбазы, пришедшие после того, как условия поменялись (другой фильтр по версии, другой профиль),
/// не перекрывают новые, а карточка мода подбирает релиз под ту установленную версию, что в профиле сейчас. Модбаза
/// поддельная: каталог из двух модов, alpha с релизом для 1.22, beta — для 1.21; ответ «какие моды есть для ветки»
/// можно придержать. Кэш каталога — в папке data рядом с тестами (без него в сеть никто не ходит).
/// </summary>
public sealed class CatalogViewModelTests : IDisposable
{
    private static readonly string FakeBin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "tests", "FakeVsServer", "bin", "Debug", "net10.0"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-catalogvm-" + Guid.NewGuid().ToString("N"));
    private readonly IAppWindows? _oldWindows = AppWindows.IsSet ? AppWindows.Current : null;
    private (MainViewModel Main, bool AutoCheck)? _main;

    /// <summary>Кэш каталога рядом с тестами можно чистить: иначе тест ничего не делает — данные пользователя не трогаем.</summary>
    private static bool CanRun => AppPaths.IsPortable;

    public CatalogViewModelTests()
    {
        if (CanRun) ClearCatalogCache();
    }

    public void Dispose()
    {
        if (_main is var (main, autoCheck)) main.AutoCheckUpdates = autoCheck; // настройка пишется в settings.json тестов
        AppWindows.Current = _oldWindows!;
        if (CanRun) ClearCatalogCache();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static void ClearCatalogCache()
    {
        if (!Directory.Exists(AppPaths.Cache)) return;
        foreach (var file in Directory.EnumerateFiles(AppPaths.Cache)
                     .Where(f => Path.GetFileName(f) is "catalog.json" or "gameversions.json" || Path.GetFileName(f).StartsWith("compat-", StringComparison.Ordinal)))
            File.Delete(file);
    }

    /// <summary>Модбаза: каталог alpha (1.22) и beta (1.21); мод alpha с релизами <see cref="Releases"/>.</summary>
    private sealed class FakeModDb : HttpMessageHandler
    {
        private static readonly Dictionary<long, long> AssetByTag = new() { [122] = 1, [121] = 2 };

        /// <summary>Ответ для ветки с этим тегом ждёт: true — отдать, false — сеть упала.</summary>
        public Dictionary<long, TaskCompletionSource<bool>> Holds { get; } = [];
        public List<(string ModVersion, string Game)> Releases { get; } = [];
        public int Asked121;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path == "/api/gameversions")
                return Json(new { statuscode = "200", gameversions = new[] { new { tagid = 122, name = "1.22.7" }, new { tagid = 121, name = "1.21.5" } } });
            if (path == "/api/mods" && query.Contains("gameversions[]=", StringComparison.Ordinal))
            {
                var tag = long.Parse(query[(query.IndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                if (tag == 121) Interlocked.Increment(ref Asked121);
                if (Holds.TryGetValue(tag, out var hold) && !await hold.Task.WaitAsync(ct))
                    throw new HttpRequestException("сеть упала");
                return Json(new { statuscode = "200", mods = new[] { new { assetid = AssetByTag[tag], modid = AssetByTag[tag] } } });
            }
            if (path == "/api/mods")
                return Json(new
                {
                    statuscode = "200",
                    mods = new[]
                    {
                        new { modid = 1, assetid = 1, name = "Alpha", modidstrs = new[] { "alpha" }, side = "both" },
                        new { modid = 2, assetid = 2, name = "Beta", modidstrs = new[] { "beta" }, side = "both" },
                    },
                });
            if (path == "/api/mod/1" && Releases.Count > 0)
                return Json(new
                {
                    statuscode = "200",
                    mod = new
                    {
                        modid = 1, assetid = 1, name = "Alpha", side = "both",
                        releases = Releases.Select(r => new
                        {
                            modidstr = "alpha", modversion = r.ModVersion, tags = new[] { r.Game },
                            mainfile = $"https://moddbcdn.vintagestory.at/files/alpha_{r.ModVersion}.zip",
                        }),
                    },
                });
            if (path.StartsWith("/api/mod/", StringComparison.Ordinal)) return Json(new { statuscode = "404" });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(o), Encoding.UTF8, "application/json") };
    }

    private static ModDbClient Client(FakeModDb db) => new(new HttpClient(db) { BaseAddress = new Uri(ModDbClient.ApiBase) });

    /// <summary>Свой клиентский профиль с игрой 1.22.7 и без модов.</summary>
    private ProfileViewModel ClientProfile(string name)
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        if (!File.Exists(Path.Combine(game, "VintagestoryServer.dll")))
            File.Copy(Path.Combine(FakeBin, "FakeVsServer.dll"), Path.Combine(game, "VintagestoryServer.dll"));
        var data = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        Directory.CreateDirectory(Path.Combine(data, "Mods"));
        File.WriteAllText(Path.Combine(data, "clientsettings.json"), """{ "stringListSettings": { "disabledMods": [] } }""");
        var model = new GameProfile
        {
            Id = name + "-" + Guid.NewGuid().ToString("N")[..6], Name = name, Kind = ProfileKind.Client, GameDir = game, DataDir = data,
        };
        return new ProfileViewModel(model, _ => { });
    }

    /// <summary>Главная модель с поддельной модбазой и данными профилями, без автопроверки; открыт первый.</summary>
    private MainViewModel Main(FakeModDb db, params ProfileViewModel[] profiles)
    {
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var main = new MainViewModel(Client(db));
        Loc.Instance.SetLanguage(language);
        _main = (main, main.AutoCheckUpdates);
        main.AutoCheckUpdates = false;
        main.Profiles.Clear(); // профили пользователя из настроек тесту не нужны
        foreach (var p in profiles) main.Profiles.Add(p);
        main.ActiveProfile = profiles[0];
        return main;
    }

    /// <summary>Каталог открыт, версия игры профиля известна; фильтр по версии — «любая», чтобы видеть оба мода.</summary>
    private static CatalogViewModel OpenCatalog(MainViewModel main)
    {
        Sta.WaitFor(() => main.Mods.GameVersion is not null);
        var catalog = main.Catalog;
        Sta.Wait(catalog.EnsureLoadedAsync());
        catalog.SelectedBranch = catalog.Branches[0];
        return catalog;
    }

    private static string? Mark(CatalogViewModel catalog, long asset) => catalog.Results.Single(r => r.Item.AssetId == asset).NoVersionText;

    /// <summary>Покрутить очередь окна ещё немного: опоздавший ответ модбазы успел бы дойти до списка.</summary>
    private static void Settle()
    {
        for (var i = 0; i < 30; i++)
        {
            UiThread.Current!.RunPending();
            Thread.Sleep(10);
        }
    }

    // фильтр «есть версия для 1.21»: модбаза думает, а фильтр уже сняли — опоздавшая ошибка сверки не перекрывает
    // статус нового поиска (раньше писала «Не удалось получить моды для 1.21.x» под списком для любой версии)
    [Fact]
    public void FailedBranchMatch_ForAFilterAlreadyChanged_DoesNotOverwriteTheStatus() => Sta.Run(() =>
    {
        if (!CanRun) return;
        var db = new FakeModDb();
        var hold = db.Holds[121] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = OpenCatalog(Main(db, ClientProfile("a")));

        catalog.SelectedBranch = catalog.Branches.Single(b => b.Value == "1.21");
        Sta.WaitFor(() => db.Asked121 > 0);
        Assert.Equal(Loc.T("catalog.matchingBranch", "1.21"), catalog.StatusText);
        catalog.SelectedBranch = catalog.Branches[0];
        var found = catalog.StatusText;
        Assert.Equal(2, catalog.Results.Count);

        hold.SetResult(false);
        Settle();

        Assert.Equal(found, catalog.StatusText);
        Assert.Equal(2, catalog.Results.Count);
    });

    // профиль 1.22 → сервер 1.21 (модбаза думает) → снова 1.22 (ответ из памяти): опоздавший ответ для 1.21 не ставит
    // свои пометки под подписью 1.22.x
    [Fact]
    public void LateBranchAnswer_ForAProfileAlreadySwitchedAway_DoesNotReplaceTheMarks() => Sta.Run(() =>
    {
        if (!CanRun) return;
        using var host = new HostedFixture();
        host.GPortalLayout(versionFile: false);
        host.Server.Put("/version.txt", "linux-x64_1.21.5");
        var db = new FakeModDb();
        var hold = db.Holds[121] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, hosted) = (ClientProfile("a"), new ProfileViewModel(host.Profile, _ => { }));
        var main = Main(db, client, hosted);
        var catalog = OpenCatalog(main);
        var noFor122 = Loc.T("catalog.noVersionFor", "1.22.x");
        Assert.Null(Mark(catalog, 1));
        Assert.Equal(noFor122, Mark(catalog, 2));

        main.ActiveProfile = hosted;
        Sta.WaitFor(() => db.Asked121 > 0); // версия сервера прочитана, пометки под 1.21 ждут модбазу
        main.ActiveProfile = client;
        Sta.WaitFor(() => main.Mods.GameVersion?.ToString() == "1.22.7" && Mark(catalog, 2) == noFor122);

        hold.SetResult(true);
        Settle();

        Assert.Null(Mark(catalog, 1));
        Assert.Equal(noFor122, Mark(catalog, 2));
    });

    // карточка открыта, а установленная версия поменялась (другой профиль с той же игрой, пре-релиз через «Версии»):
    // лучший релиз подбирается заново — стоит пре-релиз, предлагаются и пре-релизы, не стоит ничего — стабильный
    [Fact]
    public void Card_PicksTheBestRelease_ForTheVersionInstalledNow() => Sta.Run(() =>
    {
        var db = new FakeModDb { Releases = { ("1.9.0", "1.22.7"), ("2.0.0-rc.1", "1.22.7"), ("2.0.0-rc.3", "1.22.7") } };
        using var client = Client(db);
        var item = new ModDbListItem { ModId = 1, AssetId = 1, Name = "Alpha", ModIdStrs = ["alpha"] };
        var card = new ModDetailsViewModel(item, ModVersion.ParseOrNull("1.22.7"), new Dictionary<string, string> { ["alpha"] = "2.0.0-rc.1" }, null!);
        Sta.Wait(card.LoadAsync(client, CancellationToken.None));
        Assert.Equal("2.0.0-rc.3", card.BestRelease?.ModVersion);

        card.RefreshInstalled(new Dictionary<string, string>()); // профиль, где мода нет
        Assert.Equal("1.9.0", card.BestRelease?.ModVersion);
        Assert.Equal(Loc.T("card.install", "1.9.0"), card.InstallText);

        card.RefreshInstalled(new Dictionary<string, string> { ["alpha"] = "2.0.0-rc.1" }); // поставили пре-релиз
        Assert.Equal("2.0.0-rc.3", card.BestRelease?.ModVersion);
        Assert.Equal(Loc.T("card.update", "2.0.0-rc.1", "2.0.0-rc.3"), card.InstallText);
        Assert.True(card.CanInstall);
    });
}
