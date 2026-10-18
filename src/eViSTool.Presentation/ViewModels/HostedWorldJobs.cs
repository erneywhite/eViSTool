using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;

namespace eViSTool.Presentation.ViewModels;

/// <summary>Что делает операция с копией мира сервера у хостера: скачивает копию сюда или заливает копию на сервер.</summary>
public enum HostedWorldJobKind { Backup, Restore }

/// <summary>
/// Идущая операция с копией мира сервера у хостера: что делает, откуда качает и сколько прошло. Ход приходит в поток окна
/// (не чаще раза на мегабайт). Отмена — <see cref="Cancel"/>; итог — <see cref="Completion"/> (не бросает исключений).
/// </summary>
public sealed partial class HostedWorldJob : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<HostedWorldOutcome> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal HostedWorldJob(ModTarget target, HostedWorldJobKind kind, bool scheduled, HostedWorldBackup? backup)
    {
        Target = target;
        Kind = kind;
        Scheduled = scheduled;
        Backup = backup;
    }

    /// <summary>Снимок профиля на начало операции: правка настроек посреди неё ничего не сдвигает.</summary>
    internal ModTarget Target { get; }

    public string ProfileId => Target.ProfileId;
    public HostedWorldJobKind Kind { get; }

    /// <summary>Начата расписанием, а не кнопкой.</summary>
    public bool Scheduled { get; }

    /// <summary>Какую копию заливают на сервер (у скачивания — null).</summary>
    public HostedWorldBackup? Backup { get; }

    /// <summary>Откуда качаем: сам мир или копия игры. Известно после проверки лога; до неё — null.</summary>
    [ObservableProperty] private HostedWorldOrigin? _origin;

    [ObservableProperty] private HostedBackupStep _step;
    [ObservableProperty] private long _done;
    [ObservableProperty] private long _total;

    /// <summary>Передача уже началась (пришёл первый ход).</summary>
    [ObservableProperty] private bool _started;

    /// <summary>Отмену попросили: операция доделывает уборку и скоро кончится.</summary>
    [ObservableProperty] private bool _isCancelling;

    /// <summary>Итог; приходит в поток окна. Исключений не бросает: ошибка — в <see cref="HostedWorldOutcome.Error"/>.</summary>
    public Task<HostedWorldOutcome> Completion => _outcome.Task;

    internal CancellationToken Token => _cts.Token;

    public bool IsFinished => _outcome.Task.IsCompleted;

    /// <summary>Отменить: недокачанное удаляется, недолитое на сервере убирается, прежний мир остаётся.</summary>
    public void Cancel()
    {
        if (IsCancelling || IsFinished) return;
        IsCancelling = true;
        _cts.Cancel();
    }

    internal void Report(HostedTransfer transfer)
    {
        if (IsFinished) return; // последний ход доходит после итога — он уже не нужен
        Step = transfer.Step;
        Total = transfer.Total;
        Done = transfer.Done;
        Started = true;
    }

    // источник отмены не освобождаем: таймеров у него нет, а отмена из окна может прийти в тот же миг, что итог
    internal void Finish(HostedWorldOutcome outcome) => _outcome.TrySetResult(outcome);
}

/// <summary>Чем кончилась операция с копией мира: удалась (<see cref="Error"/> = null), отменена или ошибка.</summary>
public sealed record HostedWorldOutcome(HostedWorldJob Job, Exception? Error)
{
    public HostedBackupResult? Backup { get; init; }
    public HostedRestoreResult? Restore { get; init; }

    /// <summary>Состояние сервера по логу на начало скачивания (у восстановления — null).</summary>
    public HostedServerState? ServerState { get; init; }

    /// <summary>Когда кончилась (UTC, по часам операций).</summary>
    public DateTime At { get; init; }

    public bool Ok => Error is null;
    public bool Cancelled => Error is OperationCanceledException;
    public HostedWorldProblem? Problem => (Error as HostedWorldException)?.Problem;

    /// <summary>Скачать нечего: сервер работает, а свежей целой копии игры нет. Это не сбой — попробовать позже.</summary>
    public bool NoSource => Problem == HostedWorldProblem.NeedsStop;

