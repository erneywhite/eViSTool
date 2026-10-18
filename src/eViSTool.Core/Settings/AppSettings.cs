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

    /// <summary>
    /// «Установить также в» без вопроса: мод сразу ставится и в связанные профили (если туда подходит), окно — только
    /// когда связанных нет или в какой-то из них поставить нельзя.
    /// </summary>
    public bool AlsoInstallWithoutAsking { get; set; }

    /// <summary>
    /// Перед изменением модов клиентского профиля — копия одиночных миров, в которые играли с прошлой копии
    /// (<see cref="Profiles.WorldCopies"/>). По умолчанию включено.
    /// </summary>
    public bool CopyWorldsBeforeModChanges { get; set; } = true;

    /// <summary>Рамка главного окна от оконного менеджера, а не своя шапка (только Linux: на Windows шапка всегда своя).</summary>
    public bool SystemWindowFrame { get; set; }

    /// <summary>
    /// Фон из игры под боковой панелью — размытая заставка главного меню из папки игры (её же картинки, их можно
    /// подменить своими). По умолчанию включено; выключено или картинок нет — спокойный узор.
    /// </summary>
    public bool GameBackground { get; set; } = true;

    /// <summary>Версия eViSTool, про которую попросили не напоминать («Пропустить эту версию»); вышла новее — окно покажется снова.</summary>
    public string? SkippedAppVersion { get; set; }

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
            Profiles.AddRange(ProfileResolver.DefaultProfiles(GameDir));
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

    // текст файла, каким его последний раз прочитали или записали отсюда: другое окно eViSTool пишет тот же файл, и без
    // сверки последнее сохранение молча затирало бы его правки (добавленный там профиль пропадал)
    private string? _known;

    /// <summary>
    /// Файл с прошлого чтения или записи отсюда поменяли снаружи — другое окно eViSTool (или человек руками). Сохранить
    /// поверх — значит потерять те правки.
    /// </summary>
    public bool ChangedElsewhere
    {
        get
        {
            try
            {
                return File.Exists(Path) && File.ReadAllText(Path) != _known;
            }
            catch (IOException)
            {
                return false; // файл как раз переписывают — не повод мешать сохранению
            }
        }
    }

    public AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(Path))
            {
                _known = File.ReadAllText(Path);
                settings = JsonConvert.DeserializeObject<AppSettings>(_known) ?? new AppSettings();
            }
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
        var text = JsonConvert.SerializeObject(settings, Formatting.Indented);
        File.WriteAllText(tmp, text);
        Platform.SafeFile.Replace(tmp, Path);
        _known = text;
    }
}
