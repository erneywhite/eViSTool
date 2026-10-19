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
/// отправляет само окно (<see cref="HostedWatch"/>), событий меньше, а чата в Discord нет (<see cref="IsHosted"/>). Не
/// ушло оповещение у хостера — ошибка видна на вкладке, пока в этот канал не уйдёт удачное (<see cref="SendErrors"/>).
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
    private bool _saving;    // запись идёт (SaveAsync): новые щелчки только просят записать ещё раз
    private bool _saveAgain;
    private bool _saveLoud;  // среди этих просьб был щелчок, а не тихое обновление копий каналов при открытии
    private int _generation;
    private DateTime? _seenChange;
    private ServerNotifySettings _saved = new();
    private List<NotifyChannel> _channels = [];
    private HostedWatch? _watch;
    private string _sendShown = ""; // ошибки отправки, показанные в ErrorText последними (пересобираются при смене языка)

    /// <param name="hosted">Вкладка сервера у хостера (<see cref="IsHosted"/>).</param>
    public ServerNotifyViewModel(ServerViewModel server, bool hosted = false)
    {
        _server = server;
        IsHosted = hosted;
    }

    /// <summary>
    /// Сторож оповещений серверов у хостера: он шлёт их из окна и помнит, какие не ушли (<see cref="SendErrors"/>), через
    /// него же уходит проверочное. Нужен только вкладке хостера; null — тесты раздела без главной модели.
    /// </summary>
    internal HostedWatch? Watch
    {
        get => _watch;
        set
        {
            if (_watch is { } old)
            {
                old.SendFailed -= OnSendFailed;
                old.SendRecovered -= OnSendRecovered;
            }
            _watch = value;
            if (value is null) return;
            value.SendFailed += OnSendFailed;
            value.SendRecovered += OnSendRecovered;
        }
    }

    public ObservableCollection<NotifyEventRowViewModel> Rows { get; } = [];
    /// <summary>Шапка столбцов тумблеров: каналы по порядку — имя и логотип.</summary>
    public ObservableCollection<NotifyChannel> ChannelHeaders { get; } = [];
    public bool HasChannels => ChannelHeaders.Count > 0;

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
        _saveAgain = _saveLoud = false; // просили записать таблицу прежнего профиля — её больше нет
        Loaded = false;
        StatusText = "";
        ErrorText = SendErrors(); // у хостера — ошибки отправки этого профиля, если не ушло, пока смотрели другой
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
        if (_sendShown.Length > 0 && ErrorText == _sendShown) ErrorText = SendErrors();
    }

    /// <summary>Оповещение сервера у хостера не ушло (его шлёт окно) — сказать здесь, если открыт этот профиль: поверх прочего, как раньше.</summary>
    private void OnSendFailed(string profileId)
    {
        if (IsHosted && _profile?.Id == profileId) ErrorText = SendErrors();
    }

    /// <summary>В канал снова ушло: из строки ошибок отправки он пропадает. Другую ошибку вкладки (запись, проверочное) не трогаем.</summary>
    private void OnSendRecovered(string profileId)
    {
        if (IsHosted && _profile?.Id == profileId && ErrorText == _sendShown) ErrorText = SendErrors();
    }

    /// <summary>
    /// Ошибки отправки оповещений сервера у хостера, которые ещё в силе (их помнит сторож, <see cref="HostedWatch.SendFailures"/>):
    /// по каналу — последняя, пока в него не уйдёт удачное оповещение или проверочное. Вкладку открыли заново, сменили
    /// профиль и вернулись — они на месте. Канал поправили в «Настройках», убрали или выключили у этого сервера во всех
    /// событиях — ошибка уже не про него и забывается. Судим по настройкам, прочитанным вкладкой; пока их не прочитали —
    /// показываем все. У своего сервера — пусто: там шлёт агент, и его ошибки сюда не приходят.
    /// </summary>
    private string SendErrors()
    {
        if (!IsHosted || _profile is not { } profile || Watch is not { } watch) return _sendShown = "";
        if (Loaded) watch.ForgetSendFailures(profile.Id, f => !StillApplies(f));
        return _sendShown = string.Join(Environment.NewLine, watch.SendFailures(profile.Id).Select(f => f.Text));
    }

    /// <summary>Канал ошибки включён у сервера хоть для одного события и настроен так же, как когда отправка не удалась.</summary>
    private bool StillApplies(HostedSendFailure failure) =>
        _saved.Routes.Values.Any(ids => ids.Contains(failure.Channel.Id))
        && _saved.Channels.FirstOrDefault(c => c.Id == failure.Channel.Id) == failure.Channel;

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
            // в таблице щелчки, которые ждут записи: прочитанное старше их, а пересобранная по нему таблица ушла бы в эту запись
            if (_saveAgain) return;
            _saved = settings;
            Build(settings);
            Loaded = true;
            ErrorText = SendErrors(); // у своего сервера — пусто, как раньше
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
            ChannelHeaders.Clear();
            foreach (var c in _channels) ChannelHeaders.Add(c);
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

    /// <summary>
    /// Записать таблицу. Записи идут по одному: щелчок, пока прежняя запись не кончилась, только просит записать ещё раз
    /// (<see cref="_saveAgain"/>), и следующая запись снимает таблицу заново — с этим щелчком и всеми до него. Иначе
    /// два PUT агенту могли бы прийти в обратном порядке, а два потока — писать один файл .tmp своего сервера.
    /// </summary>
    private async Task SaveAsync(bool quiet)
    {
        if (_saving)
        {
            _saveAgain = true;
            _saveLoud |= !quiet;
            return;
        }
        _saving = true;
        try
        {
            await WriteAsync(quiet);
            // просьба всегда про профиль, открытый сейчас: просьбы прежнего снимает OnProfileSwitched, а пока таблица
            // нового не прочитана (Loaded), писать её нельзя
            while (_saveAgain && Loaded)
            {
                quiet = !_saveLoud;
                _saveAgain = _saveLoud = false;
                await WriteAsync(quiet);
            }
        }
        finally
        {
            _saving = false;
            _saveAgain = _saveLoud = false;
        }
    }

    /// <summary>
    /// Одна запись. Пока она шла, могли сменить профиль: тогда вкладка уже показывает другой сервер, и ни его
    /// настройки (по ним CanTest и пересборка при смене языка), ни строку ошибки чужой итог не трогает.
    /// </summary>
    private async Task WriteAsync(bool quiet)
    {
        if (_profile is not { } profile) return;
        var id = profile.Id;
        var settings = Collect();
        try
        {
            if (profile.IsRemote)
            {
                if (_server.Client is not { } client) throw new InvalidOperationException(Loc.T("server.stateOffline"));
                await client.SaveNotifyAsync(settings.ToUpload());
            }
            else await Task.Run(() => settings.Save(id));
            if (_profile?.Id == id)
            {
                if (!profile.IsRemote) _seenChange = ServerNotifySettings.ChangedAt(id);
                _saved = settings;
                TestCommand.NotifyCanExecuteChanged(); // включили первый маршрут или выключили последний — кнопка проверочного
                ErrorText = SendErrors(); // канал выключили у сервера или поправили — его ошибка отправки уходит
                if (!quiet) StatusText = "";
            }
            Saved?.Invoke(); // запись прошла — сторож хостера перечитывает с диска сам, какой профиль ни открыт
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException)
        {
            if (_profile?.Id == id) ErrorText = Loc.T("srvnotify.saveFailed", profile.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
    }

    /// <summary>
    /// «Открыть настройки» в подсказке «каналов ещё нет»: переход в общие «Настройки», где каналы добавляют (не в
    /// «Профили», куда ведут переходы раздела «Сервер» к самому профилю).
    /// </summary>
    [RelayCommand]
    private void OpenSettings() => _server.OpenAppSettings();

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
            // у хостера — через сторожа: так же, как уйдут настоящие, и дошедшее снимает прежнюю ошибку канала
            else if (IsHosted && Watch is { } watch && _profile is { } hosted)
                errors = await watch.TestAsync(hosted.Id, Loc.T("notify.testTitle"),
                    new NotifyLine("◇", Loc.T("notify.lbl.from"), Loc.T("notify.testFromWindow", Environment.MachineName)));
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

    /// <summary>Подсказки у хостера: где событие узнаётся иначе, чем у агента (по логу, раз в 6 часов, окном, копия на этот компьютер), — свои.</summary>
    private static string HostedHint(NotifyEvent e) => e switch
    {
        NotifyEvent.ServerCrashed => Loc.T("srvnotify.hostedCrashedHint"),
        NotifyEvent.ServerStopped => Loc.T("srvnotify.hostedStoppedHint"),
        NotifyEvent.ModUpdates => Loc.T("srvnotify.hostedModUpdatesHint"),
        NotifyEvent.ModsUpdated => Loc.T("srvnotify.hostedModsUpdatedHint"),
        NotifyEvent.BackupFailed => Loc.T("srvnotify.hostedBackupFailedHint"),
        _ => Hint(e),
    };
}
