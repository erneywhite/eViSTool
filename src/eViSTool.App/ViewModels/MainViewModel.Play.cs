using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.App.ViewModels;

/// <summary>Пункт меню «Играть»: сервер (или «без сервера»), подпись, состояние, булавка «по умолчанию».</summary>
public sealed partial class PlayMenuItemViewModel : ObservableObject
{
    public PlayMenuItemViewModel(PlayTarget? target, string name, string detail)
    {
        Target = target;
        Name = name;
        _detail = detail;
    }

    /// <summary>null — «Без сервера».</summary>
    public PlayTarget? Target { get; }
    public string Name { get; }
    public bool IsFavorite => Target?.Kind == PlayTargetKind.Favorite;
    public bool IsOwn => Target?.Kind == PlayTargetKind.OwnServer;
    public bool IsNone => Target is null;

    [ObservableProperty] private string _detail;
    [ObservableProperty] private bool _isDefault;

    /// <summary>Версия игры сервера не совпадает с профилем — подпись предупреждением (подключиться всё равно можно попробовать).</summary>
    [ObservableProperty] private string _versionNote = "";
}

public sealed partial class MainViewModel
{
    /// <summary>Сколько серверов из избранного игры показывать в меню; остальные — в «Найти сервер…».</summary>
    private const int MenuFavorites = 5;

    public ObservableCollection<PlayMenuItemViewModel> PlayOwnServers { get; } = [];
    public ObservableCollection<PlayMenuItemViewModel> PlayFavorites { get; } = [];
    /// <summary>«Без сервера» — создаётся при каждом открытии меню (подписи — на текущем языке).</summary>
    [ObservableProperty] private PlayMenuItemViewModel _playNoServer = new(null, "", "");

    public bool HasPlayOwnServers => PlayOwnServers.Count > 0;
    public bool HasPlayFavorites => PlayFavorites.Count > 0;
    [ObservableProperty] private string _playFavoritesMore = "";

    /// <summary>Сервер по умолчанию у активного клиентского профиля (его нет среди серверов — значит, без сервера).</summary>
    private PlayTarget? DefaultPlayTarget =>
        ActiveProfile?.Model is { Kind: ProfileKind.Client, DefaultServer: { Length: > 0 } key } profile ? FindPlayTarget(profile, key) : null;

    private PlayTarget? FindPlayTarget(GameProfile client, string key) =>
        key.StartsWith("own:", StringComparison.Ordinal)
            ? Profiles.Select(p => p.Model).FirstOrDefault(p => PlayTargets.OwnKey(p.Id) == key) is { } server ? PlayTargets.Own(server) : null
            : PlayTargets.Favorites(client.DataDir).FirstOrDefault(f => f.Key == key);

    /// <summary>«Играть · home» — когда у профиля выбран сервер по умолчанию.</summary>
    private string PlaySuffix => DefaultPlayTarget is { } t ? " · " + t.Name : "";

    /// <summary>Меню открывают — собрать его заново: свои серверы, избранное игры, у своих — состояние от их агентов.</summary>
    public void RefreshPlayMenu()
    {
        if (ActiveProfile?.Model is not { Kind: ProfileKind.Client } client) return;
        var defaultKey = DefaultPlayTarget?.Key;
        var clientVersion = ActiveProfile.Resolved.GameVersion?.ToString();

        PlayOwnServers.Clear();
        foreach (var server in Profiles.Where(p => p.Kind == ProfileKind.Server))
        {
            if (PlayTargets.Own(server.Model) is not { } target) continue;
            var item = new PlayMenuItemViewModel(target, server.Name,
                (server.Model.IsRemote ? "" : Loc.T("play.thisPc") + " · ") + Loc.T("play.checking"))
            {
                IsDefault = target.Key == defaultKey,
            };
            PlayOwnServers.Add(item);
            _ = FillServerStateAsync(item, server, clientVersion);
        }

        PlayFavorites.Clear();
        var favorites = PlayTargets.Favorites(client.DataDir);
        // сервер по умолчанию из избранного — всегда в меню, даже если он дальше пятого
        var shown = favorites.Where(f => f.Key == defaultKey).Concat(favorites.Where(f => f.Key != defaultKey)).Take(MenuFavorites);
        foreach (var f in shown)
            PlayFavorites.Add(new PlayMenuItemViewModel(f, f.Name, f.Address + (f.HasPassword ? " · " + Loc.T("play.withPassword") : ""))
            {
                IsDefault = f.Key == defaultKey,
            });
        PlayFavoritesMore = favorites.Count > MenuFavorites ? Loc.T("play.moreFavorites", favorites.Count - MenuFavorites) : "";
        PlayNoServer = new PlayMenuItemViewModel(null, Loc.T("play.noServer"), Loc.T("play.noServerHint")) { IsDefault = defaultKey is null };
        OnPropertyChanged(nameof(HasPlayOwnServers));
        OnPropertyChanged(nameof(HasPlayFavorites));
    }

