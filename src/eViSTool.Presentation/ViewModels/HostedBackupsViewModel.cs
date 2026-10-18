using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Presentation.ViewModels.Mods;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Строка списка копий мира сервера у хостера на этом компьютере.</summary>
public sealed class HostedBackupRowViewModel(HostedWorldBackup backup)
{
    public HostedWorldBackup Backup { get; } = backup;
    public string Name => Backup.Name;
    public string TimeText { get; } = Dates.Stamp(backup.Time);
    public string SizeText { get; } = Sizes.Format(backup.Size + backup.ExtraSize);

    /// <summary>Откуда скачана: «мир» (сервер был остановлен) или «копия игры» (из Backups, BackupSaves).</summary>
    public string OriginText { get; } = OriginOf(backup);

    /// <summary>Путь на сервере, откуда скачана; сведений нет — null (подсказки нет).</summary>
    public string? OriginTip { get; } = backup.SourcePath is { } path ? Loc.T("hostedbk.sourceTip", path) : null;

    /// <summary>Рядом лежит архив с настройками и данными модов.</summary>
    public bool HasExtra => Backup.HasExtra;
    public string ExtraTip { get; } = Loc.T("hostedbk.extraTip", Sizes.Format(backup.ExtraSize));

    /// <summary>Не своя копия (другой профиль, положена руками): ротация её не трогает.</summary>
    public bool IsManual => !Backup.IsOwn;
    public string ManualText { get; } = backup.FromOtherProfile ? Loc.T("hostedbk.otherProfile") : Loc.T("sched.manual");
    public string ManualTip { get; } = backup.FromOtherProfile ? Loc.T("hostedbk.otherProfileTip") : Loc.T("hostedbk.manualTip");

    public static string OriginOf(HostedWorldBackup backup) => backup.Origin switch
    {
        HostedWorldOrigin.World => Loc.T("hostedbk.originWorld"),
        HostedWorldOrigin.GameBackup => Loc.T("hostedbk.originGame"),
        _ => Loc.T("hostedbk.originUnknown"),
    };
}

/// <summary>
/// Вкладка «Копии» раздела «Сервер» у сервера у хостера: копии мира на этом компьютере — на случай, если доступ к хостеру
/// пропадёт. «Скачать копию сейчас» — откуда, решает лог: сервер остановлен или вылетел — сам мир, иначе — свежая копия
/// игры из Backups и BackupSaves; её нет — понятный отказ. Список копий: показать в папке, удалить (в Корзину),
/// восстановить на сервер — после вопроса с датой копии, вопроса «останови сервер» и только если по логу сервер
/// остановлен. Настройки (расписание, сколько хранить, папка) — в профиле, сохраняются сразу и моды не перечитывают.
/// Сами операции — общие на окно (<see cref="HostedWorldJobs"/>): ушли с вкладки или сменили профиль — операция идёт,
/// вернулись — виден её ход или итог.
/// </summary>
public sealed partial class HostedBackupsViewModel : ObservableObject
{
    private readonly MainViewModel? _main;
    private readonly HostedWorldJobs _jobs;
    private readonly HostedStopReminder _reminder = new(); // без главного окна (тесты вкладки) — свой вопрос «останови сервер»
    private ProfileViewModel? _profile;
    private HostedWorldJob? _job;               // идущая операция открытого профиля
    private HostedWorldOutcome? _shown;         // итог, показанный в строке (пересобирается при смене языка)
    private IReadOnlyList<HostedWorldBackup> _list = [];
    private bool _active;
    private bool _loading;                      // поля настроек заполняются из профиля — не сохранять
    private int _generation;                    // сменили профиль — ответы для прежнего не показывать
    private int _dirCheck;                      // какая проверка папки последняя

    /// <param name="main">Главное окно: вопрос «останови сервер» — тот же, что у модов; без него (тесты) — свой.</param>
    /// <param name="jobs">Операции с копиями мира — общие с расписанием.</param>
    public HostedBackupsViewModel(MainViewModel? main, HostedWorldJobs jobs)
    {
        _main = main;
        _jobs = jobs;
        jobs.Started += OnJobStarted;
        jobs.Finished += OnJobFinished;
    }

