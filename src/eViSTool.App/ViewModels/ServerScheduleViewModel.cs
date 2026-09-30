using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;

namespace eViSTool.App.ViewModels;

/// <summary>Строка списка резервных копий.</summary>
public sealed class BackupRowViewModel(BackupEntry entry)
{
    public BackupEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string TimeText { get; } = entry.Time.ToString("dd.MM.yyyy HH:mm");
    public string SizeText { get; } = Sizes.Format(entry.Size);

    /// <summary>Не «своя» копия (другой профиль, прежнее имя «default-…», положена руками): ротация её не трогает.</summary>
    public bool IsManual => !Entry.IsOwn;

    /// <summary>Файл на этой машине — можно показать в Проводнике.</summary>
    public bool IsLocal => Entry.LocalPath is not null;
}

/// <summary>
/// Вкладка «Расписание» раздела «Сервер»: резервные копии мира — по расписанию (их делает агент, окно можно закрыть)
/// и по кнопке, перезапуски. Работает одинаково для сервера на этой машине (файлы) и на другой (агент по сети) —
/// через <see cref="IServerData"/>. Настройки уходят агенту сразу при правке; он их подхватывает сам.
/// </summary>
public sealed partial class ServerScheduleViewModel : ObservableObject
{
    private readonly ServerViewModel _server;
    private IServerData? _data;
    private string? _profileKey;       // профиль и папка (или «удалённый»): сменились — всё читаем заново
    private string? _dataDir;          // только для сервера на этой машине: «Открыть папку»
    private ServerAutomation _saved = new();
    private bool _loaded;              // настройки прочитаны (у удалённого — когда появилась связь)
    private bool _loading;
    private bool _active;
    private int _generation;
    private DateTime? _lastSeenBackup;
    private DateTime? _seenAutomationChange;
    private bool _statusSeen; // первый статус после смены профиля — точка отсчёта для отметки изменения настроек
    private DateTime? _nextBackupAt;
    private bool _awaitingBackup; // копию запросили кнопкой, ждём сообщения агента о ней

    public ServerScheduleViewModel(ServerViewModel server) => _server = server;

    [ObservableProperty] private bool _backupEnabled;
    [ObservableProperty] private string _intervalText = "1";
    [ObservableProperty] private string _keepText = "7";
    [ObservableProperty] private bool _onlyWhenPlayed = true;
    [ObservableProperty] private bool _announce = true;

    // перезапуски по расписанию
    [ObservableProperty] private RestartMode _restartMode;
    [ObservableProperty] private string _restartIntervalText = "12";
    [ObservableProperty] private string _restartTimesText = "05:00";
    [ObservableProperty] private string _restartWarnText = "10, 5, 4, 3, 2, 1";
    [ObservableProperty] private bool _restartBackup = true;
    [ObservableProperty] private string _restartError = "";
    [ObservableProperty] private string _nextRestartText = "";
    private DateTime? _nextRestartAt;

    public bool IsRestartInterval => RestartMode == RestartMode.Interval;
    public bool IsRestartDaily => RestartMode == RestartMode.Daily;
    public bool IsRestartOn => RestartMode != RestartMode.Off;
    [ObservableProperty] private string _intervalError = "";
    [ObservableProperty] private string _keepError = "";

    [ObservableProperty] private string _lastBackupText = "";
    [ObservableProperty] private string _nextBackupText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Сервер на этой машине: есть папка копий, которую можно открыть.</summary>
    [ObservableProperty] private bool _isLocal;

    public ObservableCollection<BackupRowViewModel> Backups { get; } = [];
    public bool HasBackups => Backups.Count > 0;

    /// <summary>
    /// Сменился профиль (или его правят в настройках). Для сервера на этой машине — файлы его папки,
    /// для удалённого — агент по сети (клиент берётся у раздела «Сервер»: связь может появиться и пропасть).
    /// </summary>
    public void OnProfileSwitched(GameProfile? profile, Func<AgentClient?> remoteClient)
    {
        var key = profile is null ? null : profile.IsRemote ? "remote:" + profile.Id : profile.Id + "|" + profile.DataDir;
        if (key == _profileKey)
        {
            // только имя профиля: оно идёт в имена копий — источник данных пересоздаём, настройки не перечитываем
            if (profile is { IsRemote: false, DataDir: { } dir }) _data = Local(profile, dir);
            return;
        }
        _profileKey = key;
        _generation++;
        _dataDir = profile is { IsRemote: false } ? profile.DataDir : null;
        _data = profile is null ? null
            : profile.IsRemote ? new RemoteServerData(remoteClient)
            : profile.DataDir is { } data ? Local(profile, data) : null;
        IsLocal = _data is LocalServerData;
        _lastSeenBackup = null;
        _seenAutomationChange = null;
        _statusSeen = false;
        _nextBackupAt = null;
        _loaded = false;
        StatusText = "";
        Backups.Clear();
        OnPropertyChanged(nameof(HasBackups));
        TotalText = "";
        Apply(new ServerAutomation());
        // источник данных сменился — доступность кнопок считаем заново (до этого её посчитали без профиля)
        BackupNowCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        _ = LoadAsync(_generation);
    }

