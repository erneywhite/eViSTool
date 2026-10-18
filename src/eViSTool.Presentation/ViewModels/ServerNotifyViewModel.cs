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

namespace eViSTool.Presentation.ViewModels;

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
/// поправленный в «Настройках» токен доедет до сервера сам. У сервера у хостера агента нет: настройки лежат здесь же,
/// отправляет само окно (<see cref="HostedWatch"/>), событий меньше, а чата в Discord нет (<see cref="IsHosted"/>).
/// </summary>
public sealed partial class ServerNotifyViewModel : ObservableObject
{
    private static readonly NotifyEvent[] Events = Enum.GetValues<NotifyEvent>();

    private readonly ServerViewModel _server;

    /// <summary>Вкладка сервера у хостера: события — только видные по логу и окну, без чата в Discord, отправляет окно.</summary>
    public bool IsHosted { get; }

    /// <summary>Настройки сохранены (сторож оповещений хостера пересобирает список сразу).</summary>
    public event Action? Saved;

    /// <summary>Строки таблицы: у хостера — только его события, в том же порядке.</summary>
    private IEnumerable<NotifyEvent> Shown => IsHosted ? Events.Where(HostedWatch.Events.Contains) : Events;

    /// <summary>Кнопка проверочного: у хостера оно уходит отсюда, а не «с сервера».</summary>
    public string TestText => IsHosted ? Loc.T("notify.test") : Loc.T("srvnotify.test");

    public string TestTip => IsHosted ? Loc.T("srvnotify.hostedTestTip") : Loc.T("srvnotify.testTip");
    private GameProfile? _profile;
    private bool _active;
    private bool _loading;
    private int _generation;
    private DateTime? _seenChange;
    private ServerNotifySettings _saved = new();
    private List<NotifyChannel> _channels = [];

    /// <param name="hosted">Вкладка сервера у хостера (<see cref="IsHosted"/>).</param>
    public ServerNotifyViewModel(ServerViewModel server, bool hosted = false)
    {
        _server = server;
        IsHosted = hosted;
    }

    public ObservableCollection<NotifyEventRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> ChannelNames { get; } = [];
    public bool HasChannels => ChannelNames.Count > 0;

    [ObservableProperty] private bool _loaded;

    // ---- чат игры в Discord: канал из «Настроек» (только Discord) и входы-выходы

    /// <summary>Вариант выбора канала для чата: ID ("" — выключено) и имя.</summary>
    public sealed record ChatChannelOption(string Id, string Title);

    public ObservableCollection<ChatChannelOption> ChatChannels { get; } = [];
    [ObservableProperty] private string _chatChannelId = "";
    [ObservableProperty] private bool _chatJoins = true;
    public bool ChatOn => ChatChannelId.Length > 0;
    public bool HasDiscordChannels => ChatChannels.Count > 1;

    partial void OnChatChannelIdChanged(string value)
    {
        OnPropertyChanged(nameof(ChatOn));
        Changed();
    }

    partial void OnChatJoinsChanged(bool value) => Changed();
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

    public void OnLanguageChanged()
    {
        Build(_saved);
        OnPropertyChanged(nameof(TestText));
        OnPropertyChanged(nameof(TestTip));
    }

