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

/// <summary>
/// Строка списка объявлений: текст, «каждые N минут» или «в 12:00, 18:00», включено ли. Время в поле — по часам окна,
/// в объявление (агенту) — по часам сервера.
/// </summary>
public sealed partial class AnnouncementRowViewModel : ObservableObject
{
    private readonly ServerAnnouncementsViewModel _owner;

    public AnnouncementRowViewModel(ServerAnnouncementsViewModel owner, Announcement a)
    {
        _owner = owner;
        _text = a.Text;
        _intervalText = a.IntervalMinutes.ToString();
        _enabled = a.Enabled;
        _isTimed = a.IsTimed;
        _timesText = string.Join(", ", (a.Times ?? []).Select(t => ServerScheduleViewModel.ShiftTime(t, owner.ClockShift)));
    }

    [ObservableProperty] private string _text;
    [ObservableProperty] private string _intervalText;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _intervalBad;

    /// <summary>В заданное время, а не через интервал.</summary>
    [ObservableProperty] private bool _isTimed;
    [ObservableProperty] private string _timesText;

    /// <summary>Выбор в строке: 0 — «каждые», 1 — «в».</summary>
    public int ModeIndex
    {
        get => IsTimed ? 1 : 0;
        set => IsTimed = value == 1;
    }

    partial void OnTextChanged(string value) => _owner.Changed();
    partial void OnIntervalTextChanged(string value) => _owner.Changed();
    partial void OnEnabledChanged(bool value) => _owner.Changed();
    partial void OnTimesTextChanged(string value) => _owner.Changed();

    partial void OnIsTimedChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeIndex));
        if (value && TimesText.Trim().Length == 0) TimesText = "12:00"; // пустое поле времени непонятно, с чего начать
        _owner.Changed();
    }

    /// <summary>Интервал или время разобрались — объявление; нет — null (строку подсвечиваем, файл не трогаем).</summary>
    internal Announcement? ToModel()
    {
        var intervalOk = int.TryParse(IntervalText.Trim(), out var minutes) && minutes is >= Announcement.MinInterval and <= Announcement.MaxInterval;
        if (!IsTimed)
        {
            IntervalBad = !intervalOk;
            return intervalOk ? new Announcement(Text, minutes, Enabled) : null;
        }
        var parts = TimesText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var timesOk = parts.Length > 0 && parts.All(t => ServerAutomation.TryTimeOfDay(t, out _));
        IntervalBad = !timesOk;
        return timesOk
            ? new Announcement(Text, intervalOk ? minutes : 30, Enabled, [.. parts.Select(t => ServerScheduleViewModel.ShiftTime(t, -_owner.ClockShift))])
            : null;
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

    /// <summary>Пояс машины с сервером (минуты от UTC; null — не знаем). Время объявлений в окне — по своим часам.</summary>
    private int? _serverOffset;

    /// <summary>На сколько минут часы окна впереди часов сервера.</summary>
    public int ClockShift => _serverOffset is { } server ? (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes - server : 0;

    /// <summary>Пояса разные — под списком: время по твоим часам, сервер живёт по своим.</summary>
    public string TimeNote => ClockShift == 0 ? "" : Loc.T("ann.timeNote", Utc((int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes), Utc(_serverOffset ?? 0));

    private static string Utc(int minutes) => minutes == 0 ? "UTC"
        : $"UTC{(minutes > 0 ? "+" : "−")}{Math.Abs(minutes) / 60}" + (Math.Abs(minutes) % 60 is var m and > 0 ? $":{m:00}" : "");

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
        if (s is not null && s.UtcOffsetMinutes != _serverOffset)
        {
            // пояс сервера стал известен — время в строках пересчитываем на свои часы (ничего не сохраняя)
            _serverOffset = s.UtcOffsetMinutes;
            OnPropertyChanged(nameof(TimeNote));
            if (Loaded && _saveTicket == _savedTicket) Show(_saved);
        }
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
            ErrorText = Items.Any(r => r.IsTimed && r.IntervalBad) ? Loc.T("ann.timesBad")
                : Loc.T("ann.intervalBad", Announcement.MinInterval, Announcement.MaxInterval);
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
