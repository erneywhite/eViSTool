using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Core.Settings;
using eViSTool.Core.Localization;

namespace eViSTool.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;

    public ModsViewModel Mods { get; }
    public CatalogViewModel Catalog { get; }
    public AboutViewModel About { get; } = new();

    /// <summary>Открытая вкладка: 0 — моды, 1 — каталог, …</summary>
    [ObservableProperty] private int _selectedTab;

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 1) _ = Catalog.EnsureLoadedAsync(); // каталог грузим только когда он нужен
    }
    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];

    /// <summary>Профиль, с которым сейчас работаем (переключатель в шапке).</summary>
    [ObservableProperty] private ProfileViewModel? _activeProfile;

    /// <summary>Профиль, открытый в редакторе на вкладке «Настройки».</summary>
    [ObservableProperty] private ProfileViewModel? _editedProfile;

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
        foreach (var p in Profiles) p.NotifyLanguageChanged();
        Mods.OnProfileSwitched();
        Catalog.OnLanguageChanged();
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
    private void OpenDataFolder() => Shell.OpenFolder(Core.AppPaths.Root);

    public MainViewModel()
    {
        _settings = _store.Load();
        Loc.Instance.SetLanguage(_settings.Language); // до того, как VM начнут собирать тексты
        foreach (var p in _settings.Profiles) Profiles.Add(new ProfileViewModel(p, OnProfileChanged));

        _allowUnstable = _settings.AllowUnstable;
        _autoCheckUpdates = _settings.AutoCheckUpdates;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Value == Loc.Instance.Language) ?? Languages[0];
        _activeProfile = Profiles.FirstOrDefault(p => p.Model == _settings.ActiveProfile);
        _editedProfile = _activeProfile;

        var db = new ModDbClient();
        Mods = new ModsViewModel(this, db);
        Catalog = new CatalogViewModel(this, db);
        Save(); // перенос настроек старого формата сразу на диск
        Mods.OnProfileSwitched(); // список модов виден сразу, без сети
    }

    partial void OnActiveProfileChanged(ProfileViewModel? value)
    {
        _settings.ActiveProfileId = value?.Model.Id;
        Save();
        Mods.OnProfileSwitched();
    }

    partial void OnAllowUnstableChanged(bool value)
    {
        _settings.AllowUnstable = value;
        Save();
        Mods.ReloadLocal();
    }

    private void OnProfileChanged(ProfileViewModel profile)
    {
        Save();
        if (profile == ActiveProfile) Mods.OnProfileSwitched();
    }

    public WindowLayout Layout => _settings.Layout;

    /// <summary>Перейти на вкладку «Каталог» и открыть там мод.</summary>
    public async Task ShowInCatalogAsync(long? assetId, string? modId, string? name)
    {
        SelectedTab = 1;
        await Catalog.ShowModAsync(assetId, modId, name);
    }

    /// <summary>Сохранить настройки (например, после закрепления версии мода).</summary>
    public void SaveSettings() => Save();

    private void Save()
    {
        try { _store.Save(_settings); }
        catch (IOException) { /* не критично: попробуем при следующем изменении */ }
    }

    [RelayCommand]
    private void AddClientProfile() => AddProfile(new GameProfile
    {
        Name = Loc.T("profile.newClient", Profiles.Count + 1),
        Kind = ProfileKind.Client,
        GameDir = GameInstall.FindGameDir(),
        DataDir = GameInstall.DefaultDataDir,
    });

    [RelayCommand]
    private void AddServerProfile() => AddProfile(new GameProfile
    {
        Name = Loc.T("profile.newServer", Profiles.Count + 1),
        Kind = ProfileKind.Server,
        // сервер по умолчанию живёт в той же папке данных, что и клиент (как у тебя на виртуалке)
        GameDir = ActiveProfile?.GameDir,
        DataDir = GameInstall.DefaultDataDir,
    });

    private void AddProfile(GameProfile model)
    {
        _settings.Profiles.Add(model);
        var vm = new ProfileViewModel(model, OnProfileChanged);
        Profiles.Add(vm);
        EditedProfile = vm;
        Save();
    }

    [RelayCommand]
    private void RemoveProfile(ProfileViewModel? profile)
    {
        if (profile is null || Profiles.Count <= 1) return; // последний профиль не удаляем
        _settings.Profiles.Remove(profile.Model);
        Profiles.Remove(profile);
        if (ActiveProfile == profile) ActiveProfile = Profiles[0];
        if (EditedProfile == profile) EditedProfile = ActiveProfile;
        Save();
    }
}