    /// <summary>Оповещение сервера у хостера не ушло (его шлёт окно) — сказать здесь, если открыт этот профиль.</summary>
    public void ShowSendError(string profileId, string text)
    {
        if (IsHosted && _profile?.Id == profileId) ErrorText = text;
    }

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
            if (resync && (settings.Routes.Values.Any(v => v.Count > 0) || settings.ChatChannelId is not null)) await SaveAsync(quiet: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException or JsonException)
        {
            if (generation != _generation) return;
            ErrorText = AgentHttpException.IsOldAgent(ex) ? Loc.T("srvnotify.agentOld")
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
            ChatChannels.Clear();
            ChatChannels.Add(new ChatChannelOption("", Loc.T("chat.off")));
            foreach (var c in _channels.Where(c => c.Kind == NotifyKind.Discord)) ChatChannels.Add(new ChatChannelOption(c.Id, c.Name));
            ChatChannelId = settings.ChatChannelId is { } chat && _channels.Any(c => c.Id == chat && c.Kind == NotifyKind.Discord) ? chat : "";
            ChatJoins = settings.ChatJoins;
            OnPropertyChanged(nameof(HasDiscordChannels));
            Rows.Clear();
            foreach (var e in Shown)
                Rows.Add(new NotifyEventRowViewModel(e, Title(e), IsHosted ? HostedHint(e) : Hint(e),
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
        var chat = IsHosted ? "" : ChatChannelId; // чат игры в Discord — только у своего сервера
        if (chat.Length > 0) used.Add(chat);
        return new ServerNotifySettings
        {
            ServerName = _profile?.Name,
            ChatChannelId = chat.Length > 0 ? chat : null,
            ChatJoins = ChatJoins,
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
            Saved?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException)
        {
            ErrorText = Loc.T("srvnotify.saveFailed", profile.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
    }

    /// <summary>
    /// «Открыть настройки» в подсказке «каналов ещё нет»: переход в «Настройки», где каналы добавляют. То же, что делает
    /// одноимённая команда раздела «Сервер».
    /// </summary>
    [RelayCommand]
    private void OpenSettings() => _server.OpenSettingsCommand.Execute(null);

    private bool CanTest() => !IsTesting && _saved.Routes.Values.Any(v => v.Count > 0);

    /// <summary>
    /// Проверочное — так, как пойдут настоящие: с сервера (через агента), во все включённые каналы. У хостера агента
    /// нет — настоящие шлёт это окно, и проверочное тоже.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task Test()
    {
        IsTesting = true;
        TestCommand.NotifyCanExecuteChanged();
        StatusText = IsHosted ? Loc.T("srvnotify.hostedTesting") : Loc.T("srvnotify.testing");
        ErrorText = "";
        try
        {
            IReadOnlyList<string> errors;
            if (!IsHosted && _server.Client is { } client) errors = await client.TestNotifyAsync();
            else if (_profile is { IsRemote: false } p)
                // агент своего сервера не запущен (или это сервер у хостера) — отправляем отсюда же: каналы те же
                errors = await new ServerNotifier(() => ServerNotifySettings.Load(p.Id), NotifyChannelsViewModel.Http, (_, _) => { })
                    .TestAsync(Loc.T("notify.testTitle"), new NotifyLine("◇", Loc.T("notify.lbl.from"), IsHosted
                        ? Loc.T("notify.testFromWindow", Environment.MachineName)
                        : Loc.T("notify.testFromServer", Environment.MachineName)));
            else throw new InvalidOperationException(Loc.T("server.stateOffline"));
            StatusText = errors.Count == 0 ? Loc.T("srvnotify.testSent") : "";
            ErrorText = string.Join(Environment.NewLine, errors);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            StatusText = "";
            ErrorText = AgentHttpException.IsOldAgent(ex) ? Loc.T("srvnotify.agentOld") : ex.Message;
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
        NotifyEvent.ModUpdates => Loc.T("srvnotify.modUpdates"),
        NotifyEvent.LowDisk => Loc.T("srvnotify.lowDisk"),
        NotifyEvent.Overloaded => Loc.T("srvnotify.overloaded"),
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
        NotifyEvent.ModUpdates => Loc.T("srvnotify.modUpdatesHint"),
        NotifyEvent.LowDisk => Loc.T("srvnotify.lowDiskHint"),
        NotifyEvent.Overloaded => Loc.T("srvnotify.overloadedHint"),
        NotifyEvent.ServerStarted => Loc.T("srvnotify.startedHint"),
        NotifyEvent.ServerStopped => Loc.T("srvnotify.stoppedHint"),
        NotifyEvent.RestartSoon => Loc.T("srvnotify.restartSoonHint"),
        NotifyEvent.PlayerJoined => Loc.T("srvnotify.joinedHint"),
        NotifyEvent.PlayerLeft => Loc.T("srvnotify.leftHint"),
        _ => "",
    };

    /// <summary>Подсказки у хостера: где событие узнаётся иначе, чем у агента (по логу, раз в 6 часов, окном), — свои.</summary>
    private static string HostedHint(NotifyEvent e) => e switch
    {
        NotifyEvent.ServerCrashed => Loc.T("srvnotify.hostedCrashedHint"),
        NotifyEvent.ServerStopped => Loc.T("srvnotify.hostedStoppedHint"),
        NotifyEvent.ModUpdates => Loc.T("srvnotify.hostedModUpdatesHint"),
        NotifyEvent.ModsUpdated => Loc.T("srvnotify.hostedModsUpdatedHint"),
        _ => Hint(e),
    };
}
