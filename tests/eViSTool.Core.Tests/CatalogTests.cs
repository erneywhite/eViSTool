using System.Net;
using System.Text;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using Newtonsoft.Json;

namespace eViSTool.Core.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"evistool-catalog-{Guid.NewGuid():N}.json");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"evistool-catalog-{Guid.NewGuid():N}");

    public void Dispose()
    {
        File.Delete(_cache);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private CatalogService Service(params ModDbListItem[] items)
    {
        File.WriteAllText(_cache, Newtonsoft.Json.JsonConvert.SerializeObject(items));
        var s = new CatalogService(new ModDbClient(), _cache);
        s.LoadAsync().GetAwaiter().GetResult(); // свежий кэш — в сеть не ходит
        return s;
    }

    private static ModDbListItem Item(string name, string id, string side = "both", long downloads = 0, long trending = 0, params string[] tags) =>
        new() { Name = name, ModIdStrs = [id], Side = side, Downloads = downloads, TrendingPoints = trending, Tags = tags.Cast<string?>().ToList(), Summary = $"{name} summary" };

    [Fact]
    public void SearchesByAllWordsInNameSummaryAndId()
    {
        var s = Service(Item("Carry On", "carryon"), Item("Carry Capacity", "carrycap"), Item("Butchering", "butchering"));
        Assert.Equal(["Carry On"], s.Search(new CatalogQuery { Text = "carry on" }).Select(i => i.Name));
        Assert.Single(s.Search(new CatalogQuery { Text = "BUTCH" }));
    }

    [Fact]
    public void FiltersByTagAndSideAndSorts()
    {
        var s = Service(
            Item("A", "a", "client", 10, 1, "QoL"),
            Item("B", "b", "server", 30, 5, "QoL"),
            Item("C", "c", "both", 20, 9, "Tweak"));

        Assert.Equal(["A", "B"], s.Search(new CatalogQuery { Tag = "qol", Sort = CatalogSort.Name }).Select(i => i.Name));
        // «both» подходит серверу
        Assert.Equal(["B", "C"], s.Search(new CatalogQuery { Side = "server", Sort = CatalogSort.Name }).Select(i => i.Name));
        Assert.Equal(["B", "C", "A"], s.Search(new CatalogQuery { Sort = CatalogSort.Downloads }).Select(i => i.Name));
        Assert.Equal(["C", "B", "A"], s.Search(new CatalogQuery()).Select(i => i.Name)); // по популярности
        Assert.Equal(["QoL", "Tweak"], s.Tags());
    }

    /// <summary>Модбаза отвечает HTTP 200, но телом не того вида: страница техработ или ответ с полем другого типа.</summary>
    private sealed class BrokenModDb(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") });
    }

    private static ModDbClient Broken(string body = "<html><body>Site maintenance</body></html>") =>
        new(new HttpClient(new BrokenModDb(body)) { BaseAddress = new Uri(ModDbClient.ApiBase) });

    // битый ответ — та же ошибка сети, что недоступный сайт: её ловят все, кто ходит в модбазу (раньше JsonException
    // проходил мимо их фильтров, и статус каталога застревал на «Загружаю каталог…»)
    [Theory]
    [InlineData("<html><body>Site maintenance</body></html>")]
    [InlineData("""{"statuscode":"200","mod":[1,2],"mods":"x","gameversions":"x"}""")]
    [InlineData("""{"statuscode":"200","mods":[{"modid":1""")]
    public async Task BrokenReply_IsAnHttpError_ForEveryRequest(string body)
    {
        using var db = Broken(body);
        foreach (var call in new Func<Task>[]
                 {
                     () => db.GetModAsync("alpha"), () => db.GetAllModsAsync(), () => db.GetModsForGameVersionsAsync([1]),
                     () => db.GetGameVersionsAsync(),
                 })
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(call);
            Assert.Equal(Loc.T("moddb.badReply"), ex.Message);
            Assert.IsAssignableFrom<JsonException>(ex.InnerException);
        }
        Assert.Equal(Loc.T("moddb.badReply"), (await db.GetModsAsync(["alpha"]))["alpha"].Error); // проверка модов: «не проверен» с причиной
    }

    // модбаза на техработах — каталог, ветки игры и моды для ветки берутся из старого кэша, как без сети
    [Fact]
    public async Task BrokenReply_FallsBackToTheOldCache()
    {
        Directory.CreateDirectory(_dir);
        var cache = Path.Combine(_dir, "catalog.json");
        File.WriteAllText(cache, JsonConvert.SerializeObject(new[] { Item("Carry On", "carryon") }));
        File.WriteAllText(Path.Combine(_dir, "gameversions.json"), JsonConvert.SerializeObject(new[] { new ModDbTag { TagId = 7, Name = "1.22.7" } }));
        File.WriteAllText(Path.Combine(_dir, "compat-1.22.json"), "[5]");
        var s = new CatalogService(Broken(), cache);

        await s.LoadAsync(force: true);
        await s.LoadBranchesAsync(force: true);

        Assert.Equal(["Carry On"], s.Items.Select(i => i.Name));
        Assert.Equal(["1.22"], s.Branches);
        Assert.Equal([5L], await s.CompatibleAssetsAsync("1.22", force: true));
    }
}