    /// <summary>Состояние своего сервера — у его агента (свой — на этой машине, удалённый — по коду); не ответил — «нет связи».</summary>
    private static async Task FillServerStateAsync(PlayMenuItemViewModel item, ProfileViewModel server, string? clientVersion)
    {
        var where = server.Model.IsRemote ? "" : Loc.T("play.thisPc") + " · ";
        AgentClient? client = null;
        try
        {
            client = server.Model.IsRemote
                ? RemoteSecret.Unprotect(server.Model.RemoteCode) is { } code ? AgentClient.ForRemote(code) : null
                : AgentClient.TryConnect(server.Model.Id);
            if (client is null)
            {
                item.Detail = where + Loc.T(server.Model.IsRemote ? "play.noLink" : "play.stopped");
            }
            else
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var s = await client.StatusAsync(cts.Token);
                item.Detail = where + (s.State == ServerState.Running
                    ? Loc.T("play.srvRunning", s.Players.Count)
                    : Loc.T(s.State == ServerState.Stopped ? "play.stopped" : "play.startingSrv"));
                var version = server.Model.IsRemote ? s.GameVersion : server.Resolved.GameVersion?.ToString();
                if (version is { Length: > 0 } && clientVersion is { Length: > 0 } && version != clientVersion)
                    item.VersionNote = Loc.T("play.otherVersion", version, clientVersion);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException)
        {
            item.Detail = where + Loc.T(server.Model.IsRemote ? "play.noLink" : "play.stopped");
        }
        finally { client?.Dispose(); }
        if (!server.Model.IsRemote && clientVersion is { Length: > 0 } cv && server.Resolved.GameVersion?.ToString() is { Length: > 0 } sv && sv != cv)
            item.VersionNote = Loc.T("play.otherVersion", sv, cv);
    }

    /// <summary>Пункт меню: запустить игру с этим сервером один раз (по умолчанию не становится).</summary>
    [RelayCommand(CanExecute = nameof(CanPlayNow))]
    private Task PlayOn(PlayMenuItemViewModel? item) => item is null ? Task.CompletedTask : LaunchAsync(item.Target);

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
    public IReadOnlyList<DefaultServerOption> EditedDefaultServerOptions
    {
        get
        {
            if (EditedProfile?.Model is not { Kind: ProfileKind.Client } client) return [];
            var own = Profiles.Where(p => p.Kind == ProfileKind.Server).Select(p => PlayTargets.Own(p.Model)).OfType<PlayTarget>();
            return [new DefaultServerOption("", Loc.T("play.noServer")),
                .. own.Concat(PlayTargets.Favorites(client.DataDir)).Select(t => new DefaultServerOption(t.Key, $"{t.Name}  ·  {t.Address}"))];
        }
    }

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
    internal void NotifyEditedDefaultServer()
    {
        OnPropertyChanged(nameof(EditedDefaultServerOptions));
        OnPropertyChanged(nameof(EditedDefaultServer));
    }
}
