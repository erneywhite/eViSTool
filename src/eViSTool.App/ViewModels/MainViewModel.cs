using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.ModDb;
using eViSTool.Core.Profiles;
using eViSTool.Core.Settings;

namespace eViSTool.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;

    public ModsViewModel Mods { get; }
    public CatalogViewModel Catalog { get; }

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

    public string DataLocationText => Core.AppPaths.IsPortable
        ? $"{Core.AppPaths.Root}  (портабельно, рядом с программой)"
        : $"{Core.AppPaths.Root}  (рядом с программой писать нельзя — используется профиль пользователя)";

    [RelayCommand]
    private void OpenDataFolder() => Shell.OpenFolder(Core.AppPaths.Root);

    public MainViewModel()
    {
        _settings = _store.Load();
        foreach (var p in _settings.Profiles) Profiles.Add(new ProfileViewModel(p, OnProfileChanged));

        _allowUnstable = _settings.AllowUnstable;
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
        Name = "Клиент " + (Profiles.Count + 1),
        Kind = ProfileKind.Client,
        GameDir = GameInstall.FindGameDir(),
        DataDir = GameInstall.DefaultDataDir,
    });

    [RelayCommand]
    private void AddServerProfile() => AddProfile(new GameProfile
    {
        Name = "Сервер " + (Profiles.Count + 1),
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
