using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.ModDb;
using eViSTool.Core.Settings;
using eViSTool.Core.Versioning;
using Microsoft.Win32;

namespace eViSTool.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _store = new();
    private readonly AppSettings _settings;

    public ModsViewModel Mods { get; }

    [ObservableProperty] private string? _gameDir;
    [ObservableProperty] private string _modsDir;
    [ObservableProperty] private bool _allowUnstable;
    [ObservableProperty] private string _gameVersionText = "";

    public ModVersion? GameVersion { get; private set; }

    public MainViewModel()
    {
        _settings = _store.Load();
        _settings.GameDir ??= GameInstall.FindGameDir();

        _gameDir = _settings.GameDir;
        _modsDir = _settings.ModsDir;
        _allowUnstable = _settings.AllowUnstable;
        RefreshGameVersion();

        Mods = new ModsViewModel(this, new ModDbClient());
    }

    partial void OnGameDirChanged(string? value)
    {
        _settings.GameDir = value;
        RefreshGameVersion();
        Save();
    }

    partial void OnModsDirChanged(string value)
    {
        _settings.ModsDir = value;
        Save();
    }

    partial void OnAllowUnstableChanged(bool value)
    {
        _settings.AllowUnstable = value;
        Save();
    }

    private void RefreshGameVersion()
    {
        GameVersion = string.IsNullOrWhiteSpace(GameDir) ? null : GameInstall.DetectVersion(GameDir);
        GameVersionText = GameVersion is null ? "игра не найдена — укажи папку в настройках" : GameVersion.ToString();
    }

    private void Save()
    {
        try { _store.Save(_settings); }
        catch (IOException) { /* не критично: попробуем при следующем изменении */ }
    }

    [RelayCommand]
    private void BrowseGameDir()
    {
        var dlg = new OpenFolderDialog { Title = "Папка игры или сервера (где Vintagestory.exe / VintagestoryServer.exe)", InitialDirectory = GameDir };
        if (dlg.ShowDialog() == true) GameDir = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseModsDir()
    {
        var dlg = new OpenFolderDialog { Title = "Папка модов", InitialDirectory = ModsDir };
        if (dlg.ShowDialog() == true) ModsDir = dlg.FolderName;
    }

    [RelayCommand]
    private void ResetModsDir() => ModsDir = GameInstall.DefaultModsDir;
}