    /// <summary>Вопрос «останови сервер» перед восстановлением (true — можно дальше); тесты подменяют.</summary>
    internal Func<ModTarget, string, Task<bool>>? ConfirmStopped { get; set; }

    public ObservableCollection<HostedBackupRowViewModel> Backups { get; } = [];
    public bool HasBackups => Backups.Count > 0;

    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private string _lastText = "";
    [ObservableProperty] private string _nextText = "";

    /// <summary>Итог последней операции: удалась или отменена.</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Итог последней операции: не вышло (и почему, и что делать).</summary>
    [ObservableProperty] private string _errorText = "";

    /// <summary>Предупреждения ядра к итогу — как есть, по строке.</summary>
    [ObservableProperty] private string _warningsText = "";

    /// <summary>Пароль подключения не прочитать на этой машине: ни скачать, ни восстановить. Пусто — всё в порядке.</summary>
    [ObservableProperty] private string _problemText = "";

    // ---------- ход

    /// <summary>Идёт операция открытого профиля (своя кнопкой или по расписанию).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWork), nameof(IsBusy))]
    private bool _isWorking;

    /// <summary>Перед восстановлением проверяем по логу, остановлен ли сервер.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWork), nameof(IsBusy))]
    private bool _isChecking;

    public bool ShowWork => IsWorking || IsChecking;
    public bool IsBusy => IsWorking || IsChecking;

    /// <summary>«Скачиваю мир: 12,3 МБ из 90,1 МБ».</summary>
    [ObservableProperty] private string _workText = "";

    /// <summary>Доля шага, 0…1 — для полосы, как у «Обновить всё».</summary>
    [ObservableProperty] private double _workProgress;

    // ---------- настройки

    [ObservableProperty] private HostedBackupSchedule _schedule;
    [ObservableProperty] private string _keepText = "7";
    [ObservableProperty] private string _keepError = "";
    [ObservableProperty] private string _backupDirText = "";
    [ObservableProperty] private string _backupDirError = "";
    [ObservableProperty] private string _backupDirOk = "";
    [ObservableProperty] private bool _backupDirChecking;

    /// <summary>Где копии лежат, если своя папка не выбрана: папка программы, HostedBackups/&lt;id профиля&gt;.</summary>
    public string BackupDirDefault => _profile is { } p ? HostedWorldBackups.DefaultDir(p.Model.Id) : "";

    /// <summary>Открыт профиль у хостера.</summary>
    public bool HasProfile => _profile is not null;

    // ---------- профиль и вкладка