    private static LocalServerData Local(GameProfile profile, string dataDir) =>
        new(new ServerFiles(profile.Id, dataDir, BackupStore.Slug(profile.Name)));

    /// <summary>Прочитать настройки расписания и список копий (у удалённого сервера — когда есть связь).</summary>
    private async Task LoadAsync(int generation)
    {
        if (_data is not { } data) return;
        try
        {
            var settings = await data.LoadAutomationAsync();
            if (generation != _generation) return;
            _saved = settings;
            Apply(settings);
            _loaded = true;
            if (_active) await RefreshListAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            // удалённый сервер недоступен — попробуем, когда появится связь (ShowStatus); а вот о старом агенте — скажем
            if (generation == _generation && (!data.IsRemote || ex.Message.StartsWith("404")))
                StatusText = data.IsRemote ? RemoteSecret.Describe(ex) : Loc.T("sched.failed", ex.Message);
        }
        UpdateTexts();
    }

    /// <summary>Настройки — в поля вкладки (без записи обратно).</summary>
    private void Apply(ServerAutomation settings)
    {
        _loading = true;
        try
        {
            BackupEnabled = settings.BackupEnabled;
            IntervalText = settings.BackupIntervalHours.ToString("0.##", CultureInfo.CurrentCulture);
            KeepText = settings.BackupKeep.ToString();
            OnlyWhenPlayed = settings.BackupOnlyWhenPlayed;
            Announce = settings.BackupAnnounce;
            RestartMode = settings.RestartMode;
            RestartIntervalText = settings.RestartIntervalHours.ToString("0.##", CultureInfo.CurrentCulture);
            RestartTimesText = string.Join(", ", settings.RestartTimes);
            RestartWarnText = string.Join(", ", settings.RestartWarnMinutes);
            RestartBackup = settings.RestartBackup;
            RestartError = "";
            IntervalError = KeepError = "";
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Вкладку открыли — настройки и список копий читаются заново (их могли поменять в другом окне).</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (!active) return;
        _ = ReloadSettingsAsync();
        _ = RefreshListAsync();
    }

    /// <summary>
    /// Перечитать настройки расписания: их поменяли в другом окне (на той машине или на этой). Совпадают с тем,
    /// что мы сами только что сохранили, — поля не трогаем; в полях сейчас ошибка ввода — тоже (человек печатает).
    /// </summary>
    private async Task ReloadSettingsAsync()
    {
        if (!_loaded || _data is not { } data) return;
        var generation = _generation;
        try
        {
            var settings = await data.LoadAutomationAsync();
            if (generation != _generation || Same(settings, _saved)) return;
            if (IntervalError.Length > 0 || KeepError.Length > 0 || RestartError.Length > 0) return;
            _saved = settings;
            Apply(settings);
            UpdateTexts();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            // не прочитали — остаются прежние значения, попробуем при следующей отметке
        }
    }

    private static bool Same(ServerAutomation a, ServerAutomation b) =>
        Newtonsoft.Json.JsonConvert.SerializeObject(a) == Newtonsoft.Json.JsonConvert.SerializeObject(b);

    /// <summary>Свежий статус агента (null — агента нет или нет связи).</summary>
    public void ShowStatus(AgentStatus? status)
    {
        // удалённый сервер: связь появилась — настройки ещё не прочитаны, читаем
        if (status is not null && !_loaded && _data is { IsRemote: true }) _ = LoadAsync(_generation);

        // настройки расписания поменяли (в другом окне или здесь) — перечитываем; появление файла — тоже изменение
        if (status is not null)
        {
            if (_statusSeen && status.AutomationChangedAt != _seenAutomationChange) _ = ReloadSettingsAsync();
            _statusSeen = true;
            _seenAutomationChange = status.AutomationChangedAt;
        }

        _nextBackupAt = status?.NextBackupAt;
        _nextRestartAt = status?.NextRestartAt;
        // агент сообщил о новой копии — список устарел
        if (status?.LastBackupAt is { } last && last != _lastSeenBackup)
        {
            _lastSeenBackup = last;
            if (_active || _awaitingBackup) _ = RefreshListAsync(reportNewest: _awaitingBackup);
            _awaitingBackup = false;
        }
        UpdateTexts();
        BackupNowCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    public void OnLanguageChanged() => UpdateTexts();

    partial void OnBackupEnabledChanged(bool value) => SaveSettings();
    partial void OnIntervalTextChanged(string value) => SaveSettings();
    partial void OnKeepTextChanged(string value) => SaveSettings();
    partial void OnOnlyWhenPlayedChanged(bool value) => SaveSettings();
    partial void OnAnnounceChanged(bool value) => SaveSettings();
    partial void OnRestartIntervalTextChanged(string value) => SaveSettings();
    partial void OnRestartTimesTextChanged(string value) => SaveSettings();
    partial void OnRestartWarnTextChanged(string value) => SaveSettings();
    partial void OnRestartBackupChanged(bool value) => SaveSettings();

    partial void OnRestartModeChanged(RestartMode value)
    {
        OnPropertyChanged(nameof(IsRestartInterval));
        OnPropertyChanged(nameof(IsRestartDaily));
        OnPropertyChanged(nameof(IsRestartOn));
        SaveSettings();
    }

    /// <summary>Правка сразу уходит агенту (если числа разобрались) — он подхватит её в течение нескольких секунд.</summary>
    private void SaveSettings()
    {
        // пока настройки не прочитаны, не пишем: иначе значения по умолчанию затёрли бы настоящие
        if (_loading || !_loaded || _data is not { } data) return;

        var intervalOk = TryNumber(IntervalText, out var hours) && hours is >= 5.0 / 60 and <= 720;
        var keepOk = int.TryParse(KeepText.Trim(), out var keep) && keep is >= 0 and <= 1000;
        IntervalError = intervalOk ? "" : Loc.T("sched.intervalError");
        KeepError = keepOk ? "" : Loc.T("sched.keepError");

        // перезапуски: проверяем только поля выбранного режима; скрытые поля остаются как были
        var restartHours = _saved.RestartIntervalHours;
        var times = _saved.RestartTimes;
        var warns = _saved.RestartWarnMinutes;
        RestartError = "";
        if (RestartMode == RestartMode.Interval)
        {
            if (TryNumber(RestartIntervalText, out var h) && h is >= 5.0 / 60 and <= 720) restartHours = h;
            else RestartError = Loc.T("sched.intervalError");
        }
        else if (RestartMode == RestartMode.Daily)
        {
            var parts = Split(RestartTimesText);
            if (parts.Count > 0 && parts.All(t => ServerAutomation.TryTimeOfDay(t, out _))) times = parts;
            else RestartError = Loc.T("sched.timesError");
        }
        if (RestartMode != RestartMode.Off && RestartError.Length == 0)
        {
            var parts = Split(RestartWarnText);
            if (parts.All(w => int.TryParse(w, out var m) && m is >= 1 and <= 180)) warns = [.. parts.Select(w => int.Parse(w))];
            else RestartError = Loc.T("sched.warnError");
        }
        if (!intervalOk || !keepOk || RestartError.Length > 0) return;

        _saved = _saved with
        {
            BackupEnabled = BackupEnabled,
            BackupIntervalHours = hours,
            BackupKeep = keep,
            BackupOnlyWhenPlayed = OnlyWhenPlayed,
            BackupAnnounce = Announce,
            RestartMode = RestartMode,
            RestartIntervalHours = restartHours,
            RestartTimes = times,
            RestartWarnMinutes = warns,
            RestartBackup = RestartBackup,
        };
        _ = SaveAsync(data, _saved);
        UpdateTexts();
    }

    private async Task SaveAsync(IServerData data, ServerAutomation settings)
    {
        try
        {
            await data.SaveAutomationAsync(settings);
            if (StatusText.StartsWith(Loc.T("sched.saveFailed", ""), StringComparison.Ordinal)) StatusText = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException
                                       or TaskCanceledException)
        {
            StatusText = Loc.T("sched.saveFailed", data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
    }

    // «05:00, 17:30» или «10 5 1» → части
    private static List<string> Split(string text) =>
        [.. text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // «1,5» и «1.5» — оба годятся
    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void UpdateTexts()
    {
        var newest = Backups.FirstOrDefault()?.Entry.Time;
        LastBackupText = newest is { } t ? Loc.T("sched.last", When(t)) : Loc.T("sched.lastNone");
        NextBackupText = !BackupEnabled ? Loc.T("sched.nextOff")
            : _nextBackupAt is { } next ? Loc.T("sched.next", When(next))
            : Loc.T("sched.nextStopped");
        NextRestartText = RestartMode == RestartMode.Off ? ""
            : _nextRestartAt is { } restart ? Loc.T("sched.restartNext", When(restart))
            : Loc.T("sched.restartNextStopped");
    }

    private static string When(DateTime time) =>
        time.Date == DateTime.Today ? Loc.T("sched.today", time.ToString("HH:mm")) : time.ToString("dd.MM.yyyy HH:mm");

    [RelayCommand]
    private Task RefreshList() => RefreshListAsync();

    /// <param name="reportNewest">Копию просили кнопкой — вместо «сервер делает копию…» показать итог.</param>
    private async Task RefreshListAsync(bool reportNewest = false)
    {
        if (_data is not { } data) return;
        var generation = _generation;
        IReadOnlyList<BackupEntry> list;
        try
        {
            list = await data.ListBackupsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException
                                       or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            if (generation == _generation && (!data.IsRemote || ex.Message.StartsWith("404")))
                StatusText = data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message;
            return;
        }
        if (generation != _generation) return;

        Backups.Clear();
        foreach (var entry in list) Backups.Add(new BackupRowViewModel(entry));
        TotalText = Backups.Count == 0 ? "" : Loc.T("sched.total", Backups.Count, Sizes.Format(list.Sum(b => b.Size)));
        OnPropertyChanged(nameof(HasBackups));
        if (reportNewest && list.FirstOrDefault(b => b.IsOwn) is { } made) StatusText = Loc.T("sched.done", made.Name, Sizes.Format(made.Size));
        UpdateTexts();
    }

    // На работающем сервере копию делает сам сервер (/genbackup), на полностью остановленном — копируется файл мира
    // (у удалённого сервера — его агентом). Чужой сервер из той же папки — только своими средствами.
    private bool CanBackupNow => !IsBusy && _data is not null
                                 && (!_server.IsRemoteProfile || _server.AgentRunning)
                                 && (_server.State == ServerState.Running && _server.AgentRunning
                                     || _server.State == ServerState.Stopped && !_server.HasForeign);

    partial void OnIsBusyChanged(bool value)
    {
        BackupNowCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanBackupNow))]
    private async Task BackupNow()
    {
        if (_data is not { } data) return;
        IsBusy = true;
        try
        {
            if (_server.State == ServerState.Running)
            {
                // сервер сам сохранит мир и положит копию в Backups; список обновится, когда агент сообщит о ней
                _awaitingBackup = true;
                await _server.BackupAsync();
                StatusText = Loc.T("sched.requested");
            }
            else
            {
                var copy = await data.CopyWorldAsync();
                StatusText = Loc.T("sched.done", copy.Name, Sizes.Format(copy.Size));
                await RefreshListAsync();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            StatusText = Loc.T("sched.failed", data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // восстановление подменяет файл мира — только при полностью остановленном сервере
    private bool CanRestore(BackupRowViewModel? row) =>
        row is not null && !IsBusy && _data is not null && _server.State == ServerState.Stopped && !_server.HasForeign
        && (!_server.IsRemoteProfile || _server.AgentRunning);

    /// <summary>Вернуть мир из копии. Текущий мир перед этим сохраняется рядом с копиями — восстановление можно откатить.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore(BackupRowViewModel? row)
    {
        if (row is null || _data is not { } data) return;
        var text = Loc.T("sched.restoreAsk", row.Name, row.TimeText, row.SizeText)
                   + (row.Entry.IsOwn ? "" : "\n\n" + Loc.T("sched.restoreForeign"));
        if (MessageBox.Show(Application.Current.MainWindow!, text, "eViSTool", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        try
        {
            var result = await data.RestoreAsync(row.Name);
            StatusText = result.SafetyName is null ? Loc.T("sched.restored", row.Name) : Loc.T("sched.restoredKept", row.Name, result.SafetyName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException
                                       or TaskCanceledException or Newtonsoft.Json.JsonException)
        {
            StatusText = Loc.T("sched.failed", data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshListAsync();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_data is not LocalServerData local) return;
        var dir = local.Files.BackupsDir;
        Directory.CreateDirectory(dir);
        Shell.OpenFolder(dir);
    }

    [RelayCommand]
    private static void ShowInFolder(BackupRowViewModel? row)
    {
        if (row?.Entry.LocalPath is { } path && File.Exists(path)) Shell.ShowInFolder(path);
    }

    [RelayCommand]
    private async Task Delete(BackupRowViewModel? row)
    {
        if (row is null || _data is not { } data) return;
        var ask = Loc.T(data.IsRemote ? "sched.deleteAskRemote" : "sched.deleteAsk", row.Name);
        if (MessageBox.Show(Application.Current.MainWindow!, ask, "eViSTool",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            await data.DeleteBackupAsync(row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException
                                       or TaskCanceledException)
        {
            StatusText = Loc.T("sched.failed", data.IsRemote ? RemoteSecret.Describe(ex) : ex.Message);
        }
        await RefreshListAsync();
    }
}
