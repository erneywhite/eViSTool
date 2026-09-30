using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Profiles;
using Microsoft.Win32;
using eViSTool.Core.Localization;

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

    public string KindText => Loc.T(Kind == ProfileKind.Server ? "profile.kindServer" : "profile.kindClient");
    public string GameVersionText => Resolved.GameVersion?.ToString() ?? Loc.T("common.notFound");
    public string ConfigText => Resolved.ConfigPath ?? Loc.T("common.notFoundM");
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

    /// <summary>Перечитать файлы игры. notify=false — когда перечитывает сам список модов (без повторной перезагрузки).</summary>
    public void Refresh(bool notify = true)
    {
        Resolved = ProfileResolver.Resolve(Model);
        if (notify) _changed(this);
    }

    [RelayCommand]
    private void MakeClient() => Kind = ProfileKind.Client;

    [RelayCommand]
    private void MakeServer() => Kind = ProfileKind.Server;

    [RelayCommand]
    private void Reread() => Refresh();

    /// <summary>Сменился язык: пересчитать тексты (предупреждения тоже приходят из Core на текущем языке).</summary>
    public void NotifyLanguageChanged()
    {
        Resolved = ProfileResolver.Resolve(Model);
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void BrowseGameDir()
    {
        var exe = Kind == ProfileKind.Server ? "VintagestoryServer.exe" : "Vintagestory.exe";
        var dlg = new OpenFolderDialog { Title = Loc.T("profile.pickGameDir", exe), InitialDirectory = GameDir };
        if (dlg.ShowDialog() == true) GameDir = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseDataDir()
    {
        var dlg = new OpenFolderDialog
        {
            Title = Loc.T(Kind == ProfileKind.Server ? "profile.pickServerData" : "profile.pickClientData"),
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
