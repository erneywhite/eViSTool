using eViSTool.Core.Profiles;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace eViSTool.Core.Packs;

/// <summary>
/// Модпак .evpack — обычный zip:
///   evpack.json          — этот манифест
///   mods/&lt;файл&gt;.zip    — моды, если их положили внутрь
///   config/ModConfig/…   — настройки модов, если их положили
/// Моды, которых внутри нет, при импорте скачиваются из модбазы строго той версии, что в манифесте.
/// </summary>
public sealed class PackManifest
{
    public const string FileName = "evpack.json";
    public const string ModsFolder = "mods/";
    public const string ConfigFolder = "config/ModConfig/";
    public const string Extension = ".evpack";

    public int Format { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? CreatedWith { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>Версия игры, на которой собирали (для предупреждения при импорте).</summary>
    public string? GameVersion { get; set; }

    [JsonConverter(typeof(Settings.TolerantEnumConverter))] // значение из более новой версии не сбрасывает весь файл
    public ProfileKind Kind { get; set; }

    public bool IncludesModConfig { get; set; }
    public List<PackMod> Mods { get; set; } = [];
}

public sealed class PackMod
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Имя файла мода (как у автора).</summary>
    public string FileName { get; set; } = "";

    /// <summary>SHA-256 файла — чтобы у всех стоял ровно тот же мод.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Лежит внутри пака (mods/FileName) или качается из модбазы.</summary>
    public bool Bundled { get; set; }

    /// <summary>
    /// Где мод работает (как в modinfo.json или на модбазе: client / server / both). Импорт в серверный профиль по нему
    /// пропускает клиентские моды. Нет (пак старой версии) — сторону узнаём из самого файла при установке.
    /// </summary>
    public string? Side { get; set; }
}
