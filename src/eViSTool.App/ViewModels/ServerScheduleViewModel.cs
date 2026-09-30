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
using eViSTool.Core.Server.Config;
using Newtonsoft.Json.Linq;

namespace eViSTool.App.ViewModels;

/// <summary>Строка списка резервных копий.</summary>
public sealed class BackupRowViewModel(BackupFile file)
{
    public BackupFile File { get; } = file;
    public string Name => File.Name;
    public string TimeText { get; } = file.Time.ToString("dd.MM.yyyy HH:mm");
    public string SizeText { get; } = Sizes.Format(file.Size);

    /// <summary>Не «своя» копия (другой профиль, прежнее имя «default-…», положена руками): ротация её не трогает.</summary>
    public bool IsManual => !File.IsOwn;
}

/// <summary>
/// Вкладка «Расписание» раздела «Сервер»: резервные копии мира — по расписанию (их делает агент, окно можно закрыть)
/// и по кнопке. Настройки пишутся в файл рядом с ключом агента сразу при правке; агент сам их перечитывает.
/// </summary>
public sealed partial class ServerScheduleViewModel : ObservableObject
{
    private readonly ServerViewModel _server;
    private string? _profileId;
    private string? _dataDir;
    private string _profileName = "";
    private bool _loading;
    private bool _active;
    private DateTime? _lastSeenBackup;
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

    public ObservableCollection<BackupRowViewModel> Backups { get; } = [];
    public bool HasBackups => Backups.Count > 0;

    /// <summary>Сменился профиль (или его правят в настройках): другой профиль — другие настройки и другая папка копий.</summary>
    public void OnProfileSwitched(string? profileId, string? dataDir, string profileName)
    {
        _profileName = profileName; // имя идёт в имена копий; его правка настройки и список не перечитывает
        if (profileId == _profileId && string.Equals(dataDir, _dataDir, StringComparison.OrdinalIgnoreCase)) return;
        _profileId = profileId;
        _dataDir = dataDir;
        _lastSeenBackup = null;
        _nextBackupAt = null;
        StatusText = "";

        _loading = true;
        try
        {
            var settings = profileId is null ? new ServerAutomation() : ServerAutomation.Load(profileId);
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
        if (_active) RefreshList();
        UpdateTexts();
    }

    /// <summary>Вкладку открыли — список копий читается с диска.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active) RefreshList();
    }