    /// <summary>Предупреждения ядра — как есть.</summary>
    public IReadOnlyList<string> Warnings => Backup?.Warnings ?? Restore?.Warnings ?? [];

    /// <summary>Итог словами — на текущем языке.</summary>
    public string Text => HostedWorldText.Outcome(this);
}

/// <summary>
/// Операции с копиями мира серверов у хостера — одни на окно: кнопка вкладки «Копии» и расписание сторожа
/// (<see cref="HostedWatch"/>) идут через них, поэтому две операции одного профиля сразу не начнутся. Операция живёт
/// дольше вкладки: сменили профиль или ушли с раздела — она идёт до конца или до отмены, а вкладка, открытая снова,
/// видит её ход. Скачивание и заливка держат очередь соединения профиля (<see cref="HostedSessions"/>) всё время
/// передачи; сама работа — в пуле потоков, окно не ждёт. Всё остальное — в потоке окна.
/// </summary>
/// <param name="time">Часы для имён копий и отметок итогов (тесты двигают их сами).</param>
/// <param name="state">Состояние сервера по логу перед скачиванием; по умолчанию — быстрая проверка логов профиля
/// (<see cref="HostedLogs.CheckNowAsync"/>). Тесты подменяют.</param>
public sealed class HostedWorldJobs(TimeProvider? time = null, Func<ModTarget, CancellationToken, Task<HostedServerState>>? state = null)
{
    private readonly Dictionary<string, HostedWorldJob> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HostedWorldOutcome> _last = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _held = new(StringComparer.Ordinal); // придержанные профили: сколько раз
    private readonly Func<ModTarget, CancellationToken, Task<HostedServerState>> _state =
        state ?? ((target, ct) => HostedLogsPool.For(target).CheckNowAsync(ct, StateTimeout));

    /// <summary>
    /// Сколько ждать проверки лога перед скачиванием или восстановлением. Дольше, чем перед вопросом о модах: соединение
    /// профиля может быть занято опросом раздела или установкой, а «не узнали» здесь значит «не трогать мир».
    /// </summary>
    public static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    public TimeProvider Time { get; } = time ?? TimeProvider.System;

    /// <summary>Операция началась (кнопкой или по расписанию).</summary>
    public event Action<HostedWorldJob>? Started;

    /// <summary>
    /// Операция кончилась (уже не в <see cref="Running"/>, итог — в <see cref="Last"/>). Обработчики зовутся сразу, в потоке
    /// окна. Сторож оповещений на это событие не подписан: итог копии по расписанию он ждёт в <see cref="HostedWorldJob.Completion"/>,
    /// а это продолжение приходит позже обработчиков.
    /// </summary>
    public event Action<HostedWorldOutcome>? Finished;

    /// <summary>Идущая операция профиля; нет — null.</summary>
    public HostedWorldJob? Running(string profileId) => _running.GetValueOrDefault(profileId);

    /// <summary>Идёт хоть одна операция по расписанию.</summary>
    public bool AnyScheduled => _running.Values.Any(j => j.Scheduled);

    /// <summary>Чем кончилась последняя операция профиля в этом окне; не было — null.</summary>
    public HostedWorldOutcome? Last(string profileId) => _last.GetValueOrDefault(profileId);

    /// <summary>
    /// Придержать профиль: пока вкладка спрашивает о восстановлении и смотрит лог, расписание копию этого профиля не
    /// начинает. Иначе копия заняла бы соединение профиля на всю передачу, проверка лога не дождалась бы его и
    /// восстановление отказало бы с непонятной причиной. Кнопку «Скачать копию сейчас» это не держит. Отпустить — Dispose.
    /// </summary>
    public IDisposable Hold(string profileId)
    {
        _held[profileId] = _held.GetValueOrDefault(profileId) + 1;
        return new Release(() =>
        {
            if (_held.TryGetValue(profileId, out var count) && count > 1) _held[profileId] = count - 1;
            else _held.Remove(profileId);
        });
    }

