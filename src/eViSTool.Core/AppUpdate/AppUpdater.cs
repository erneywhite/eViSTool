using System.IO.Compression;
using System.Net;
using System.Xml.Linq;
using System.Security.Cryptography;
using eViSTool.Core.Localization;
using eViSTool.Core.Versioning;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.AppUpdate;

/// <summary>Релиз eViSTool на GitHub с zip-архивом программы.</summary>
public sealed record AppRelease
{
    public required ModVersion Version { get; init; }
    public required string Tag { get; init; }
    public bool Prerelease { get; init; }

    /// <summary>Страница релиза — «что нового».</summary>
    public string PageUrl { get; init; } = "";
    public string Notes { get; init; } = "";

    public required string AssetName { get; init; }
    public required string AssetUrl { get; init; }
    public long AssetSize { get; init; }

    /// <summary>SHA-256 архива в hex (из отпечатка, который GitHub считает сам при загрузке файла); null — неизвестен.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Файл с контрольной суммой рядом с архивом («….zip.sha256») — когда отпечатка от API нет.</summary>
    public string? ChecksumUrl { get; init; }
}

/// <summary>
/// Обновление программы из релизов GitHub без установщика. Работающий exe нельзя перезаписать, но можно переименовать:
/// старые файлы становятся «*.old», новые встают на их место, окно перезапускается. Запущенный агент при этом
/// продолжает работать со старым файлом — сервер не останавливается; новый агент возьмётся при следующем запуске.
/// Хвосты «*.old» убираются при следующем старте (<see cref="CleanupOld"/>).
/// </summary>
public sealed class AppUpdater
{
    public const string Repo = "erneywhite/eViSTool";

    /// <summary>Архив релиза для этой системы: на Windows — zip с окном и агентом, на Linux — tar.gz с одним агентом.</summary>
    public static string AssetSuffix { get; } = OperatingSystem.IsWindows() ? "-win-x64.zip" : "-linux-x64.tar.gz";

    /// <summary>Главная программа в архиве — по ней узнаём архив eViSTool. На Linux окна нет, главная там — агент.</summary>
    public static string MainExe { get; } = OperatingSystem.IsWindows() ? "eViSTool.exe" : Server.AgentProtocol.ExeName;

    private readonly HttpClient _http;