    /// <summary>
    /// Сменился активный профиль или его настройки. Тот же профиль (поменяли имя, оно в именах копий) — список заново,
    /// поля как есть; другой — поля из его настроек, ход его операции, если идёт, и итог последней.
    /// </summary>
    public void OnProfileSwitched(ProfileViewModel? profile)
    {
        var next = profile is { IsHosted: true } ? profile : null;
        var same = next is not null && _profile?.Model.Id == next.Model.Id;
        _profile = next;
        if (!same)
        {
            _generation++;
            _dirCheck++;
            _list = [];
            Backups.Clear();
            OnPropertyChanged(nameof(HasBackups));
            BackupDirChecking = false;
            IsChecking = false;
            Watch(next is null ? null : _jobs.Running(next.Model.Id));
            ShowOutcome(next is null ? null : _jobs.Last(next.Model.Id));
            LoadSettings();
        }
        ProblemText = next is not null && ModTarget.For(next.Model) is null ? ModTarget.UnreadableText(next.Model) : "";
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(BackupDirDefault));
        UpdateTexts();
        NotifyCommands();
        if (_active) _ = RefreshAsync();
    }

    /// <summary>Вкладку открыли (или закрыли): открыли — список копий заново, его могли поменять руками.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active) _ = RefreshAsync();
    }

    public void OnLanguageChanged()
    {
        FillRows();
        UpdateTexts();
        ShowOutcome(_shown);
        ShowProgress();
        if (KeepError.Length > 0) KeepError = Loc.T("sched.keepError");
    }

    /// <summary>Цель операций — снимок профиля сейчас; пароль не прочитать — null.</summary>
    private ModTarget? Target() => _profile is { } p && ModTarget.For(p.Model) is { IsHosted: true } target ? target : null;

    /// <summary>Перечитать список копий в папке (в пуле потоков: папка бывает сетевой).</summary>
    public async Task RefreshAsync()
    {
        var generation = _generation;
        // shortcut: пароль подключения не прочитать (настройки перенесли на другую машину) — список пуст: копиям нужна цель
        // с подключением. Сами файлы видно через «Открыть папку с копиями». Переделать, если копии понадобятся без пароля
        var list = Target() is { } target ? await Task.Run(() => new HostedWorldBackups(target).List()) : [];
        if (generation != _generation) return;
        _list = list;
        FillRows();
        UpdateTexts();
    }

    private void FillRows()
    {
        Backups.Clear();
        foreach (var backup in _list) Backups.Add(new HostedBackupRowViewModel(backup));
        OnPropertyChanged(nameof(HasBackups));
    }

    private void UpdateTexts()
    {
        var own = _list.Where(b => b.IsOwn).ToList();
        LastText = own.Count > 0 ? Loc.T("sched.last", When(own.Max(b => b.Time))) : Loc.T("sched.lastNone");
        TotalText = _list.Count == 0 ? "" : Loc.T("sched.total", _list.Count, Sizes.Format(_list.Sum(b => b.Size + b.ExtraSize)));
        NextText = Next(own);
    }

    /// <summary>Когда следующая копия по расписанию: выключено, повтор после неудачи, через срок после последней или скоро.</summary>
    private string Next(List<HostedWorldBackup> own)
    {
        var schedule = _profile?.BackupSchedule ?? HostedBackupSchedule.Off;
        if (schedule == HostedBackupSchedule.Off || _profile is not { } profile) return Loc.T("hostedbk.nextOff");
        var now = _jobs.Time.GetUtcNow().UtcDateTime;
        if (_jobs.Last(profile.Model.Id) is { Job.Scheduled: true, Ok: false } failed && failed.At + HostedWatch.BackupRetry > now)
            return Loc.T("hostedbk.nextRetry", When((failed.At + HostedWatch.BackupRetry).ToLocalTime()));
        if (own.Count > 0 && own.Max(b => b.Time) + HostedWatch.PeriodOf(schedule) is var next && next > _jobs.Time.GetLocalNow().DateTime)
            return Loc.T("hostedbk.next", When(next));
        return Loc.T("hostedbk.nextSoon");
    }

    private static string When(DateTime time) =>
        time.Date == DateTime.Today ? Loc.T("sched.today", Dates.Clock(time)) : Dates.Stamp(time);

    // ---------- операции

    private void OnJobStarted(HostedWorldJob job)
    {
        if (_profile?.Model.Id != job.ProfileId) return;
        ShowOutcome(null);
        Watch(job);
    }

    /// <summary>Перечитывание списка после конца операции (тесты ждут его).</summary>
    internal Task Refreshed { get; private set; } = Task.CompletedTask;

    private void OnJobFinished(HostedWorldOutcome outcome)
    {
        if (_profile?.Model.Id != outcome.Job.ProfileId) return;
        Watch(null);
        ShowOutcome(outcome);
        Refreshed = RefreshAsync(); // новая копия, ротация — список заново; вкладку могли и не открывать
    }

    /// <summary>Следить за ходом этой операции (прежнюю — отпустить).</summary>
    private void Watch(HostedWorldJob? job)
    {
        if (_job is { } old) old.PropertyChanged -= OnJobChanged;
        _job = job;
        if (job is not null) job.PropertyChanged += OnJobChanged;
        IsWorking = job is not null;
        ShowProgress();
        NotifyCommands();
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowProgress();
        if (e.PropertyName == nameof(HostedWorldJob.IsCancelling)) CancelCommand.NotifyCanExecuteChanged();
    }

    private void ShowProgress()
    {
        WorkText = _job is { } job ? HostedWorldText.Progress(job) : IsChecking ? Loc.T("hostedbk.checking") : "";
        WorkProgress = _job is { } running ? HostedWorldText.Fraction(running) : 0;
    }

    private void ShowOutcome(HostedWorldOutcome? outcome)
    {
        _shown = outcome;
        if (outcome is null)
        {
            StatusText = ErrorText = WarningsText = "";
            return;
        }
        // что случилось без человека — с пометкой «по расписанию»: иначе непонятно, откуда строка
        var text = outcome.Job.Scheduled ? Loc.T("hostedbk.scheduled", outcome.Text) : outcome.Text;
        (StatusText, ErrorText) = outcome.Ok || outcome.Cancelled ? (text, "") : ("", text);
        WarningsText = string.Join(Environment.NewLine, outcome.Warnings);
    }

    private void Say(string error)
    {
        _shown = null;
        StatusText = WarningsText = "";
        ErrorText = error;
    }

    private void NotifyCommands()
    {
        BackupNowCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCheckingChanged(bool value)
    {
        ShowProgress();
        NotifyCommands();
    }

    private bool CanBackupNow() => _profile is not null && ProblemText.Length == 0 && !IsBusy;

    /// <summary>
    /// «Скачать копию сейчас»: та же операция, что по расписанию. Откуда — по логу; ход — в строке и полосе, отмена —
    /// кнопкой рядом. У профиля уже идёт операция (расписание успело раньше) — кнопка недоступна.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBackupNow))]
    private void BackupNow()
    {
        if (Target() is not { } target) return;
        if (_jobs.StartBackup(target, scheduled: false) is null) Say(Loc.T("hostedbk.busy"));
    }

    private bool CanCancel() => _job is { IsCancelling: false };

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _job?.Cancel();

    private bool CanTouch(HostedBackupRowViewModel? row) => row is not null && _profile is not null && !IsBusy;

    /// <summary>
    /// «Восстановить на сервер…»: вопрос с датой копии и галочкой «и настройки модов, игроков и данные модов»; затем
    /// «останови сервер» — тот же вопрос, что перед изменением модов (по логу остановлен — без вопроса); затем ещё раз
    /// лог — и только если по нему сервер точно не работает (остановлен или вылетел), заливка. Работает — отказ с
    /// объяснением: при остановке он записал бы свой мир поверх восстановленного. Нынешний мир остаётся на сервере как bak.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTouch))]
    private async Task Restore(HostedBackupRowViewModel? row)
    {
        if (row is null || Target() is not { } target) return;
        if (AppWindows.Current.ConfirmHostedRestore(row.Backup, target.Name) is not { } choice) return;
        var generation = _generation;
        ShowOutcome(null);
        IsChecking = true;
        HostedServerState state;
        try
        {
            if (!await AskStoppedAsync(target, Loc.T("hostedbk.stopAsk", target.Name)) || generation != _generation) return;
            state = await _jobs.StateAsync(target, CancellationToken.None);
        }
        finally
        {
            if (generation == _generation) IsChecking = false;
        }
        if (generation != _generation) return;
        if (!HostedLogs.IsSurelyDown(state))
        {
            Say(state == HostedServerState.Unknown ? Loc.T("hostedbk.restoreUnknown") : Loc.T("hostedbk.restoreRunning", StateWord(state)));
            return;
        }
        var options = new HostedRestoreOptions
        {
            ServerStopped = true,
            KeepOldWorld = true, // нынешний мир — рядом с ним как bak: об этом и спрашивали
            Extras = choice.Extras ? HostedWorldExtras.ModConfig | HostedWorldExtras.Playerdata | HostedWorldExtras.ModData : HostedWorldExtras.None,
        };
        if (_jobs.StartRestore(target, row.Backup, options) is null) Say(Loc.T("hostedbk.busy"));
    }

    private Task<bool> AskStoppedAsync(ModTarget target, string question) =>
        ConfirmStopped?.Invoke(target, question)
        ?? (_main is { } main ? main.Mods.ConfirmIfRunningAsync(target, question) : AskOwnAsync(target, question));

    private async Task<bool> AskOwnAsync(ModTarget target, string question)
    {
        await _reminder.CheckAsync(target);
        return _reminder.IsKnownStopped(target) || _reminder.Confirm(target, question);
    }

    /// <summary>«работает», «запускается», «останавливается» — внутри фразы «По логу сервер …».</summary>
    private static string StateWord(HostedServerState state) => state switch
    {
        HostedServerState.Starting => Loc.T("hostedbk.stateStarting"),
        HostedServerState.Stopping => Loc.T("hostedbk.stateStopping"),
        _ => Loc.T("hostedbk.stateRunning"),
    };

    /// <summary>«Удалить»: копия, её архив и сведения — в Корзину, после вопроса.</summary>
    [RelayCommand(CanExecute = nameof(CanTouch))]
    private async Task Delete(HostedBackupRowViewModel? row)
    {
        if (row is null || Target() is not { } target) return;
        if (!Ui.Confirm(Loc.T("sched.deleteAsk", row.Name), byDefault: Answer.No)) return;
        try
        {
            var backups = new HostedWorldBackups(target);
            await Task.Run(() => backups.Delete(row.Backup, Ui.Platform.MoveToTrash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Say(Loc.T("sched.failed", ex.Message));
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private static void ShowInFolder(HostedBackupRowViewModel? row)
    {
        if (row is not null && File.Exists(row.Backup.Path)) Ui.ShowInFolder(row.Backup.Path);
    }

    /// <summary>Открыть папку копий (нет — создать).</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        if (_profile is not { } p) return;
        var dir = p.BackupDir is { Length: > 0 } own && Path.IsPathFullyQualified(own) ? own : HostedWorldBackups.DefaultDir(p.Model.Id);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say(Loc.T("backupdir.unreachable", ex.Message));
            return;
        }
        Ui.OpenFolder(dir);
    }

    // ---------- настройки: в профиль сразу, без перечитывания модов

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            Schedule = _profile?.BackupSchedule ?? HostedBackupSchedule.Off;
            KeepText = (_profile?.BackupKeep ?? 7).ToString(CultureInfo.InvariantCulture);
            BackupDirText = _profile?.BackupDir ?? "";
            KeepError = BackupDirError = BackupDirOk = "";
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnScheduleChanged(HostedBackupSchedule value)
    {
        if (_loading || _profile is not { } profile) return;
        profile.BackupSchedule = value;
        UpdateTexts();
        _main?.HostedWatch?.Refresh(); // сторож берёт расписание сразу, а не через минуту
    }

    partial void OnKeepTextChanged(string value)
    {
        if (_loading || _profile is not { } profile) return;
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var keep) && keep is >= 0 and <= 1000)
        {
            KeepError = "";
            profile.BackupKeep = keep;
        }
        else KeepError = Loc.T("sched.keepError");
    }

    partial void OnBackupDirTextChanged(string value)
    {
        if (_loading || _profile is null) return;
        _ = ApplyBackupDirAsync(value.Trim());
    }

    /// <summary>
    /// Новую папку сначала проверяем (создаётся ли, пишется ли — в пуле потоков: папка бывает сетевой), годится —
    /// в профиль, нет — остаётся прежняя, а под полем сказано, что не так. Пусто — папка программы.
    /// </summary>
    private async Task ApplyBackupDirAsync(string dir)
    {
        if (_profile is not { } profile) return;
        var check = ++_dirCheck;
        BackupDirError = BackupDirOk = "";
        if (string.Equals(dir, profile.BackupDir ?? "", StringComparison.Ordinal)) return;
        BackupDirChecking = true;
        var error = dir.Length == 0 ? null : await Task.Run(() => HostedWorldBackups.CheckDir(dir));
        if (check != _dirCheck) return;
        BackupDirChecking = false;
        if (error is not null)
        {
            BackupDirError = error;
            return;
        }
        profile.BackupDir = dir.Length > 0 ? dir : null;
        BackupDirOk = Loc.T(dir.Length > 0 ? "hostedbk.dirSaved" : "hostedbk.dirSavedDefault");
        await RefreshAsync();
    }

    [RelayCommand]
    private void BrowseBackupDir()
    {
        var start = BackupDirText.Length > 0 && Directory.Exists(BackupDirText) ? BackupDirText : null;
        if (Ui.Platform.PickFolder(Loc.T("backupdir.pick"), start) is { } picked) BackupDirText = picked;
    }

    [RelayCommand]
    private void DefaultBackupDir() => BackupDirText = "";

    /// <summary>Enter в поле папки: применить набранное сразу (привязка поля — по уходу из него).</summary>
    public void CommitBackupDir(string? text) => BackupDirText = text ?? "";
}
