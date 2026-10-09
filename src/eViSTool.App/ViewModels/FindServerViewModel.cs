using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Game;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;

namespace eViSTool.App.ViewModels;

/// <summary>Строка окна «Найти сервер»: сервер из общего списка или из избранного игры.</summary>
public sealed partial class ServerRowViewModel : ObservableObject
{
    private readonly FindServerViewModel _owner;

    public ServerRowViewModel(FindServerViewModel owner, PublicServer? server, PlayTarget? favorite)
    {
        _owner = owner;
        Server = server;
        Favorite = favorite;
    }

    public PublicServer? Server { get; }
    public PlayTarget? Favorite { get; }
    public bool IsFavoriteRow => Favorite is not null;

    public string Name => Server?.Name ?? Favorite!.Name;
    public string Address => Server?.Address ?? Favorite!.Address;
    public bool HasPassword => Server?.HasPassword ?? Favorite!.HasPassword;
    public bool Whitelisted => Server?.Whitelisted == true;
    public string PlayersText => Server is { } s ? $"{s.Players} / {s.MaxPlayers}" : "";
    public bool HasPlayers => Server is not null;

    /// <summary>«1.22.7 · выживание · 158 модов» — без пароля и белого списка (у них свои значки).</summary>
    public string Details => Server is not { } s ? Address
        : string.Join(" · ", new[]
        {
            s.GameVersion, FindServerViewModel.PlaystyleText(s.Playstyle),
            s.Mods.Count == 0 ? Loc.T("browse.noMods") : Loc.Plural("browse.mods", s.Mods.Count),
        }.Where(x => !string.IsNullOrEmpty(x)));

    public string Description => Server?.Description is { Length: > 0 } d ? d : Loc.T("browse.noDescription");

    /// <summary>«Моды · 158 — у тебя есть 41, остальные игра скачает сама при входе».</summary>
    public string ModsSummary => Server is not { Mods.Count: > 0 } s ? ""
        : Loc.T("browse.modsSummary", s.Mods.Count, s.Mods.Count(m => _owner.HasMod(m.Id)));

    public IEnumerable<string> ModTags => Server is not { } s ? []
        : s.Mods.Take(30).Select(m => $"{m.Id} {m.Version}").Concat(s.Mods.Count > 30 ? [Loc.T("browse.modsMore", s.Mods.Count - 30)] : []);

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _inFavorites;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// Окно «Найти сервер»: общий список серверов (тот же, что в браузере серверов игры) с поиском и фильтрами, и избранное
/// игры целиком. «Играть» запускает игру сразу с подключением, «В избранное» пишет в избранное игры (при закрытой игре).
/// </summary>
public sealed partial class FindServerViewModel : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly MainViewModel _main;
    private readonly GameProfile _client;
    private readonly string? _clientVersion;
    private HashSet<string> _myMods = new(StringComparer.OrdinalIgnoreCase);

