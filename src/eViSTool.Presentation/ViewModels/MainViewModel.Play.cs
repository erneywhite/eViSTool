using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Presentation.ViewModels;

/// <summary>
/// «Играть»: запуск игры с активным клиентским профилем, меню серверов у кнопки (сборка пунктов — в
/// <see cref="PlayMenu"/>) и «Сервер по умолчанию» в настройках клиентского профиля.
/// </summary>
public sealed partial class MainViewModel
{
    // ---- запуск игры с активным клиентским профилем

    /// <summary>Кнопка «Играть» — только для клиентского профиля (у серверного свой раздел «Сервер»).</summary>
    public bool CanPlay => ActiveProfile is { Kind: ProfileKind.Client };

    [ObservableProperty] private bool _isLaunching;

    public string PlayText => IsGameRunning ? Loc.T("play.running") : IsLaunching ? Loc.T("play.starting") : Loc.T("play.button") + PlaySuffix;

    /// <summary>Игра этого профиля запущена (нами или как угодно ещё: ярлык, лаунчер) — второй экземпляр не даём.</summary>
    [ObservableProperty] private bool _isGameRunning;

    partial void OnIsGameRunningChanged(bool value)
    {
        if (!value) ShowUpdatePrompt(); // окно об обновлении ждало, пока закроют игру
        OnPropertyChanged(nameof(PlayText));
        PlayCommand.NotifyCanExecuteChanged();
        PlayOnCommand.NotifyCanExecuteChanged();
    }

    private bool CanPlayNow => !IsGameRunning && !IsLaunching;

    public string PlayTip => ActiveProfile is not { Kind: ProfileKind.Client } profile ? ""
        : GameLauncher.DataPathFor(profile.Model) is { } dataPath ? Loc.T("play.tipOwnData", profile.Name, dataPath)
        : Loc.T("play.tip", profile.Name);

