using eViSTool.Core.Versioning;
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

    /// <summary>Только эти моды (assetid) — например, у которых есть релиз для выбранной ветки игры.</summary>
    public IReadOnlySet<long>? OnlyAssets { get; init; }
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

    /// <summary>Ветки игры (1.22, 1.21, …) — новые сверху.</summary>
    public IReadOnlyList<string> Branches { get; private set; } = [];

    private IReadOnlyList<ModDbTag> _gameVersions = [];
    private readonly Dictionary<string, IReadOnlySet<long>> _compat = [];

    /// <summary>Каталог из кэша, если он свежий, иначе из сети. force — всегда из сети.</summary>
    public async Task LoadAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && TryReadCache(out var cached, out var at) && DateTime.UtcNow - at < MaxAge)
        {
            Items = cached;
            LoadedAt = at;
            return;
        }

        if (force) _compat.Clear();
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

    /// <summary>Версии игры из модбазы и ветки из них (кэш на диске, как у каталога).</summary>
    public async Task LoadBranchesAsync(bool force = false, CancellationToken ct = default)
    {
        _gameVersions = await Cached("gameversions.json", force, () => db.GetGameVersionsAsync(ct)).ConfigureAwait(false);
        Branches = _gameVersions
            .Select(v => ModVersion.ParseOrNull(v.Name))
            .Where(v => v is not null)
            .Select(v => (v!.Major, v.Minor))
            .Distinct()
            .OrderByDescending(b => b.Major).ThenByDescending(b => b.Minor)
            .Select(b => $"{b.Major}.{b.Minor}")
            .ToList();
    }

    /// <summary>assetid модов, у которых есть релиз для ветки (например, "1.22" — любой 1.22.x, включая rc/pre).</summary>
    public async Task<IReadOnlySet<long>> CompatibleAssetsAsync(string branch, bool force = false, CancellationToken ct = default)
    {
        if (!force && _compat.TryGetValue(branch, out var known)) return known;

        var tags = _gameVersions
            .Where(v => ModVersion.ParseOrNull(v.Name) is { } ver && $"{ver.Major}.{ver.Minor}" == branch)
            .Select(v => v.TagId)
            .ToList();
        var mods = await Cached<long>($"compat-{branch}.json", force,
            async () => (await db.GetModsForGameVersionsAsync(tags, ct).ConfigureAwait(false)).Select(m => m.AssetId).ToList())
            .ConfigureAwait(false);
        return _compat[branch] = mods.ToHashSet();
    }

    /// <summary>Значение из файлового кэша, если он свежий, иначе из сети (при ошибке сети — устаревший кэш).</summary>
    private async Task<List<T>> Cached<T>(string name, bool force, Func<Task<IReadOnlyList<T>>> fetch)
    {
        var path = Path.Combine(Path.GetDirectoryName(_cacheFile)!, name);
        List<T>? stale = null;
        try
        {
            if (File.Exists(path))
            {
                stale = JsonConvert.DeserializeObject<List<T>>(File.ReadAllText(path));
                if (!force && stale is { Count: > 0 } && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MaxAge) return stale;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }

        try
        {
            var fresh = (await fetch().ConfigureAwait(false)).ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonConvert.SerializeObject(fresh));
            }
            catch (IOException) { }
            return fresh;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && stale is { Count: > 0 })
        {
            return stale;
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
        if (q.OnlyAssets is not null)
            items = items.Where(i => q.OnlyAssets.Contains(i.AssetId));
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
