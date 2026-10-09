using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;
using Newtonsoft.Json;

namespace eViSTool.App.ViewModels;

/// <summary>Строка списка объявлений: текст, интервал в минутах, включено ли.</summary>
public sealed partial class AnnouncementRowViewModel : ObservableObject
{
    private readonly ServerAnnouncementsViewModel _owner;

    public AnnouncementRowViewModel(ServerAnnouncementsViewModel owner, Announcement a)
    {
        _owner = owner;
        _text = a.Text;
        _intervalText = a.IntervalMinutes.ToString();
        _enabled = a.Enabled;
    }

    [ObservableProperty] private string _text;
    [ObservableProperty] private string _intervalText;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _intervalBad;

    partial void OnTextChanged(string value) => _owner.Changed();
    partial void OnIntervalTextChanged(string value) => _owner.Changed();
    partial void OnEnabledChanged(bool value) => _owner.Changed();

    /// <summary>Интервал разобрался — объявление; нет — null (строку подсвечиваем, файл не трогаем).</summary>
    internal Announcement? ToModel()
    {
        var ok = int.TryParse(IntervalText.Trim(), out var minutes) && minutes is >= Announcement.MinInterval and <= Announcement.MaxInterval;
        IntervalBad = !ok;
        return ok ? new Announcement(Text, minutes, Enabled) : null;
    }
}

/// <summary>
/// Вкладка «Объявления» раздела «Сервер»: сообщения в чат по расписанию (их отправляет агент — окно можно закрыть)
/// и разовое «Сказать сейчас». Настройки — в своём файле рядом с расписанием; у удалённого сервера — через агента.
/// Правки уходят сами, с небольшой задержкой, пока печатаешь; правки из другого окна подхватываются.
/// </summary>
public sealed partial class ServerAnnouncementsViewModel : ObservableObject
{
    private readonly ServerViewModel _server;
    private GameProfile? _profile;
    private bool _active;
    private bool _loading;
    private int _generation;
    private int _saveTicket;
    private ServerAnnouncements _saved = new();
    private DateTime? _seenChange;

    public ServerAnnouncementsViewModel(ServerViewModel server) => _server = server;

    public ObservableCollection<AnnouncementRowViewModel> Items { get; } = [];
    public bool HasItems => Items.Count > 0;

    [ObservableProperty] private bool _onlyWithPlayers = true;
    [ObservableProperty] private string _sayText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _loaded;

    /// <summary>«Сказать сейчас» — только работающему серверу под eViSTool.</summary>
    public bool CanSay => _server.Client is not null && _server.State == ServerState.Running;

    partial void OnOnlyWithPlayersChanged(bool value) => Changed();
    partial void OnSayTextChanged(string value) => SayCommand.NotifyCanExecuteChanged();

    public void OnServerStateChanged()
    {
        OnPropertyChanged(nameof(CanSay));
        SayCommand.NotifyCanExecuteChanged();
    }

