using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Profiles;
using Microsoft.Win32;

namespace eViSTool.App.ViewModels;

/// <summary>Редактируемый профиль. Любое изменение — сохранение настроек и перечитывание файлов игры.</summary>
public sealed partial class ProfileViewModel : ObservableObject
{
    private readonly Action<ProfileViewModel> _changed;

    public GameProfile Model { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private ProfileKind _kind;
    [ObservableProperty] private string? _gameDir;
    [ObservableProperty] private string? _dataDir;
    [ObservableProperty] private ResolvedProfile _resolved;

    public ProfileViewModel(GameProfile model, Action<ProfileViewModel> changed)
    {
        Model = model;
        _changed = changed;
        _name = model.Name;
        _kind = model.Kind;
        _gameDir = model.GameDir;
        _dataDir = model.DataDir;
        _resolved = ProfileResolver.Resolve(model);
    }

    public bool IsServer
    {
        get => Kind == ProfileKind.Server;
        set => Kind = value ? ProfileKind.Server : ProfileKind.Client;
    }

    public string KindText => Kind == ProfileKind.Server ? "сервер" : "клиент";
    public string GameVersionText => Resolved.GameVersion?.ToString() ?? "не найдена";
    public string ConfigText => Resolved.ConfigPath ?? "не найден";
    public string ModDirsText => Resolved.ModDirs.Count == 0 ? "—" : string.Join(Environment.NewLine, Resolved.ModDirs);
    public string WarningsText => string.Join(Environment.NewLine, Resolved.Warnings);
    public bool HasWarnings => Resolved.Warnings.Count > 0;

    partial void OnNameChanged(string value) { Model.Name = value; _changed(this); }
    partial void OnKindChanged(ProfileKind value)
    {
        Model.Kind = value;
        OnPropertyChanged(nameof(IsServer));
        OnPropertyChanged(nameof(KindText));
        Refresh();
    }
    partial void OnGameDirChanged(string? value) { Model.GameDir = value; Refresh(); }
    partial void OnDataDirChanged(string? value) { Model.DataDir = value; Refresh(); }

    partial void OnResolvedChanged(ResolvedProfile value)
    {
        OnPropertyChanged(nameof(GameVersionText));
        OnPropertyChanged(nameof(ConfigText));
        OnPropertyChanged(nameof(ModDirsText));
        OnPropertyChanged(nameof(WarningsText));
        OnPropertyChanged(nameof(HasWarnings));
    }

    [RelayCommand]
    public void Refresh()
    {
        Resolved = ProfileResolver.Resolve(Model);
        _changed(this);
    }

    [RelayCommand]
    private void BrowseGameDir()
    {
        var exe = Kind == ProfileKind.Server ? "VintagestoryServer.exe" : "Vintagestory.exe";
        var dlg = new OpenFolderDialog { Title = $"Папка, где лежит {exe}", InitialDirectory = GameDir };
        if (dlg.ShowDialog() == true) GameDir = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseDataDir()
    {
        var dlg = new OpenFolderDialog
        {
            Title = Kind == ProfileKind.Server ? "Папка данных сервера (--dataPath, там serverconfig.json)" : "Папка данных игры (VintagestoryData)",
            InitialDirectory = DataDir,
        };
        if (dlg.ShowDialog() == true) DataDir = dlg.FolderName;
    }

    [RelayCommand]
    private void OpenDataDir()
    {
        if (Directory.Exists(DataDir)) Shell.OpenFolder(DataDir);
    }
}