    public FindServerViewModel(MainViewModel main, ProfileViewModel client)
    {
        _main = main;
        _client = client.Model;
        _clientVersion = client.Resolved.GameVersion?.ToString();
        MasterUrl = ServerBrowser.MasterUrl(_client.DataDir);
        View = CollectionViewSource.GetDefaultView(All);
        View.Filter = o => o is ServerRowViewModel r && Matches(r);
        _ = Task.Run(() =>
        {
            // моды профиля — для «у тебя есть N из M»; не прочитались — не беда
            try { _myMods = [.. ModUpdateService.ScanLocal(client.Resolved).Select(m => m.Info?.ModId).OfType<string>()]; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
        ReloadFavorites();
    }

    public string MasterUrl { get; }
    public string MasterHost => Uri.TryCreate(MasterUrl, UriKind.Absolute, out var u) ? u.Host : MasterUrl;
    public string VersionChip => Loc.T("browse.myVersion", _clientVersion ?? "?");

    public ObservableCollection<ServerRowViewModel> All { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<ServerRowViewModel> Favorites { get; } = [];

    [ObservableProperty] private bool _isFavoritesTab;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _myVersion = true;
    [ObservableProperty] private bool _hasSlots;
    [ObservableProperty] private bool _noPassword;
    [ObservableProperty] private bool _noWhitelist;
    [ObservableProperty] private bool _noMods;
    [ObservableProperty] private bool _withMods;
    [ObservableProperty] private bool _byName;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _countText = "";

    public string AllTabTitle => Loc.T("browse.tabAll", View.Cast<object>().Count());
    public string FavoritesTabTitle => Loc.T("browse.tabFavorites", Favorites.Count);
    public bool IsAllTab => !IsFavoritesTab;

    partial void OnIsFavoritesTabChanged(bool value) => OnPropertyChanged(nameof(IsAllTab));
    partial void OnSearchChanged(string value) => Refilter();
    partial void OnMyVersionChanged(bool value) => Refilter();
    partial void OnHasSlotsChanged(bool value) => Refilter();
    partial void OnNoPasswordChanged(bool value) => Refilter();
    partial void OnNoWhitelistChanged(bool value) => Refilter();
    partial void OnNoModsChanged(bool value)
    {
        if (value) WithMods = false;
        Refilter();
    }
    partial void OnWithModsChanged(bool value)
    {
        if (value) NoMods = false;
        Refilter();
    }
    partial void OnByNameChanged(bool value) => Sort();

    internal bool HasMod(string id) => _myMods.Contains(id);

    private bool Matches(ServerRowViewModel r)
    {
        if (r.Server is not { } s) return false;
        if (MyVersion && _clientVersion is { } v && s.GameVersion != v) return false;
        if (HasSlots && !s.HasSlots) return false;
        if (NoPassword && s.HasPassword) return false;
        if (NoWhitelist && s.Whitelisted) return false;
        if (NoMods && s.Mods.Count > 0) return false;
        if (WithMods && s.Mods.Count == 0) return false;
        var q = Search.Trim();
        return q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Refilter()
    {
        View.Refresh();
        UpdateCounts();
    }

    private void Sort()
    {
        View.SortDescriptions.Clear();
        if (ByName) View.SortDescriptions.Add(new SortDescription(nameof(ServerRowViewModel.Name), ListSortDirection.Ascending));
        // по игрокам — так список и приходит отсортированным ниже, при загрузке
        View.Refresh();
    }

    private void UpdateCounts()
    {
        var shown = View.Cast<object>().Count();
        CountText = Loc.T("browse.shown", shown, All.Count);
        OnPropertyChanged(nameof(AllTabTitle));
    }

    [RelayCommand]
    public async Task Load()
    {
        IsLoading = true;
        ErrorText = "";
        StatusText = Loc.T("browse.loading", MasterHost);
        try
        {
            var list = await ServerBrowser.FetchAsync(Http, MasterUrl);
            var favorites = PlayTargets.Favorites(_client.DataDir).Select(f => f.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
            All.Clear();
            foreach (var s in list.OrderByDescending(s => s.Players))
                All.Add(new ServerRowViewModel(this, s, null) { InFavorites = favorites.Contains(s.Address) });
            Sort();
            UpdateCounts();
            StatusText = Loc.T("browse.loaded", MasterHost, DateTime.Now.ToString("HH:mm"));
        }
        catch (InvalidOperationException ex)
        {
            StatusText = "";
            ErrorText = ex.Message;
        }
        finally { IsLoading = false; }
    }

    private void ReloadFavorites()
    {
        Favorites.Clear();
        foreach (var f in PlayTargets.Favorites(_client.DataDir)) Favorites.Add(new ServerRowViewModel(this, null, f));
        OnPropertyChanged(nameof(FavoritesTabTitle));
    }

    /// <summary>Игра этого профиля открыта — избранное не трогаем: она перезапишет его при выходе.</summary>
    private bool GameRunning() => GameProcess.FindClientPids(_client).Count > 0;

    /// <summary>Запустить игру с этим сервером. Пароль спросит окно (у избранного он уже сохранён). true — окно закрыть.</summary>
    public async Task<bool> PlayAsync(ServerRowViewModel row, Func<string, string?> askPassword)
    {
        PlayTarget target;
        if (row.Favorite is { } f) target = f;
        else
        {
            string? password = null;
            if (row.HasPassword && (password = askPassword(row.Name)) is null) return false;
            target = new PlayTarget("browse:" + row.Address, PlayTargetKind.Favorite, row.Name, row.Address, password);
        }
        await _main.PlayOnTargetAsync(target);
        return true;
    }

    /// <summary>В избранное игры (при закрытой игре); пароль — по желанию, если сервер с паролем.</summary>
    public void AddFavorite(ServerRowViewModel row, Func<string, string?> askPassword)
    {
        ErrorText = "";
        if (GameRunning())
        {
            ErrorText = Loc.T("browse.closeGame");
            return;
        }
        string? password = null;
        if (row.HasPassword && (password = askPassword(row.Name)) is null) return;
        try
        {
            GameFavorites.Add(_client.DataDir, row.Name, row.Address, password);
            row.InFavorites = true;
            ReloadFavorites();
            StatusText = Loc.T("browse.added", row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Newtonsoft.Json.JsonException)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>
    /// Добавить сервер вручную (old = null) или изменить запись избранного — как в меню игры: название, адрес, пароль.
    /// edit получает прежнюю запись и возвращает новую (null — отмена).
    /// </summary>
    public void SaveFavorite(PlayTarget? old, Func<PlayTarget?, (string Name, string Address, string? Password)?> edit)
    {
        ErrorText = "";
        if (GameRunning())
        {
            ErrorText = Loc.T("browse.closeGame");
            return;
        }
        if (edit(old) is not { } server) return;
        try
        {
            if (old is null) GameFavorites.Add(_client.DataDir, server.Name, server.Address, server.Password);
            else GameFavorites.Update(_client.DataDir, old.Address, server.Name, server.Address, server.Password);
            ReloadFavorites();
            var inFavorites = PlayTargets.Favorites(_client.DataDir).Select(f => f.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in All) r.InFavorites = inFavorites.Contains(r.Address);
            StatusText = Loc.T("browse.added", server.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Newtonsoft.Json.JsonException)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveFavorite(ServerRowViewModel? row)
    {
        if (row?.Favorite is not { } f) return;
        ErrorText = "";
        if (GameRunning())
        {
            ErrorText = Loc.T("browse.closeGame");
            return;
        }
        try
        {
            GameFavorites.RemoveAddress(_client.DataDir, f.Address);
            ReloadFavorites();
            foreach (var r in All.Where(r => string.Equals(r.Address, f.Address, StringComparison.OrdinalIgnoreCase))) r.InFavorites = false;
            StatusText = Loc.T("browse.removed", f.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Newtonsoft.Json.JsonException)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>Стиль игры по коду из списка; незнакомый — без подписи.</summary>
    internal static string PlaystyleText(string? langCode) => langCode switch
    {
        "surviveandbuild" or "surviveandbuild-bands" or "preset-surviveandbuild" => Loc.T("browse.styleSurvive"),
        "preset-exploration" => Loc.T("browse.styleExplore"),
        "preset-wildernesssurvival" => Loc.T("browse.styleWilderness"),
        "preset-homosapiens" => "Homo sapiens",
        "creativebuilding" or "preset-creativebuilding" => Loc.T("browse.styleCreative"),
        _ => "",
    };
}
