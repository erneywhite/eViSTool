using System.Net;
using System.Reflection;
using eViSTool.Core.Localization;
using Newtonsoft.Json;

namespace eViSTool.Core.ModDb;

/// <summary>Клиент API модбазы mods.vintagestory.at.</summary>
public sealed class ModDbClient : IDisposable
{
    public const string ApiBase = "https://mods.vintagestory.at/api/";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ModDbClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(ApiBase);

        var version = typeof(ModDbClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"eViSTool/{version.Split('+')[0]} (github: erneywhite/eViSTool)");
    }

    /// <summary>Мод по строковому modid или числовому id. null — если в модбазе такого нет.</summary>
    public async Task<ModDbMod?> GetModAsync(string modId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"mod/{Uri.EscapeDataString(modId)}", ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonConvert.DeserializeObject<ModDbModResponse>(json);
        // API отвечает HTTP 200 с {"statuscode":"404"}, если мода нет
        return data?.StatusCode == "200" ? data.Mod : null;
    }

    /// <summary>Параллельно запрашивает несколько модов (не больше <paramref name="parallelism"/> одновременно).</summary>
    public async Task<IReadOnlyDictionary<string, ModDbResult>> GetModsAsync(
        IEnumerable<string> modIds, int parallelism = 8, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var ids = modIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new Dictionary<string, ModDbResult>(StringComparer.OrdinalIgnoreCase);
        var done = 0;

        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (id, token) =>
            {
                ModDbResult r;
                try
                {
                    r = new ModDbResult(await GetModAsync(id, token).ConfigureAwait(false), null);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !token.IsCancellationRequested)
                {
                    r = new ModDbResult(null, ex.Message);
                }
                lock (results) results[id] = r;
                progress?.Report(Interlocked.Increment(ref done));
            }).ConfigureAwait(false);

        return results;
    }

    /// <summary>Весь каталог модов (~4 МБ).</summary>
    public async Task<IReadOnlyList<ModDbListItem>> GetAllModsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync("mods", ct).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<ModDbListResponse>(json)?.Mods ?? [];
    }

    /// <summary>
    /// Моды, у которых есть релиз с любым из этих тегов версий игры. Фильтр делает сам сайт
    /// (gameversions[]=…) — одним запросом вместо запроса на каждый мод.
    /// </summary>
    public async Task<IReadOnlyList<ModDbListItem>> GetModsForGameVersionsAsync(IEnumerable<long> tagIds, CancellationToken ct = default)
    {
        var query = string.Join("&", tagIds.Select(t => $"gameversions[]={t}"));
        if (query.Length == 0) return [];
        var json = await _http.GetStringAsync($"mods?{query}", ct).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<ModDbListResponse>(json)?.Mods ?? [];
    }

    public async Task<IReadOnlyList<ModDbTag>> GetGameVersionsAsync(CancellationToken ct = default)
    {
        var json = await _http.GetStringAsync("gameversions", ct).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<ModDbGameVersionsResponse>(json)?.GameVersions ?? [];
    }

    /// <summary>Скачивает файл (архив мода с CDN модбазы). Пишет во временный файл и переименовывает в конце.</summary>
    /// <summary>Сколько ждать следующего куска файла: дольше — скачивание считается зависшим.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Файлы модов модбаза отдаёт с https://*.vintagestory.at (сейчас moddbcdn). Адрес приходит из её ответа, но
    /// качать по нему что угодно откуда угодно (http, чужой хост) незачем — такой адрес отклоняем.
    /// </summary>
    public static bool IsModDbFileUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("vintagestory.at", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".vintagestory.at", StringComparison.OrdinalIgnoreCase));

    public async Task DownloadAsync(string url, string destPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!IsModDbFileUrl(url)) throw new InvalidDataException(Loc.T("dl.badUrl", url));
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var tmp = destPath + ".part";
        // тайм-аут HttpClient покрывает только заголовки: сервер, замолчавший посреди файла, держал бы установку вечно
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    if ((n = await src.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) <= 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total.Value);
                }
            }
            File.Move(tmp, destPath, overwrite: true);
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryDelete(tmp);
            throw new TimeoutException(Loc.T("dl.stalled", (int)StallTimeout.TotalSeconds));
        }
        catch
        {
            TryDelete(tmp); // недокачанный файл не оставляем
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>Результат запроса одного мода: сам мод (null — не найден) или ошибка сети.</summary>
public sealed record ModDbResult(ModDbMod? Mod, string? Error);
