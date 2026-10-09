using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core;
using eViSTool.Core.Localization;
using eViSTool.Core.Notifications;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;
using Newtonsoft.Json;

namespace eViSTool.App.ViewModels;

/// <summary>Пересечение «событие × канал»: тумблер.</summary>
public sealed partial class NotifyCellViewModel(ServerNotifyViewModel owner, string channelId, string channelName, bool on) : ObservableObject
{
    public string ChannelId { get; } = channelId;
    public string ChannelName { get; } = channelName;
    [ObservableProperty] private bool _isOn = on;

    partial void OnIsOnChanged(bool value) => owner.Changed();
}

/// <summary>Строка таблицы: событие и тумблеры по каналам.</summary>
public sealed class NotifyEventRowViewModel(NotifyEvent e, string title, string hint, IReadOnlyList<NotifyCellViewModel> cells)
{
    public NotifyEvent Event { get; } = e;
    public string Title { get; } = title;
    public string Hint { get; } = hint;
    public IReadOnlyList<NotifyCellViewModel> Cells { get; } = cells;
}

/// <summary>
/// Вкладка «Оповещения» раздела «Сервер»: какие события в какие каналы (каналы — из «Настроек»). Отправляет агент, так
/// что окно можно закрыть. Каналы, включённые у сервера, копируются ему вместе с секретами: своему серверу — в файл рядом
/// с расписанием, удалённому — по защищённому соединению (там их шифруют заново). Каждое открытие вкладки обновляет копии —
/// поправленный в «Настройках» токен доедет до сервера сам.
/// </summary>
public sealed partial class ServerNotifyViewModel : ObservableObject
{
    private static readonly NotifyEvent[] Events = Enum.GetValues<NotifyEvent>();

    private readonly ServerViewModel _server;
    private GameProfile? _profile;
    private bool _active;
    private bool _loading;
    private int _generation;
    private DateTime? _seenChange;
    private ServerNotifySettings _saved = new();
    private List<NotifyChannel> _channels = [];

    public ServerNotifyViewModel(ServerViewModel server) => _server = server;

    public ObservableCollection<NotifyEventRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> ChannelNames { get; } = [];
    public bool HasChannels => ChannelNames.Count > 0;

    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _isTesting;

    public void OnProfileSwitched(GameProfile? profile)
    {
        _profile = profile;
        _generation++;
        _seenChange = null;
        _saved = new();
        Loaded = false;
        StatusText = ErrorText = "";
        Build(new ServerNotifySettings());
        if (_active) _ = LoadAsync(resync: true);
    }

