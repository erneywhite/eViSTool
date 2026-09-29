using eViSTool.Core.ModDb;

namespace eViSTool.Core.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"evistool-catalog-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_cache);

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
}