    public void OnProfileSwitched(GameProfile? profile)
    {
        _profile = profile;
        _generation++;
        _savedTicket = ++_saveTicket; // отложенное сохранение старого профиля отменено — ждать нечего
        _seenChange = null;
        _saved = new();
        Loaded = false;
        StatusText = ErrorText = "";
        Show(new ServerAnnouncements());
        if (_active) _ = LoadAsync();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active) _ = LoadAsync();
    }

    /// <summary>Статус агента: объявления поменяли (в другом окне) — перечитать; связь с удалённым появилась — прочитать.</summary>
    public void ShowStatus(AgentStatus? s)
    {
        OnServerStateChanged();
        if (s is null || _profile is not { IsRemote: true }) return;
        if (!Loaded || s.AnnouncementsChangedAt != _seenChange)
        {
            _seenChange = s.AnnouncementsChangedAt;
            if (_active) _ = LoadAsync();
        }
    }

    /// <summary>Сервер на этой машине: файл поменяли в другом окне — перечитать.</summary>
    public void PollLocal()
    {
        if (!_active || _profile is not { IsRemote: false } p) return;
        var stamp = ServerAnnouncements.ChangedAt(p.Id);
        if (stamp == _seenChange) return;
        _seenChange = stamp;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var generation = ++_generation;
        try
        {
            ServerAnnouncements settings;
            if (_profile is { IsRemote: true })
            {
                if (_server.Client is not { } client) return; // связи нет — прочитаем, когда появится
                settings = await client.GetAnnouncementsAsync();
            }
            else if (_profile is { } p)
            {
                _seenChange = ServerAnnouncements.ChangedAt(p.Id);
                settings = ServerAnnouncements.Load(p.Id);
            }
            else return;
            if (generation != _generation) return;
            // то же, что мы сами только что записали, или человек сейчас печатает — поля не трогаем
            if (Loaded && (Same(settings, _saved) || _saveTicket != _savedTicket)) return;
            _saved = settings;
            Show(settings);
            Loaded = true;
            ErrorText = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException or JsonException)
        {
            if (generation != _generation) return;
            ErrorText = ex.Message.StartsWith("404") ? Loc.T("ann.agentOld")
                : _profile is { IsRemote: true } ? RemoteSecret.Describe(ex) : ex.Message;
        }
    }

    private void Show(ServerAnnouncements settings)
    {
        _loading = true;
        try
        {
            OnlyWithPlayers = settings.OnlyWithPlayers;
            Items.Clear();
            foreach (var a in settings.Items) Items.Add(new AnnouncementRowViewModel(this, a));
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(HasItems));
    }

    private static bool Same(ServerAnnouncements a, ServerAnnouncements b) => JsonConvert.SerializeObject(a) == JsonConvert.SerializeObject(b);

    private int _savedTicket;

    /// <summary>Что-то поправили: сохранить чуть позже (пока печатают — не на каждую букву).</summary>
    internal void Changed()
    {
        if (_loading || !Loaded) return;
        var ticket = ++_saveTicket;
        _ = SaveLaterAsync(ticket);
    }

    private async Task SaveLaterAsync(int ticket)
    {
        await Task.Delay(700);
        if (ticket != _saveTicket || _profile is not { } profile) return;
        var rows = Items.Select(r => r.ToModel()).ToList();
        if (rows.Any(r => r is null))
        {
            ErrorText = Loc.T("ann.intervalBad", Announcement.MinInterval, Announcement.MaxInterval);
            _savedTicket = ticket;
            return;
        }
        var settings = new ServerAnnouncements { OnlyWithPlayers = OnlyWithPlayers, Items = [.. rows!] };
        try
        {
            if (profile.IsRemote)
            {
                if (_server.Client is not { } client) throw new InvalidOperationException(Loc.T("server.stateOffline"));
                await client.SaveAnnouncementsAsync(settings);
            }
            else
            {
                await Task.Run(() => settings.Save(profile.Id));
                _seenChange = ServerAnnouncements.ChangedAt(profile.Id);
            }
            _saved = settings;
            ErrorText = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException)
        {
            ErrorText = Loc.T("ann.saveFailed", profile.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
        finally
        {
            if (ticket == _saveTicket) _savedTicket = ticket;
        }
    }

    [RelayCommand]
    private void Add()
    {
        Items.Add(new AnnouncementRowViewModel(this, new Announcement("", 30)));
        OnPropertyChanged(nameof(HasItems));
        Changed();
    }

    [RelayCommand]
    private void Delete(AnnouncementRowViewModel? row)
    {
        if (row is null) return;
        Items.Remove(row);
        OnPropertyChanged(nameof(HasItems));
        Changed();
    }

    private bool CanSayNow() => CanSay && SayText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSayNow))]
    private async Task Say()
    {
        if (_server.Client is not { } client) return;
        var text = SayText.Trim();
        try
        {
            await client.CommandAsync("/announce " + text);
            SayText = "";
            StatusText = Loc.T("ann.said", text);
            ErrorText = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            ErrorText = ex.Message;
        }
    }
}
