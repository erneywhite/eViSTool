using Newtonsoft.Json;

namespace eViSTool.Core.ModDb;

// Модели API mods.vintagestory.at. Поля и их капризы подсмотрены в Rustique (MIT, Tekunogosu):
// в тегах бывают null, filename бывает числом, modversion — null.

public sealed class ModDbModResponse
{
    [JsonProperty("statuscode")] public string StatusCode { get; set; } = "";
    [JsonProperty("mod")] public ModDbMod? Mod { get; set; }
}

public sealed class ModDbMod
{
    [JsonProperty("modid")] public long ModId { get; set; }
    [JsonProperty("assetid")] public long AssetId { get; set; }
    [JsonProperty("name")] public string? Name { get; set; }
    [JsonProperty("text")] public string? Text { get; set; }
    [JsonProperty("author")] public string? Author { get; set; }
    [JsonProperty("urlalias")] public string? UrlAlias { get; set; }
    [JsonProperty("logofile")] public string? LogoFile { get; set; }
    [JsonProperty("homepageurl")] public string? HomePageUrl { get; set; }
    [JsonProperty("sourcecodeurl")] public string? SourceCodeUrl { get; set; }
    [JsonProperty("downloads")] public long Downloads { get; set; }
    [JsonProperty("follows")] public long Follows { get; set; }
    [JsonProperty("side")] public string? Side { get; set; }
    [JsonProperty("type")] public string? Type { get; set; }
    [JsonProperty("lastreleased")] public string? LastReleased { get; set; }
    [JsonProperty("tags")] public List<string?> Tags { get; set; } = [];
    [JsonProperty("releases")] public List<ModDbRelease> Releases { get; set; } = [];
    [JsonProperty("screenshots")] public List<ModDbScreenshot> Screenshots { get; set; } = [];

    /// <summary>Страница мода на сайте.</summary>
    public string PageUrl => string.IsNullOrEmpty(UrlAlias)
        ? $"https://mods.vintagestory.at/show/mod/{AssetId}"
        : $"https://mods.vintagestory.at/{UrlAlias}";
}

public sealed class ModDbRelease
{
    [JsonProperty("releaseid")] public long ReleaseId { get; set; }
    [JsonProperty("mainfile")] public string? MainFile { get; set; }
    [JsonProperty("filename")] public string? FileName { get; set; }
    [JsonProperty("fileid")] public long? FileId { get; set; }
    [JsonProperty("downloads")] public long Downloads { get; set; }
    /// <summary>Версии игры, с которыми автор отметил совместимость.</summary>
    [JsonProperty("tags")] public List<string?> Tags { get; set; } = [];
    [JsonProperty("modidstr")] public string? ModIdStr { get; set; }
    [JsonProperty("modversion")] public string? ModVersion { get; set; }
    [JsonProperty("created")] public string? Created { get; set; }
    [JsonProperty("changelog")] public string? Changelog { get; set; }

    public IEnumerable<string> GameVersions => Tags.Where(t => !string.IsNullOrWhiteSpace(t))!;
}

public sealed class ModDbScreenshot
{
    [JsonProperty("mainfile")] public string? MainFile { get; set; }
    [JsonProperty("thumbnailfilename")] public string? ThumbnailFileName { get; set; }
}

/// <summary>Краткая карточка из /api/mods (весь каталог).</summary>
public sealed class ModDbListItem
{
    [JsonProperty("modid")] public long ModId { get; set; }
    [JsonProperty("assetid")] public long AssetId { get; set; }
    [JsonProperty("downloads")] public long Downloads { get; set; }
    [JsonProperty("follows")] public long Follows { get; set; }
    [JsonProperty("trendingpoints")] public long TrendingPoints { get; set; }
    [JsonProperty("comments")] public long Comments { get; set; }
    [JsonProperty("name")] public string? Name { get; set; }
    [JsonProperty("summary")] public string? Summary { get; set; }
    [JsonProperty("modidstrs")] public List<string?> ModIdStrs { get; set; } = [];
    [JsonProperty("author")] public string? Author { get; set; }
    [JsonProperty("urlalias")] public string? UrlAlias { get; set; }
    [JsonProperty("side")] public string? Side { get; set; }
    [JsonProperty("type")] public string? Type { get; set; }
    [JsonProperty("logo")] public string? Logo { get; set; }
    [JsonProperty("tags")] public List<string?> Tags { get; set; } = [];
    [JsonProperty("lastreleased")] public string? LastReleased { get; set; }

    [JsonIgnore]
    public string PageUrl => string.IsNullOrEmpty(UrlAlias)
        ? $"https://mods.vintagestory.at/show/mod/{AssetId}"
        : $"https://mods.vintagestory.at/{UrlAlias}";

    /// <summary>Строковый modid (для установки и сверки с установленными).</summary>
    [JsonIgnore]
    public string? PrimaryModId => ModIdStrs.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.ToLowerInvariant();
}

internal sealed class ModDbListResponse
{
    [JsonProperty("statuscode")] public string StatusCode { get; set; } = "";
    [JsonProperty("mods")] public List<ModDbListItem> Mods { get; set; } = [];
}

internal sealed class ModDbGameVersionsResponse
{
    [JsonProperty("statuscode")] public string StatusCode { get; set; } = "";
    [JsonProperty("gameversions")] public List<ModDbTag> GameVersions { get; set; } = [];
}

public sealed class ModDbTag
{
    [JsonProperty("tagid")] public long TagId { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = "";
    [JsonProperty("color")] public string? Color { get; set; }
}