    /// <summary>Профиль придержан (<see cref="Hold"/>): копию по расписанию сейчас не начинать.</summary>
    public bool IsHeld(string profileId) => _held.ContainsKey(profileId);

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
        {
            _release?.Invoke();
            _release = null;
        }
    }

    /// <summary>
    /// Скачать копию мира сюда. Откуда — решает лог: остановлен или вылетел — сам мир, иначе — свежая копия игры из
    /// Backups и BackupSaves. У профиля уже идёт операция — null, вторая не начинается. По расписанию — ещё и null,
    /// пока профиль придержан (<see cref="Hold"/>).
    /// </summary>
    public HostedWorldJob? StartBackup(ModTarget target, bool scheduled)
    {
        if (!target.IsHosted || _running.ContainsKey(target.ProfileId) || scheduled && IsHeld(target.ProfileId)) return null;
        var job = Begin(new HostedWorldJob(target, HostedWorldJobKind.Backup, scheduled, null));
        _ = RunBackupAsync(job);
        return job;
    }

    /// <summary>
    /// Залить копию на сервер. Остановлен ли сервер, решает вызывающий (по логу, <see cref="HostedRestoreOptions.ServerStopped"/>).
    /// У профиля уже идёт операция — null.
    /// </summary>
    public HostedWorldJob? StartRestore(ModTarget target, HostedWorldBackup backup, HostedRestoreOptions options)
    {
        if (!target.IsHosted || _running.ContainsKey(target.ProfileId)) return null;
        var job = Begin(new HostedWorldJob(target, HostedWorldJobKind.Restore, scheduled: false, backup));
        _ = RunRestoreAsync(job, options);
        return job;
    }

    /// <summary>Состояние сервера по логу — то же, что берёт скачивание (вкладка спрашивает его перед восстановлением).</summary>
    public Task<HostedServerState> StateAsync(ModTarget target, CancellationToken ct) => _state(target, ct);

    /// <summary>Профиль удалили — его операцию отменить.</summary>
    public void Cancel(string profileId) => Running(profileId)?.Cancel();

    private HostedWorldJob Begin(HostedWorldJob job)
    {
        _running[job.ProfileId] = job;
        Started?.Invoke(job);
        return job;
    }

    // итог объявляется вне try: обработчик Finished, упавший с ошибкой, не должен превращать удачную операцию в
    // неудачную и объявлять итог второй раз. Его ошибка уходит в задачу, а оттуда — в журнал вылетов окна
    private async Task RunBackupAsync(HostedWorldJob job)
    {
        HostedServerState? state = null;
        HostedWorldOutcome outcome;
        try
        {
            var found = await _state(job.Target, job.Token);
            state = found;
            var stopped = HostedLogs.IsSurelyDown(found);
            job.Origin = stopped ? HostedWorldOrigin.World : HostedWorldOrigin.GameBackup;
            var progress = new Progress<HostedTransfer>(job.Report);
            var backups = new HostedWorldBackups(job.Target) { Time = Time };
            // в пуле потоков целиком: проверка папки (бывает сетевой), сведение журнала SQLite и архив — синхронные
            var result = await Task.Run(() => backups.CreateAsync(stopped, progress, job.Token), CancellationToken.None);
            outcome = new HostedWorldOutcome(job, null) { Backup = result, ServerState = found, At = Now };
        }
        catch (Exception ex)
        {
            Unexpected(ex);
            outcome = new HostedWorldOutcome(job, ex) { ServerState = state, At = Now };
        }
        End(outcome);
    }

    private async Task RunRestoreAsync(HostedWorldJob job, HostedRestoreOptions options)
    {
        HostedWorldOutcome outcome;
        try
        {
            var progress = new Progress<HostedTransfer>(job.Report);
            var backups = new HostedWorldBackups(job.Target) { Time = Time };
            // проверка копии и архива читает файлы целиком — тоже не в потоке окна
            var result = await Task.Run(() => backups.RestoreAsync(job.Backup!, options, progress, job.Token), CancellationToken.None);
            outcome = new HostedWorldOutcome(job, null) { Restore = result, At = Now };
        }
        catch (Exception ex)
        {
            Unexpected(ex);
            outcome = new HostedWorldOutcome(job, ex) { At = Now };
        }
        End(outcome);
    }

    private DateTime Now => Time.GetUtcNow().UtcDateTime;

    private void End(HostedWorldOutcome outcome)
    {
        var job = outcome.Job;
        if (_running.TryGetValue(job.ProfileId, out var running) && ReferenceEquals(running, job)) _running.Remove(job.ProfileId);
        _last[job.ProfileId] = outcome;
        job.Finish(outcome);
        Finished?.Invoke(outcome);
    }

    /// <summary>Ошибки сети, диска и ядра — обычный итог; остальное — ещё и в журнал вылетов, а не молча.</summary>
    internal static void Unexpected(Exception ex)
    {
        if (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException
            or InvalidDataException or TimeoutException))
            Ui.Platform.WriteCrashLog(ex);
    }
}

