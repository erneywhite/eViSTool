using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Settings;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// Настройки программы на вкладке «Настройки»: переключатели поведения, язык, папка данных. Каждое изменение сразу
/// пишется в settings.json (<see cref="AppSettings"/>); загружаются настройки в конструкторе главной модели.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>«Установить также в» без вопроса — сразу в связанные профили.</summary>
    [ObservableProperty] private bool _alsoInstallWithoutAsking;

    /// <summary>Копия одиночных миров перед изменением модов клиентского профиля.</summary>
    [ObservableProperty] private bool _copyWorldsBeforeModChanges;

    /// <summary>Рамка главного окна — системная (окно следит за свойством и переключает шапку сразу).</summary>
    [ObservableProperty] private bool _systemWindowFrame;

    partial void OnAlsoInstallWithoutAskingChanged(bool value)
    {
        _settings.AlsoInstallWithoutAsking = value;
        Save();
    }

    partial void OnCopyWorldsBeforeModChangesChanged(bool value)
    {
        _settings.CopyWorldsBeforeModChanges = value;
        Save();
    }

    /// <summary>Переключатель «Системная рамка» — только на Linux: там шапку можно вернуть оконному менеджеру.</summary>
    public bool CanChooseWindowFrame => OperatingSystem.IsLinux();

    partial void OnSystemWindowFrameChanged(bool value)
    {
        _settings.SystemWindowFrame = value;
        Save();
    }

    [ObservableProperty] private bool _allowUnstable;
    [ObservableProperty] private bool _autoCheckUpdates;

    public IReadOnlyList<Choice<string>> Languages { get; } =
        Loc.Available.Select(l => new Choice<string>(l.Name, l.Code)).ToList();

    [ObservableProperty] private Choice<string> _selectedLanguage = null!;

    partial void OnSelectedLanguageChanged(Choice<string> value)
    {
        if (value.Value == Loc.Instance.Language) return;
        _settings.Language = value.Value;
        Save();
        Loc.Instance.SetLanguage(value.Value);

        // подписи в разметке обновились сами; тексты, собранные в коде, пересобираем
        OnPropertyChanged(nameof(DataLocationText));
        OnPropertyChanged(nameof(PageTitle));
        foreach (var p in Profiles) p.NotifyLanguageChanged();
        NotifyPlay();
        Mods.OnProfileSwitched();
        Mods.OnLanguageChanged();
        Catalog.OnLanguageChanged();
        Server.OnLanguageChanged();
    }

    partial void OnAutoCheckUpdatesChanged(bool value)
    {
        _settings.AutoCheckUpdates = value;
        Save();
    }

    public string DataLocationText => Core.AppPaths.IsPortable
        ? Loc.T("settings.dataPortable", Core.AppPaths.Root)
        : Loc.T("settings.dataFallback", Core.AppPaths.Root);

    [RelayCommand]
    private void OpenDataFolder() => Ui.OpenFolder(Core.AppPaths.Root);

    partial void OnAllowUnstableChanged(bool value)
    {
        _settings.AllowUnstable = value;
        Save();
        Server?.Schedule.SyncPolicy(); // агент обновляет моды сервера при перезапуске — с тем же правилом
        Mods.ReloadLocal();
    }

    public WindowLayout Layout => _settings.Layout;

    /// <summary>Сохранить настройки (например, после закрепления версии мода).</summary>
    /// <summary>Сохранить настройки (из «Моих модов»: закрепления, пропуски версий) — и отдать их агенту сервера:
    /// он соблюдает их, обновляя моды при перезапуске (не поменялись — ничего не шлётся).</summary>
    public void SaveSettings()
    {
        Save();
        Server?.Schedule.SyncPolicy();
    }

    private void Save()
    {
        try { _store.Save(_settings); }
        catch (IOException) { /* не критично: попробуем при следующем изменении */ }
    }
}
