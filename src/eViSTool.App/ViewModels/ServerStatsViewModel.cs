using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.App.ViewModels;

/// <summary>Строка таблицы игроков: имя, полоса наигранного (доля от лучшего), сеансы, когда был.</summary>
public sealed record PlayerStatsRow(string Name, string PlayedText, double Share, int Sessions, string LastSeenText, bool Online)
{
    /// <summary>Ширина полосы в пикселях: у лучшего игрока — 90.</summary>
    public double BarWidth => Math.Max(3, 90 * Share);
}

/// <summary>
/// Вкладка «Статистика»: тумблер сбора (по умолчанию выключен), срок, плитки, графики, игроки. Данные собирает агент
/// на машине с сервером; итоги считает он же (или окно по файлам — для сервера на этой машине).
/// </summary>
public sealed partial class ServerStatsViewModel : ObservableObject
{
    private readonly ServerViewModel _server;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(60) };
    private GameProfile? _profile;
    private bool _active, _applying;
    private int _generation;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private StatsPeriod _period = StatsPeriod.Week;
    [ObservableProperty] private StatsReport? _report;
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty] private string _totalPlayedText = "—";
    [ObservableProperty] private string _peakText = "—";
    [ObservableProperty] private string _peakAtText = "";
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private string _restartsText = "";
    [ObservableProperty] private string _crashesText = "0";
    [ObservableProperty] private string _lastCrashText = "";
    [ObservableProperty] private bool _hasCrashes;
    [ObservableProperty] private bool _hasData;

    public ObservableCollection<PlayerStatsRow> Players { get; } = [];
    public bool HasPlayers => Players.Count > 0;
    public string PlayersTitle => Loc.T(Period switch
    {
        StatsPeriod.Day => "stats.playersDay",
        StatsPeriod.Week => "stats.playersWeek",
        _ => "stats.playersMonth",
    });

    public ServerStatsViewModel(ServerViewModel server)
    {
        _server = server;
        _timer.Tick += (_, _) => _ = LoadAsync(_generation);
    }

    private IServerData? Data => _server.Schedule.Data;

    public void OnProfileSwitched(GameProfile? profile)
    {
        if (profile?.Id == _profile?.Id && profile?.IsRemote == _profile?.IsRemote) return;
        _profile = profile;
        _generation++;
        Loaded = false;
        StatusText = "";
        Show(null);
        if (_active) _ = LoadAsync(_generation);
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            _ = LoadAsync(_generation);
            _timer.Start();
        }
        else _timer.Stop();
    }

    /// <summary>Связь с удалённым агентом появилась — дочитать, если вкладка открыта и ещё пустая.</summary>
    public void ShowStatus(AgentStatus? status)
    {
        if (status is not null && _active && !Loaded) _ = LoadAsync(_generation);
    }

    partial void OnPeriodChanged(StatsPeriod value)
    {
        OnPropertyChanged(nameof(PlayersTitle));
        _ = LoadAsync(_generation);
    }

    /// <summary>Графиков ещё нет: сбор выключен — что он даст; включён — что скоро появится.</summary>
    public string EmptyText => Loc.T(Enabled ? "stats.waiting" : "stats.off");

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(EmptyText));
        if (_applying || !Loaded || Data is not { } data) return;
        _ = ToggleAsync(data, value);
    }

    private async Task ToggleAsync(IServerData data, bool on)
    {
        try
        {
            await data.SetStatsEnabledAsync(on);
            StatusText = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException
                                       or TaskCanceledException)
        {
            StatusText = Loc.T("stats.failed", data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
        await LoadAsync(_generation);
    }

    private async Task LoadAsync(int generation)
    {
        if (Data is not { } data) return;
        try
        {
            var report = await data.LoadStatsAsync(Period);
            if (generation != _generation) return;
            Show(report);
            Loaded = true;
            if (StatusText.Length > 0 && !StatusText.StartsWith(Loc.T("stats.failed", ""), StringComparison.Ordinal)) StatusText = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException
                                       or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            if (generation != _generation) return;
            // старый агент не знает /stats — подскажем обновить его
            StatusText = data.IsRemote && ex.Message.StartsWith("404", StringComparison.Ordinal)
                ? Loc.T("stats.oldAgent")
                : Loc.T("stats.failed", data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
    }

    private void Show(StatsReport? r)
    {
        _applying = true;
        try
        {
            Enabled = r?.Enabled ?? false;
        }
        finally
        {
            _applying = false;
        }
        Report = r;
        HasData = r?.HasData ?? false;
        var c = Loc.Culture;
        TotalPlayedText = r is null ? "—" : Duration(r.TotalPlayed);
        PeakText = r is { PeakAt: not null } ? r.PeakPlayers.ToString(c) : "—";
        PeakAtText = r?.PeakAt is { } at ? When(at) : "";
        UptimeText = r?.Uptime is { } u ? (u * 100).ToString("0", c) + " %" : "—";
        RestartsText = r is null ? "" : Loc.Plural("stats.starts", r.Restarts);
        CrashesText = (r?.Crashes.Count ?? 0).ToString(c);
        HasCrashes = r?.Crashes.Count > 0;
        LastCrashText = r?.Crashes.Count > 0 ? When(r.Crashes.Max()) : "";

        Players.Clear();
        var best = r?.Players.FirstOrDefault()?.Played.Ticks ?? 0;
        foreach (var p in r?.Players ?? [])
            Players.Add(new PlayerStatsRow(p.Name, Duration(p.Played), best > 0 ? (double)p.Played.Ticks / best : 0, p.Sessions,
                p.Online ? Loc.T("stats.online") : When(p.LastSeen), p.Online));
        OnPropertyChanged(nameof(HasPlayers));
    }

    [RelayCommand]
    private async Task Clear()
    {
        if (Data is not { } data) return;
        if (MessageBox.Show(Application.Current.MainWindow!, Loc.T("stats.clearAsk"), "eViSTool", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            await data.ClearStatsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException
                                       or TaskCanceledException)
        {
            StatusText = Loc.T("stats.failed", data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
        await LoadAsync(_generation);
    }

    /// <summary>«17 ч 05 мин», «45 мин».</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? Loc.T("stats.hoursMinutes", (int)t.TotalHours, t.Minutes.ToString("00")) : Loc.T("stats.minutes", (int)t.TotalMinutes);

    /// <summary>«сегодня, 21:14», «вчера, 23:48», «чт 08.10, 21:14».</summary>
    public static string When(DateTime t)
    {
        var time = t.ToString("HH:mm", Loc.Culture);
        if (t.Date == DateTime.Today) return Loc.T("stats.today", time);
        if (t.Date == DateTime.Today.AddDays(-1)) return Loc.T("stats.yesterday", time);
        return t.ToString("ddd dd.MM, ", Loc.Culture) + time;
    }
}