/// <summary>Тексты операций с копиями мира у хостера: ход и итог (собираются на текущем языке при каждом показе).</summary>
internal static class HostedWorldText
{
    /// <summary>«Скачиваю мир: 12,3 МБ из 90,1 МБ» — или что делается до начала передачи.</summary>
    public static string Progress(HostedWorldJob job)
    {
        if (job.IsCancelling) return Loc.T("hostedbk.cancelling");
        if (!job.Started)
            return job.Kind == HostedWorldJobKind.Restore ? Loc.T("hostedbk.restorePreparing")
                : job.Origin is null ? Loc.T("hostedbk.checking")
                : Loc.T("hostedbk.preparing");
        // возврат настроек и данных модов считается файлами, а не байтами
        if (job.Step == HostedBackupStep.ExtraUpload) return Loc.T("hostedbk.stepExtraUpload", job.Done, job.Total);
        var (done, total) = (Sizes.Format(job.Done), Sizes.Format(job.Total));
        return job.Step switch
        {
            HostedBackupStep.Extra => Loc.T("hostedbk.stepExtra", done, total),
            HostedBackupStep.Upload => Loc.T("hostedbk.stepUpload", done, total),
            _ => job.Origin == HostedWorldOrigin.GameBackup ? Loc.T("hostedbk.stepGameCopy", done, total) : Loc.T("hostedbk.stepWorld", done, total),
        };
    }

    /// <summary>Доля пройденного шага, 0…1.</summary>
    public static double Fraction(HostedWorldJob job) => job.Total > 0 ? Math.Clamp((double)job.Done / job.Total, 0, 1) : 0;

    public static string Outcome(HostedWorldOutcome o) => o.Job.Kind == HostedWorldJobKind.Restore ? RestoreText(o) : BackupText(o);

    private static string BackupText(HostedWorldOutcome o)
    {
        if (o.Backup is { } done)
        {
            var text = Loc.T("hostedbk.done", done.Backup.Name, Sizes.Format(done.Backup.Size + done.Backup.ExtraSize));
            return done.Pruned.Count > 0 ? text + " " + Loc.T("hostedbk.pruned", done.Pruned.Count) : text;
        }
        return o.Error switch
        {
            OperationCanceledException => Loc.T("hostedbk.cancelled"),
            // по логу не понять, остановлен ли сервер: тогда мир не трогаем и ищем копию игры, — сказать, почему так решили
            HostedWorldException { Problem: HostedWorldProblem.NeedsStop } e when o.ServerState is HostedServerState.Unknown =>
                Loc.T("hostedbk.stateUnknown") + " " + e.Message,
            HostedWorldException e => e.Message,
            { } e => Loc.T("hostedbk.failed", HostedServerViewModel.Describe(e)),
            _ => "",
        };
    }

    private static string RestoreText(HostedWorldOutcome o)
    {
        if (o.Restore is { } done)
        {
            var text = Loc.T("hostedbk.restored", o.Job.Backup is { } b ? Dates.Stamp(b.Time) : "?");
            if (done.KeptOldWorld is { } kept) text += " " + Loc.T("hostedbk.restoredKept", kept);
            if (done.ExtraFiles > 0) text += " " + Loc.T("hostedbk.restoredExtras", done.ExtraFiles);
            return text + " " + Loc.T("hostedbk.restoredStart");
        }
        return o.Error switch
        {
            OperationCanceledException => Loc.T("hostedbk.restoreCancelled"),
            HostedWorldException e => e.Message, // в тексте уже сказано, что с миром на сервере
            { } e => Loc.T("hostedbk.restoreFailed", HostedServerViewModel.Describe(e)),
            _ => "",
        };
    }
}
