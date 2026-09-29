using Newtonsoft.Json;

namespace eViSTool.Core.ModDb;

public enum CatalogSort
{
    Trending,
    Downloads,
    Follows,
    RecentlyUpdated,
    Name,
}

/// <summary>Условия поиска по каталогу.</summary>
public sealed record CatalogQuery
{
    public string Text { get; init; } = "";
    public string? Tag { get; init; }
    /// <summary>client / server / both, null — любая.</summary>
    public string? Side { get; init; }
    public CatalogSort Sort { get; init; } = CatalogSort.Trending;
}

/// <summary>
/// Каталог модбазы: весь список (~9 тыс. модов, ~4 МБ) кэшируется на диск и ищется локально —
/// так поиск мгновенный и не долбит сайт на каждую букву.
/// </summary>
public sealed class CatalogService(ModDbClient db, string? cacheFile = null)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    private readonly string _cacheFile = cacheFile ?? Path.Combine(AppPaths.Cache, "catalog.json");

    public IReadOnlyList<ModDbListItem> Items { get; private set; } = [];
    public DateTime? LoadedAt { get; private set; }

    /// <summary>Каталог из кэша, если он свежий, иначе из сети. force — всегда из сети.</summary>
    public async Task LoadAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && TryReadCache(out var cached, out var at) && DateTime.UtcNow - at < MaxAge)
        {
            Items = cached;
            LoadedAt = at;
            return;
        }

        try
        {
            Items = await db.GetAllModsAsync(ct).ConfigureAwait(false);
            LoadedAt = DateTime.UtcNow;
            WriteCache();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // сети нет — лучше старый каталог, чем никакого
            if (!TryReadCache(out var stale, out var staleAt)) throw;
            Items = stale;
            LoadedAt = staleAt;
        }
    }

    /// <summary>Все теги каталога по алфавиту.</summary>
    public IReadOnlyList<string> Tags() =>
        Items.SelectMany(i => i.Tags).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<ModDbListItem> Search(CatalogQuery q)
    {
        IEnumerable<ModDbListItem> items = Items;

        var words = q.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 0)
            items = items.Where(i => words.All(w => Matches(i, w)));
        if (!string.IsNullOrEmpty(q.Tag))
            items = items.Where(i => i.Tags.Any(t => string.Equals(t, q.Tag, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrEmpty(q.Side))
            // мод «both» подходит и клиенту, и серверу
            items = items.Where(i => string.Equals(i.Side, q.Side, StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(i.Side, "both", StringComparison.OrdinalIgnoreCase));

        items = q.Sort switch
        {
            CatalogSort.Downloads => items.OrderByDescending(i => i.Downloads),
            CatalogSort.Follows => items.OrderByDescending(i => i.Follows),
            CatalogSort.RecentlyUpdated => items.OrderByDescending(i => i.LastReleased, StringComparer.Ordinal), // формат yyyy-MM-dd HH:mm:ss сортируется как строка
            CatalogSort.Name => items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(i => i.TrendingPoints).ThenByDescending(i => i.Downloads),
        };
        return items.ToList();
    }

    private static bool Matches(ModDbListItem i, string word) =>
        Contains(i.Name, word) || Contains(i.Summary, word) || Contains(i.Author, word)
        || i.ModIdStrs.Any(s => Contains(s, word));

    private static bool Contains(string? s, string word) => s?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    private bool TryReadCache(out IReadOnlyList<ModDbListItem> items, out DateTime at)
    {
        items = [];
        at = default;
        try
        {
            if (!File.Exists(_cacheFile)) return false;
            items = JsonConvert.DeserializeObject<List<ModDbListItem>>(File.ReadAllText(_cacheFile)) ?? [];
            at = File.GetLastWriteTimeUtc(_cacheFile);
            return items.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false;
        }
    }

    private void WriteCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            File.WriteAllText(_cacheFile, JsonConvert.SerializeObject(Items));
        }
        catch (IOException)
        {
            // без кэша тоже работаем
        }
    }
}