    partial void OnIsLaunchingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayText));
        PlayCommand.NotifyCanExecuteChanged();
        PlayOnCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Значок игры для блока «Vintage Story» внизу слева — из установленной игры активного профиля, а если у него
    /// своей нет (удалённый профиль) — из любого другого профиля. null — игры нет нигде: показываются буквы «VS».
    /// </summary>
    public IReadOnlyList<string?> IconSearchDirs =>
        [.. new[] { ActiveProfile?.GameDir }.Concat(Profiles.Select(p => p.GameDir)).Append(_foundGameDir.Value)];

    // shortcut: игра, найденная поиском, запоминается до перезапуска окна — значок пересчитывается на каждой смене
    // профиля, а поиск у игры не в стандартной папке читает весь реестр «Установка и удаление программ» в потоке окна.
    // Поставят игру при открытом окне — значок появится после перезапуска (или раньше — из папки профиля).
    private readonly Lazy<string?> _foundGameDir = new(Core.Game.GameInstall.FindGameDir);

    private void NotifyPlay()
    {
        _ = CheckGameAsync(); // сменили профиль — у нового своя игра
        OnPropertyChanged(nameof(IconSearchDirs));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayText));
        OnPropertyChanged(nameof(PlayTip));
    }

    /// <summary>«Играть»: с сервером по умолчанию, если он выбран у профиля, иначе просто игра.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayNow))]
    private Task Play() => LaunchAsync(DefaultPlayTarget);

    private async Task LaunchAsync(PlayTarget? server)
    {
        if (ActiveProfile is not { Kind: ProfileKind.Client } profile) return;

        // вторая копия игры с той же папкой данных перезапишет настройки первой: кнопка в это время неактивна,
        // а здесь — на случай, если игру запустили только что (ярлык), а опрос ещё не заметил
        if (await Task.Run(() => GameProcess.FindClientPids(profile.Model).Count > 0))
        {
            IsGameRunning = true;
            return;
        }

        try
        {
            GameLauncher.Launch(profile.Model, server);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Ui.Warn(Loc.T("play.failed", ex.Message));
            return;
        }

        // процесс уже есть — дальше кнопку держит опрос («Игра запущена»)
        IsLaunching = true;
        try
        {
            await CheckGameAsync();
            if (!IsGameRunning) await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally { IsLaunching = false; }
        await CheckGameAsync();
    }

    // ---- меню «Играть»: свои серверы, избранное игры, без сервера

    public ObservableCollection<PlayMenuItemViewModel> PlayOwnServers { get; } = [];
    public ObservableCollection<PlayMenuItemViewModel> PlayFavorites { get; } = [];
    /// <summary>«Без сервера» — создаётся при каждом открытии меню (подписи — на текущем языке).</summary>
    [ObservableProperty] private PlayMenuItemViewModel _playNoServer = new(null, "", "");

    public bool HasPlayOwnServers => PlayOwnServers.Count > 0;
    public bool HasPlayFavorites => PlayFavorites.Count > 0;
    [ObservableProperty] private string _playFavoritesMore = "";

    /// <summary>Сервер по умолчанию у активного клиентского профиля (его нет среди серверов — значит, без сервера).</summary>
    private PlayTarget? DefaultPlayTarget => PlayMenu.DefaultTarget(ActiveProfile, Profiles);

    /// <summary>«Играть · home» — когда у профиля выбран сервер по умолчанию.</summary>
    private string PlaySuffix => DefaultPlayTarget is { } t ? " · " + t.Name : "";

    /// <summary>Меню открывают — собрать его заново: свои серверы, избранное игры, у своих — состояние от их агентов.</summary>
    public void RefreshPlayMenu()
    {
        if (ActiveProfile?.Model is not { Kind: ProfileKind.Client } client) return;
        var defaultKey = DefaultPlayTarget?.Key;
        var clientVersion = ActiveProfile.Resolved.GameVersion?.ToString();

        PlayMenu.FillOwnServers(PlayOwnServers, Profiles, defaultKey, clientVersion);
        PlayFavoritesMore = PlayMenu.FillFavorites(PlayFavorites, client, defaultKey);
        PlayNoServer = PlayMenu.NoServer(defaultKey);
        OnPropertyChanged(nameof(HasPlayOwnServers));
        OnPropertyChanged(nameof(HasPlayFavorites));
    }

    /// <summary>Пункт меню: запустить игру с этим сервером один раз (по умолчанию не становится).</summary>
    [RelayCommand(CanExecute = nameof(CanPlayNow))]
    private Task PlayOn(PlayMenuItemViewModel? item) => item is null ? Task.CompletedTask : LaunchAsync(item.Target);

    /// <summary>Из окна «Найти сервер»: сыграть на этом сервере (один раз).</summary>
    public Task PlayOnTargetAsync(PlayTarget target) => CanPlayNow ? LaunchAsync(target) : Task.CompletedTask;

    /// <summary>«Найти сервер…»: общий список серверов игры и избранное целиком.</summary>
    public void OpenFindServer()
    {
        if (ActiveProfile is not { Kind: ProfileKind.Client } client) return;
        AppWindows.Current.ShowFindServer(new FindServerViewModel(this, client));
    }

    /// <summary>Булавка: сделать сервером по умолчанию (повторно — снять, тогда «без сервера»).</summary>
    [RelayCommand]
    private void PinPlayTarget(PlayMenuItemViewModel? item)
    {
        if (item is null || ActiveProfile?.Model is not { Kind: ProfileKind.Client } client) return;
        client.DefaultServer = item.Target is { } t && client.DefaultServer != t.Key ? t.Key : null;
        Save();
        RefreshPlayMenu();
        OnPlayDefaultChanged();
    }

    private void OnPlayDefaultChanged()
    {
        OnPropertyChanged(nameof(PlayText));
        OnPropertyChanged(nameof(PlayTip));
        OnPropertyChanged(nameof(EditedDefaultServer));
    }

    // ---------- «Сервер по умолчанию» в настройках клиентского профиля

    /// <summary>Вариант поля «Сервер по умолчанию»: ключ ("" — без сервера) и подпись.</summary>
    public sealed record DefaultServerOption(string Key, string Title);

    /// <summary>Без сервера, свои серверы, избранное игры редактируемого профиля.</summary>
    public IReadOnlyList<DefaultServerOption> EditedDefaultServerOptions =>
        EditedProfile?.Model is not { Kind: ProfileKind.Client } client ? [] : PlayMenu.DefaultServerOptions(client, Profiles);

    /// <summary>Выбранный ключ (выбор по ключу: варианты пересоздаются, а выбор должен узнаваться).</summary>
    public string EditedDefaultServer
    {
        get => EditedProfile?.Model.DefaultServer ?? "";
        set
        {
            if (EditedProfile?.Model is not { Kind: ProfileKind.Client } client) return;
            var key = string.IsNullOrEmpty(value) ? null : value;
            if (client.DefaultServer == key) return;
            client.DefaultServer = key;
            Save();
            OnPlayDefaultChanged();
        }
    }

    /// <summary>Сменился редактируемый профиль или список серверов — варианты поля заново.</summary>
    public void NotifyEditedDefaultServer()
    {
        OnPropertyChanged(nameof(EditedDefaultServerOptions));
        OnPropertyChanged(nameof(EditedDefaultServer));
    }
}