    /// <summary>Свежий статус агента (null — агента нет, сервер остановлен).</summary>
    public void ShowStatus(AgentStatus? status)
    {
        _nextBackupAt = status?.NextBackupAt;
        _nextRestartAt = status?.NextRestartAt;
        // агент сообщил о новой копии — список устарел
        if (status?.LastBackupAt is { } last && last != _lastSeenBackup)
        {
            _lastSeenBackup = last;
            if (_active) RefreshList();
            // копию просили кнопкой — вместо «сервер делает копию…» показываем итог
            if (_awaitingBackup && _dataDir is not null)
            {
                _awaitingBackup = false;
                var made = Store(_dataDir).List().FirstOrDefault(b => b.IsOwn);
                StatusText = made is null ? "" : Loc.T("sched.done", made.Name, Sizes.Format(made.Size));
            }
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

    /// <summary>Папка копий профиля; «свои» копии — с именем профиля в названии файла.</summary>
    private BackupStore Store(string dataDir) => new(dataDir, BackupStore.Slug(_profileName));

    /// <summary>Правка сразу уходит в файл настроек (если числа разобрались) — агент подхватит её в течение нескольких секунд.</summary>
    private void SaveSettings()
    {
        if (_loading || _profileId is null) return;

        var intervalOk = TryNumber(IntervalText, out var hours) && hours is >= 5.0 / 60 and <= 720;
        var keepOk = int.TryParse(KeepText.Trim(), out var keep) && keep is >= 0 and <= 1000;
        IntervalError = intervalOk ? "" : Loc.T("sched.intervalError");
        KeepError = keepOk ? "" : Loc.T("sched.keepError");

        // перезапуски: проверяем только поля выбранного режима; скрытые поля остаются в файле как были
        var saved = ServerAutomation.Load(_profileId);
        var restartHours = saved.RestartIntervalHours;
        var times = saved.RestartTimes;
        var warns = saved.RestartWarnMinutes;
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

        try
        {
            new ServerAutomation
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
            }.Save(_profileId);
            StatusText = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = Loc.T("sched.saveFailed", ex.Message);
        }
        UpdateTexts();
    }

    // «05:00, 17:30» или «10 5 1» → части
    private static List<string> Split(string text) =>
        [.. text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // «1,5» и «1.5» — оба годятся
    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void UpdateTexts()
    {
        var newest = Backups.FirstOrDefault()?.File.Time;
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
    private void RefreshList()
    {
        Backups.Clear();
        long total = 0;
        if (_dataDir is not null)
        {
            try
            {
                foreach (var file in Store(_dataDir).List())
                {
                    Backups.Add(new BackupRowViewModel(file));
                    total += file.Size;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText = ex.Message;
            }
        }
        TotalText = Backups.Count == 0 ? "" : Loc.T("sched.total", Backups.Count, Sizes.Format(total));
        OnPropertyChanged(nameof(HasBackups));
        UpdateTexts();
    }

    // копию можно сделать на работающем сервере (её делает сам сервер) или на полностью остановленном (копируем файл)
    private bool CanBackupNow => !IsBusy && _dataDir is not null
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
        if (_dataDir is not { } dataDir) return;
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
                var save = SaveFile(dataDir) ?? throw new InvalidOperationException(Loc.T("sched.noConfig"));
                var settings = _profileId is null ? new ServerAutomation() : ServerAutomation.Load(_profileId);
                var copy = await Task.Run(() =>
                {
                    var store = Store(dataDir);
                    var made = store.CopySave(save, DateTime.Now);
                    if (settings.BackupEnabled) store.Prune(settings.BackupKeep);
                    return made;
                });
                StatusText = Loc.T("sched.done", copy.Name, Sizes.Format(copy.Size));
                RefreshList();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or HttpRequestException or Newtonsoft.Json.JsonException)
        {
            StatusText = Loc.T("sched.failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Файл мира профиля — из его serverconfig.json (null — конфига или пути в нём нет).</summary>
    private static string? SaveFile(string dataDir)
    {
        var config = Path.Combine(dataDir, ProfileResolver.ServerConfigName);
        if (!File.Exists(config)) return null;
        if (ServerConfigDocument.Load(config).Get("WorldConfig.SaveFileLocation") is not JValue { Value: string path } || path.Trim().Length == 0)
            return null;
        return Path.IsPathRooted(path) ? path : Path.Combine(dataDir, path);
    }

    // восстановление подменяет файл мира — только при полностью остановленном сервере
    private bool CanRestore(BackupRowViewModel? row) =>
        row is not null && !IsBusy && _dataDir is not null && _server.State == ServerState.Stopped && !_server.HasForeign;

    /// <summary>Вернуть мир из копии. Текущий мир перед этим сохраняется рядом с копиями — восстановление можно откатить.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore(BackupRowViewModel? row)
    {
        if (row is null || _dataDir is not { } dataDir) return;
        var text = Loc.T("sched.restoreAsk", row.Name, row.TimeText, row.SizeText)
                   + (row.File.IsOwn ? "" : "\n\n" + Loc.T("sched.restoreForeign"));
        if (MessageBox.Show(Application.Current.MainWindow!, text, "eViSTool", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        try
        {
            var save = SaveFile(dataDir) ?? throw new InvalidOperationException(Loc.T("sched.noConfig"));
            var safety = await Task.Run(() => Store(dataDir).Restore(row.File, save, DateTime.Now));
            StatusText = safety is null ? Loc.T("sched.restored", row.Name) : Loc.T("sched.restoredKept", row.Name, safety.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            StatusText = Loc.T("sched.failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
        RefreshList();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_dataDir is null) return;
        var dir = new BackupStore(_dataDir).Dir;
        Directory.CreateDirectory(dir);
        Shell.OpenFolder(dir);
    }

    [RelayCommand]
    private static void ShowInFolder(BackupRowViewModel? row)
    {
        if (row is not null && File.Exists(row.File.Path)) Shell.ShowInFolder(row.File.Path);
    }

    [RelayCommand]
    private void Delete(BackupRowViewModel? row)
    {
        if (row is null) return;
        if (MessageBox.Show(Application.Current.MainWindow!, Loc.T("sched.deleteAsk", row.Name), "eViSTool",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            Shell.MoveToRecycleBin(row.File.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            StatusText = Loc.T("sched.failed", ex.Message);
        }
        RefreshList();
    }
}
