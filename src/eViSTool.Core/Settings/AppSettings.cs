using eViSTool.Core.Profiles;
using Newtonsoft.Json;

namespace eViSTool.Core.Settings;

public sealed class AppSettings
{
    public List<GameProfile> Profiles { get; set; } = [];
    public string? ActiveProfileId { get; set; }

    /// <summary>Предлагать пре-релизы модов (rc/pre) даже тем, у кого стоит стабильная версия.</summary>
    public bool AllowUnstable { get; set; }

    /// <summary>Сверять моды с модбазой сразу при запуске и при смене профиля.</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Язык интерфейса (en, ru). По умолчанию английский.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Раскладка окна: запоминается между запусками.</summary>
    public WindowLayout Layout { get; set; } = new();

    /// <summary>Устаревшее (до профилей): папка игры. Переносится в профиль при загрузке.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? GameDir { get; set; }

    [JsonIgnore]
    public GameProfile? ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles.FirstOrDefault();

    /// <summary>Гарантирует хотя бы один профиль (первый запуск или настройки старого формата).</summary>
    public void EnsureProfiles()
    {
        if (Profiles.Count == 0)
            Profiles.Add(ProfileResolver.DefaultProfile(GameDir));
        GameDir = null;
        if (Profiles.All(p => p.Id != ActiveProfileId))
            ActiveProfileId = Profiles[0].Id;
    }
}

public sealed class WindowLayout
{
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 860;
    public bool Maximized { get; set; }

    /// <summary>Ширина карточки мода в каталоге (перетаскивается разделителем).</summary>
    public double CatalogDetailsWidth { get; set; } = 760;

    /// <summary>Ширина карточки мода во вкладке «Мои моды».</summary>
    public double ModsCardWidth { get; set; } = 420;
}

/// <summary>Хранит настройки в settings.json папки данных (см. <see cref="AppPaths"/>). Запись атомарная: сначала во временный файл.</summary>
public sealed class SettingsStore(string? path = null)
{
    public string Path { get; } = path ?? AppPaths.SettingsFile;

    public AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(Path))
                settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // битый файл не должен ронять приложение — сохраним копию и начнём с чистых настроек
            try { File.Copy(Path, Path + ".broken", overwrite: true); } catch (IOException) { }
        }
        settings.EnsureProfiles();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(settings, Formatting.Indented));
        File.Move(tmp, Path, overwrite: true);
    }
}
