using eViSTool.Presentation;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Profiles;
using eViSTool.Core.Localization;

namespace eViSTool.Presentation.ViewModels;

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

    /// <summary>Тип задаётся при создании профиля и дальше не меняется.</summary>
    public bool IsServer => Kind == ProfileKind.Server;

    public string KindText => Model.IsHosted ? Loc.T("profile.kindHosted")
        : Loc.T(Model.IsRemote ? "profile.kindRemote" : Kind == ProfileKind.Server ? "profile.kindServer" : "profile.kindClient");

    public bool IsRemote => Model.IsRemote;

    /// <summary>Сервер у хостера: моды по FTP, запуск и консоль — в панели хостера.</summary>
    public bool IsHosted => Model.IsHosted;

    /// <summary>Профиль на этой машине: папки игры и данных показываются и выбираются.</summary>
    public bool IsLocal => Model.IsLocal;

    /// <summary>Куда ведёт подключение к хостеру: «ftp://логин@host:21 · без шифрования» (пароль не показываем).
    /// Auto не обещает шифрования: сервер без TLS (G-Portal) оставляет профиль на Auto, и пароль идёт открытым текстом.</summary>
    public string HostedTarget => Model.Hosted is not { } h ? Loc.T("hosted.errNoConnection")
        : h.Describe() + " · " + h.Tls switch
        {
            Core.Hosting.FtpTls.Explicit or Core.Hosting.FtpTls.Implicit => Loc.T("hosted.encrypted"),
            Core.Hosting.FtpTls.Auto => Loc.T("hosted.encryptionIfSupported"),
            _ => Loc.T("hosted.noEncryption"),
        };

    /// <summary>Изменить подключение к хостеру (адрес, логин, пароль, папки).</summary>
    [RelayCommand]
    private void EditConnection()
    {
        if (AppWindows.Current.AskHostedConnection(Model.Hosted, Loc.T("settings.hostedEditOk")) is not { } connection) return;
        Model.Hosted = connection;
        Model.RemoteCode = GameProfile.HostedMarker;
        OnPropertyChanged(nameof(HostedTarget));
        OnPropertyChanged(nameof(ManualGameVersion));
        OnPropertyChanged(nameof(RemindStopServer));
        OnPropertyChanged(nameof(GameVersionText));
        _changed(this);
    }

    /// <summary>Версия игры сервера у хостера, указанная вручную; пусто — определяется по файлам сервера.</summary>
    public string ManualGameVersion
    {
        get => Model.Hosted?.GameVersion ?? "";
        set
        {
            if (Model.Hosted is not { } h) return;
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (h.GameVersion == v) return;
            h.GameVersion = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GameVersionText));
            _changed(this);
        }
    }

    /// <summary>Спрашивать «останови сервер в панели хостера» перед изменением модов (по умолчанию — да).</summary>
    public bool RemindStopServer
    {
        get => Model.Hosted is not { SkipStopReminder: true };
        set
        {
            if (Model.Hosted is not { } h || h.SkipStopReminder == !value) return;
            h.SkipStopReminder = !value;
            OnPropertyChanged();
            _changed(this);
        }
    }

    /// <summary>Версия игры сервера у хостера, которую нашло чтение модов (null — ещё не читали или не нашлась).</summary>
    public string? HostedGameVersion
    {
        get;
        set { if (field == value) return; field = value; OnPropertyChanged(); OnPropertyChanged(nameof(GameVersionText)); }
    }

    /// <summary>Откуда взялась <see cref="HostedGameVersion"/> — «из serverconfig.json» и т. п.; пусто — неизвестно.</summary>
    public string HostedGameVersionSource
    {
        get;
        set { if (field == value) return; field = value; OnPropertyChanged(); }
    } = "";

    /// <summary>Куда ведёт код подключения удалённого профиля (сам код не показываем).</summary>
    public string RemoteTarget => Core.Server.Remote.RemoteSecret.Unprotect(Model.RemoteCode) is { } c ? $"{c.Host}:{c.Port}" : Loc.T("remote.errDecrypt");

    /// <summary>Заменить код подключения (сменили ключ на сервере, переехал адрес).</summary>
    [RelayCommand]
    private void ReplaceCode()
    {
        if (AppWindows.Current.AskConnectionCode(Loc.T("settings.remoteReplaceOk")) is not { } code) return;
        Model.RemoteCode = Core.Server.Remote.RemoteSecret.Protect(code);
        OnPropertyChanged(nameof(RemoteTarget));
        _changed(this);
    }
    public string GameVersionText => Model.IsHosted
            ? Model.Hosted?.GameVersion ?? HostedGameVersion ?? Loc.T("profile.hostedVersion")
        : Model.IsRemote ? RemoteGameVersion ?? Loc.T("profile.remoteVersion")
        : Resolved.GameVersion?.ToString() ?? Loc.T("common.notFound");

    /// <summary>Версия игры удалённого сервера — из статуса его агента (пока связи не было — неизвестна).</summary>
    public string? RemoteGameVersion
    {
        get;
        set { if (field == value) return; field = value; OnPropertyChanged(nameof(GameVersionText)); }
    }
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
        var exe = Kind == ProfileKind.Server ? Core.Game.ServerExecutable.FileName : Core.Game.GameLauncher.ClientExeName;
        if (Ui.Platform.PickFolder(Loc.T("profile.pickGameDir", exe), GameDir) is { } picked) GameDir = picked;
    }

    [RelayCommand]
    private void BrowseDataDir()
    {
        var title = Loc.T(Kind == ProfileKind.Server ? "profile.pickServerData" : "profile.pickClientData");
        if (Ui.Platform.PickFolder(title, DataDir) is { } picked) DataDir = picked;
    }

    [RelayCommand]
    private void OpenDataDir()
    {
        if (Directory.Exists(DataDir)) Ui.OpenFolder(DataDir);
    }
}