    /// <summary>Вкладку открыли: каналы из «Настроек» и настройки сервера — заново, копии каналов у сервера — обновить.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active) _ = LoadAsync(resync: true);
    }

    public void OnLanguageChanged() => Build(_saved);

    public void ShowStatus(AgentStatus? s)
    {
        TestCommand.NotifyCanExecuteChanged();
        if (s is null || _profile is not { IsRemote: true } || !_active) return;
        if (!Loaded || s.NotifyChangedAt != _seenChange)
        {
            _seenChange = s.NotifyChangedAt;
            _ = LoadAsync(resync: !Loaded);
        }
    }

    public void PollLocal()
    {
        if (!_active || _profile is not { IsRemote: false } p) return;
        var stamp = ServerNotifySettings.ChangedAt(p.Id);
        if (stamp == _seenChange) return;
        _seenChange = stamp;
        _ = LoadAsync(resync: false);
    }

    private async Task LoadAsync(bool resync)
    {
        var generation = ++_generation;
        _channels = NotifyChannels.Load(AppPaths.Root);
        try
        {
            ServerNotifySettings settings;
            if (_profile is { IsRemote: true })
            {
                if (_server.Client is not { } client)
                {
                    Build(_saved); // связи нет — хотя бы столбцы каналов; прочитаем, когда появится
                    return;
                }
                settings = await client.GetNotifyAsync();
            }
            else if (_profile is { } p)
            {
                _seenChange = ServerNotifySettings.ChangedAt(p.Id);
                settings = ServerNotifySettings.Load(p.Id);
            }
            else return;
            if (generation != _generation) return;
            _saved = settings;
            Build(settings);
            Loaded = true;
            ErrorText = "";
            // поправленные в «Настройках» каналы (новый токен, другой чат) — до сервера
            if (resync && settings.Routes.Values.Any(v => v.Count > 0)) await SaveAsync(quiet: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException or JsonException)
        {
            if (generation != _generation) return;
            ErrorText = ex.Message.StartsWith("404") ? Loc.T("srvnotify.agentOld")
                : _profile is { IsRemote: true } ? RemoteSecret.Describe(ex) : ex.Message;
        }
    }

    /// <summary>Таблица: строки — события, столбцы — каналы из «Настроек».</summary>
    private void Build(ServerNotifySettings settings)
    {
        _loading = true;
        try
        {
            ChannelNames.Clear();
            foreach (var c in _channels) ChannelNames.Add(c.Name);
            Rows.Clear();
            foreach (var e in Events)
                Rows.Add(new NotifyEventRowViewModel(e, Title(e), Hint(e),
                    [.. _channels.Select(c => new NotifyCellViewModel(this, c.Id, c.Name, settings.IsOn(e, c.Id)))]));
        }
        finally { _loading = false; }
        OnPropertyChanged(nameof(HasChannels));
        TestCommand.NotifyCanExecuteChanged();
    }

    internal void Changed()
    {
        if (_loading || !Loaded) return;
        _ = SaveAsync(quiet: false);
    }

    /// <summary>Настройки из таблицы: маршруты и копии включённых каналов (с секретами этого компьютера).</summary>
    private ServerNotifySettings Collect()
    {
        var routes = Rows.ToDictionary(r => r.Event, r => r.Cells.Where(c => c.IsOn).Select(c => c.ChannelId).ToList());
        var used = routes.Values.SelectMany(v => v).ToHashSet();
        return new ServerNotifySettings
        {
            ServerName = _profile?.Name,
            Routes = routes.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
            Channels = [.. _channels.Where(c => used.Contains(c.Id))],
        };
    }

    private async Task SaveAsync(bool quiet)
    {
        if (_profile is not { } profile) return;
        var settings = Collect();
        try
        {
            if (profile.IsRemote)
            {
                if (_server.Client is not { } client) throw new InvalidOperationException(Loc.T("server.stateOffline"));
                await client.SaveNotifyAsync(settings.ToUpload());
            }
            else
            {
                await Task.Run(() => settings.Save(profile.Id));
                _seenChange = ServerNotifySettings.ChangedAt(profile.Id);
            }
            _saved = settings;
            ErrorText = "";
            if (!quiet) StatusText = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException)
        {
            ErrorText = Loc.T("srvnotify.saveFailed", profile.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
    }

    private bool CanTest() => !IsTesting && _saved.Routes.Values.Any(v => v.Count > 0);

    /// <summary>Проверочное — так, как пойдут настоящие: с сервера (через агента), во все включённые каналы.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task Test()
    {
        IsTesting = true;
        TestCommand.NotifyCanExecuteChanged();
        StatusText = Loc.T("srvnotify.testing");
        ErrorText = "";
        try
        {
            IReadOnlyList<string> errors;
            if (_server.Client is { } client) errors = await client.TestNotifyAsync();
            else if (_profile is { IsRemote: false } p)
                // агент своего сервера не запущен — отправляем отсюда же: машина та же, каналы те же
                errors = await new ServerNotifier(() => ServerNotifySettings.Load(p.Id), NotifyChannelsViewModel.Http, (_, _) => { })
                    .TestAsync(Loc.T("notify.testTitle"), new NotifyLine("📡", Loc.T("notify.testFromServer", Environment.MachineName)));
            else throw new InvalidOperationException(Loc.T("server.stateOffline"));
            StatusText = errors.Count == 0 ? Loc.T("srvnotify.testSent") : "";
            ErrorText = string.Join(Environment.NewLine, errors);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusText = "";
            ErrorText = ex.Message.StartsWith("404") ? Loc.T("srvnotify.agentOld") : ex.Message;
        }
        finally
        {
            IsTesting = false;
            TestCommand.NotifyCanExecuteChanged();
        }
    }

    // явные ключи — без склейки имён (LocalizationTests проверяет, что все ключи есть)
    private static string Title(NotifyEvent e) => e switch
    {
        NotifyEvent.ServerCrashed => Loc.T("srvnotify.crashed"),
        NotifyEvent.StartFailed => Loc.T("srvnotify.startFailed"),
        NotifyEvent.BackupFailed => Loc.T("srvnotify.backupFailed"),
        NotifyEvent.ModsUpdated => Loc.T("srvnotify.modsUpdated"),
        NotifyEvent.ServerStarted => Loc.T("srvnotify.started"),
        NotifyEvent.ServerStopped => Loc.T("srvnotify.stopped"),
        NotifyEvent.RestartSoon => Loc.T("srvnotify.restartSoon"),
        NotifyEvent.PlayerJoined => Loc.T("srvnotify.joined"),
        NotifyEvent.PlayerLeft => Loc.T("srvnotify.left"),
        _ => e.ToString(),
    };

    private static string Hint(NotifyEvent e) => e switch
    {
        NotifyEvent.ServerCrashed => Loc.T("srvnotify.crashedHint"),
        NotifyEvent.StartFailed => Loc.T("srvnotify.startFailedHint"),
        NotifyEvent.BackupFailed => Loc.T("srvnotify.backupFailedHint"),
        NotifyEvent.ModsUpdated => Loc.T("srvnotify.modsUpdatedHint"),
        NotifyEvent.ServerStarted => Loc.T("srvnotify.startedHint"),
        NotifyEvent.ServerStopped => Loc.T("srvnotify.stoppedHint"),
        NotifyEvent.RestartSoon => Loc.T("srvnotify.restartSoonHint"),
        NotifyEvent.PlayerJoined => Loc.T("srvnotify.joinedHint"),
        NotifyEvent.PlayerLeft => Loc.T("srvnotify.leftHint"),
        _ => "",
    };
}
