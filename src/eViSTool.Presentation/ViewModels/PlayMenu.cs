using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.Presentation.ViewModels;

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
    public bool HasPassword => Target?.HasPassword == true;

    [ObservableProperty] private string _detail;
    [ObservableProperty] private bool _isDefault;

    /// <summary>Версия игры сервера не совпадает с профилем — подпись предупреждением (подключиться всё равно можно попробовать).</summary>
    [ObservableProperty] private string _versionNote = "";
}

/// <summary>
/// Из чего собирается меню «Играть»: свои серверы (их состояние — у агентов), избранное игры, «без сервера», сервер по
/// умолчанию клиентского профиля и варианты поля «Сервер по умолчанию» в настройках. Сами списки меню и выбор
/// по умолчанию живут в главной модели — здесь только сборка.
/// </summary>
public static class PlayMenu
{
    /// <summary>Сколько серверов из избранного игры показывать в меню; остальные — в «Найти сервер…».</summary>
    public const int MenuFavorites = 5;

    /// <summary>Сервер по умолчанию у активного клиентского профиля (его нет среди серверов — значит, без сервера).</summary>
    public static PlayTarget? DefaultTarget(ProfileViewModel? active, IEnumerable<ProfileViewModel> profiles) =>
        active?.Model is { Kind: ProfileKind.Client, DefaultServer: { Length: > 0 } key } profile ? FindTarget(profiles, profile, key) : null;

    /// <summary>Сервер по ключу: свой — среди профилей, из избранного — в избранном игры клиентского профиля.</summary>
    public static PlayTarget? FindTarget(IEnumerable<ProfileViewModel> profiles, GameProfile client, string key) =>
        key.StartsWith("own:", StringComparison.Ordinal)
            ? profiles.Select(p => p.Model).FirstOrDefault(p => PlayTargets.OwnKey(p.Id) == key) is { } server ? PlayTargets.Own(server) : null
            : PlayTargets.Favorites(client.DataDir).FirstOrDefault(f => f.Key == key);

    /// <summary>
    /// Свои серверы в меню — заново: сначала «проверяю…», состояние дописывается, когда ответит агент сервера.
    /// </summary>
    /// <param name="clientVersion">Версия игры клиентского профиля: у сервера другая — предупреждение в пункте.</param>
    public static void FillOwnServers(ICollection<PlayMenuItemViewModel> items, IEnumerable<ProfileViewModel> profiles,
        string? defaultKey, string? clientVersion)
    {
        items.Clear();
        foreach (var server in profiles.Where(p => p.Kind == ProfileKind.Server && !p.Model.IsHosted))
        {
            if (PlayTargets.Own(server.Model) is not { } target) continue;
            var item = new PlayMenuItemViewModel(target, server.Name,
                (server.Model.IsRemote ? "" : Loc.T("play.thisPc") + " · ") + Loc.T("play.checking"))
            {
                IsDefault = target.Key == defaultKey,
            };
            items.Add(item);
            _ = FillServerStateAsync(item, server, clientVersion);
        }
    }

    /// <summary>
    /// Избранное игры в меню — заново, не больше <see cref="MenuFavorites"/>; сервер по умолчанию из избранного —
    /// всегда в меню, даже если он дальше пятого. Возвращает подпись «ещё N в «Найти сервер…»» ("" — все показаны).
    /// </summary>
    public static string FillFavorites(ICollection<PlayMenuItemViewModel> items, GameProfile client, string? defaultKey)
    {
        items.Clear();
        var favorites = PlayTargets.Favorites(client.DataDir);
        // сервер по умолчанию из избранного — всегда в меню, даже если он дальше пятого
        var shown = favorites.Where(f => f.Key == defaultKey).Concat(favorites.Where(f => f.Key != defaultKey)).Take(MenuFavorites);
        foreach (var f in shown)
            items.Add(new PlayMenuItemViewModel(f, f.Name, f.Address + (f.HasPassword ? " · " + Loc.T("play.withPassword") : ""))
            {
                IsDefault = f.Key == defaultKey,
            });
        return favorites.Count > MenuFavorites ? Loc.T("play.moreFavorites", favorites.Count - MenuFavorites) : "";
    }

    /// <summary>«Без сервера» — новый пункт при каждом открытии меню (подписи — на текущем языке).</summary>
    public static PlayMenuItemViewModel NoServer(string? defaultKey) =>
        new(null, Loc.T("play.noServer"), Loc.T("play.noServerHint")) { IsDefault = defaultKey is null };

    /// <summary>Варианты поля «Сервер по умолчанию»: без сервера, свои серверы, избранное игры клиентского профиля.</summary>
    public static IReadOnlyList<MainViewModel.DefaultServerOption> DefaultServerOptions(GameProfile client, IEnumerable<ProfileViewModel> profiles)
    {
        var own = profiles.Where(p => p.Kind == ProfileKind.Server && !p.Model.IsHosted).Select(p => PlayTargets.Own(p.Model)).OfType<PlayTarget>();
        return [new MainViewModel.DefaultServerOption("", Loc.T("play.noServer")),
            .. own.Concat(PlayTargets.Favorites(client.DataDir)).Select(t => new MainViewModel.DefaultServerOption(t.Key, $"{t.Name}  ·  {t.Address}"))];
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
}
