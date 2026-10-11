using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Localization;
using eViSTool.Core.Packs;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Окно «Создать модпак»: название, описание, три галочки и сводка. Путь к файлу спрашивается отдельно, после окна
/// (<see cref="ModsViewModel"/>). Окно показывает поля и закрывается с тем, что вернёт <see cref="Build"/>.
/// </summary>
public sealed partial class ExportPackViewModel : ObservableObject
{
    /// <param name="defaultName">Название по умолчанию — имя профиля.</param>
    /// <param name="enabledCount">Сколько в профиле включённых модов (для сводки).</param>
    /// <param name="disabledCount">Сколько выключенных.</param>
    /// <param name="hasModConfig">У профиля есть папка ModConfig: без неё галочку «Настройки модов» поставить нельзя.</param>
    public ExportPackViewModel(string defaultName, int enabledCount, int disabledCount, bool hasModConfig)
    {
        _name = defaultName;
        HasModConfig = hasModConfig;
        Summary = Loc.T("packx.summary", enabledCount, disabledCount);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasName))]
    private string _name;

    [ObservableProperty] private string _description = "";

    /// <summary>Класть файлы модов внутрь пака (по умолчанию да: работает без интернета, но файл больше).</summary>
    [ObservableProperty] private bool _bundleFiles = true;

    [ObservableProperty] private bool _includeModConfig;

    [ObservableProperty] private bool _includeDisabled;

    /// <summary>Есть ли у профиля настройки модов; нет — галочка «Настройки модов» выключена.</summary>
    public bool HasModConfig { get; }

    /// <summary>«Включённых модов: N, выключенных: M.»</summary>
    public string Summary { get; }

    /// <summary>Название не пустое (пробелы не считаются): без него пак не собрать.</summary>
    public bool HasName => !string.IsNullOrWhiteSpace(Name);

    /// <summary>
    /// Параметры сборки по заполненным полям. null — название пустое: окно не закрывается и возвращает фокус в поле
    /// названия. Описание из одних пробелов — не описание (null); настройки модов берутся, только если они есть у профиля.
    /// </summary>
    public PackExportOptions? Build()
    {
        if (!HasName) return null;
        return new PackExportOptions
        {
            Name = Name.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            BundleFiles = BundleFiles,
            IncludeModConfig = HasModConfig && IncludeModConfig,
            IncludeDisabled = IncludeDisabled,
        };
    }
}