    public AppUpdater(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"eViSTool (github: {Repo})"); // без User-Agent GitHub отвечает 403
    }

    /// <summary>
    /// Список релизов — API GitHub. Переменная EVISTOOL_RELEASES_URL подменяет адрес (такой же JSON со ссылками на архивы):
    /// так «Обновить там» проверяется на своей машине без настоящего релиза.
    /// </summary>
    private static string ReleasesUrl =>
        Environment.GetEnvironmentVariable("EVISTOOL_RELEASES_URL") is { Length: > 0 } url ? url : $"https://api.github.com/repos/{Repo}/releases?per_page=30";

    /// <summary>Новее ли что-то на GitHub. null — обновлений нет.</summary>
    public async Task<AppRelease?> FindUpdateAsync(ModVersion current, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        // API без входа — 60 запросов в час на внешний адрес (у всех за одним роутером он общий): исчерпали — берём ленту
        // релизов с самого github.com, у неё такого лимита нет
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return PickUpdate(await FeedReleasesAsync(ct).ConfigureAwait(false), current);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(Loc.T("update.httpFailed", (int)response.StatusCode));
        return PickUpdate(ParseReleases(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)), current);
    }

    /// <summary>
    /// Релиз ровно этой версии — агент на другом компьютере обновляется до версии окна, а не до самой свежей.
    /// null — такого релиза на GitHub нет (например, окно собрано из исходников).
    /// </summary>
    public async Task<AppRelease?> FindReleaseAsync(ModVersion version, CancellationToken ct = default)
    {
        IReadOnlyList<AppRelease> releases;
        using (var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl))
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                releases = await FeedReleasesAsync(ct).ConfigureAwait(false);
            else if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(Loc.T("update.httpFailed", (int)response.StatusCode));
            else
                releases = ParseReleases(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        return releases.FirstOrDefault(r => r.Version.ToString() == version.ToString());
    }

    /// <summary>
    /// Параметры для новой копии агента после обновления: прежние, без «--start» и «--after-update», плюс «--after-update»
    /// (новая копия подождёт, пока прежняя освободит профиль) и «--start», если сервер работал.
    /// </summary>
    public static IReadOnlyList<string> RelaunchArgs(IEnumerable<string> current, bool startServer)
    {
        var args = current.Where(a => a is not ("--start" or "--after-update")).ToList();
        args.Add("--after-update");
        if (startServer) args.Add("--start");
        return args;
    }

    /// <summary>Релизы из ленты github.com (Atom): версии и ссылки. Архив и его контрольная сумма — по прямым ссылкам.</summary>
    public async Task<IReadOnlyList<AppRelease>> FeedReleasesAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"https://github.com/{Repo}/releases.atom", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(Loc.T("update.rateLimited"));
        return ParseFeed(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Лента релизов → релизы. Черновиков в ленте нет; альфа/бета/rc — по версии.</summary>
    public static IReadOnlyList<AppRelease> ParseFeed(string xml)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var result = new List<AppRelease>();
        foreach (var entry in XDocument.Parse(xml).Root?.Elements(atom + "entry") ?? [])
        {
            var page = entry.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value ?? "";
            var at = page.LastIndexOf("/releases/tag/", StringComparison.Ordinal);
            if (at < 0) continue;
            var tag = Uri.UnescapeDataString(page[(at + "/releases/tag/".Length)..]);
            if (!ModVersion.TryParse(tag.TrimStart('v', 'V'), out var version)) continue;
            var asset = $"eViSTool-{tag.TrimStart('v', 'V')}{AssetSuffix}";
            var url = $"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{asset}";
            result.Add(new AppRelease
            {
                Version = version,
                Tag = tag,
                Prerelease = version.IsPrerelease,
                PageUrl = page,
                AssetName = asset,
                AssetUrl = url,
                ChecksumUrl = url + ".sha256",
            });
        }
        return result;
    }

    /// <summary>
    /// Самый новый релиз новее текущей версии. Альфы и беты предлагаются только тем, кто сам стоит на альфе или бете:
    /// со стабильной версии — только стабильные.
    /// </summary>
    public static AppRelease? PickUpdate(IEnumerable<AppRelease> releases, ModVersion current) =>
        releases.Where(r => r.Version > current && (current.IsPrerelease || !r.Prerelease))
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();

    /// <summary>Ответ GitHub → релизы с архивом программы (черновики и релизы без архива пропускаются).</summary>
    public static IReadOnlyList<AppRelease> ParseReleases(string json)
    {
        var result = new List<AppRelease>();
        foreach (var r in JArray.Parse(json).OfType<JObject>())
        {
            if (r.Value<bool?>("draft") == true) continue;
            var tag = r.Value<string>("tag_name") ?? "";
            if (!ModVersion.TryParse(tag.TrimStart('v', 'V'), out var version)) continue;
            var asset = (r["assets"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(a => (a.Value<string>("name") ?? "").EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase));
            if (asset is null) continue;

            var digest = asset.Value<string>("digest"); // «sha256:…»
            result.Add(new AppRelease
            {
                Version = version,
                Tag = tag,
                Prerelease = r.Value<bool?>("prerelease") == true || version.IsPrerelease,
                PageUrl = r.Value<string>("html_url") ?? "",
                Notes = r.Value<string>("body") ?? "",
                AssetName = asset.Value<string>("name")!,
                AssetUrl = asset.Value<string>("browser_download_url") ?? "",
                AssetSize = asset.Value<long?>("size") ?? 0,
                Sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null,
            });
        }
        return result;
    }

    /// <summary>Скачать архив и сверить отпечаток. Без отпечатка не ставим: файл мог подмениться по дороге.</summary>
    public async Task<string> DownloadAsync(AppRelease release, string downloadsDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var expected = release.Sha256 ?? (release.ChecksumUrl is { } checksumUrl ? await ReadChecksumAsync(checksumUrl, ct).ConfigureAwait(false) : null);
        if (expected is null) throw new InvalidOperationException(Loc.T("update.noHash"));
        Directory.CreateDirectory(downloadsDir);
        var path = Path.Combine(downloadsDir, release.AssetName);

        using (var response = await _http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(Loc.T("update.httpFailed", (int)response.StatusCode));
            var total = response.Content.Headers.ContentLength ?? release.AssetSize;
            await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(path);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        if (!string.Equals(Sha256Of(path), expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException(Loc.T("update.badHash"));
        }
        return path;
    }

    /// <summary>«<hex>  имя-файла» → hex; нет файла или внутри не контрольная сумма — null.</summary>
    private async Task<string?> ReadChecksumAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var first = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim().Split(' ', '\t', '\n').FirstOrDefault() ?? "";
        return first.Length == 64 && first.All(Uri.IsHexDigit) ? first.ToLowerInvariant() : null;
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Поставить скачанный архив в папку программы: каждый файл архива встаёт на место прежнего, прежний переименовывается
    /// в «*.old». Папку data не трогаем — её в архиве нет. Если что-то пошло не так — всё возвращается как было.
    /// Архив — zip (Windows) или tar.gz (Linux: права файлов, в том числе «исполняемый», берутся из архива).
    /// </summary>
    public static void Install(string zipPath, string appDir)
    {
        var staging = Path.Combine(appDir, ".update");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        if (zipPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(staging);
            using var gz = new GZipStream(File.OpenRead(zipPath), CompressionMode.Decompress);
            System.Formats.Tar.TarFile.ExtractToDirectory(gz, staging, overwriteFiles: false);
        }
        else
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
        }
        try
        {
            // архив мог быть собран с папкой внутри
            var root = staging;
            if (!File.Exists(Path.Combine(root, MainExe)) && Directory.GetDirectories(root) is [var only] && Directory.GetFiles(root).Length == 0)
                root = only;
            if (!File.Exists(Path.Combine(root, MainExe))) throw new InvalidOperationException(Loc.T("update.badArchive"));

            var done = new List<(string Target, string? Old)>();
            try
            {
                foreach (var file in Directory.GetFiles(root))
                {
                    var target = Path.Combine(appDir, Path.GetFileName(file));
                    string? old = null;
                    if (File.Exists(target))
                    {
                        // уникальное имя: прежний «.old» может быть ещё занят агентом, запущенным до прошлого обновления
                        old = $"{target}.{Guid.NewGuid().ToString("N")[..8]}.old";
                        File.Move(target, old);
                    }
                    done.Add((target, old));
                    File.Move(file, target);
                }
            }
            catch
            {
                // откат: новые файлы — прочь, прежние — на место
                foreach (var (target, old) in Enumerable.Reverse(done))
                {
                    try
                    {
                        if (old is null) { File.Delete(target); continue; }
                        File.Move(old, target, overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Убрать «*.old» от прошлых обновлений. Занятые (старый агент ещё работает) остаются до следующего раза.</summary>
    public static void CleanupOld(string appDir)
    {
        foreach (var file in Directory.EnumerateFiles(appDir, "*.old"))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
