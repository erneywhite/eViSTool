using eViSTool.Core.Hosting;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Notifications;

namespace eViSTool.Presentation.ViewModels;

// Копии мира серверов у хостера по расписанию — тем же таймером, что оповещения. Отдельным файлом, чтобы не трогать сторожа.
public sealed partial class HostedWatch
{
    /// <summary>Скачать было нечего (сервер работает, копии игры нет) или не вышло — следующая попытка через столько.</summary>
    public static readonly TimeSpan BackupRetry = TimeSpan.FromHours(1);

    /// <summary>
    /// Первая копия по расписанию — не сразу после запуска окна: передача держит соединение профиля, и моды в первые
    /// минуты работы ждали бы её.
    /// </summary>
    public static readonly TimeSpan BackupFirstDelay = TimeSpan.FromMinutes(5);

    /// <summary>Срок расписания: последняя копия старше — пора.</summary>
    public static TimeSpan PeriodOf(HostedBackupSchedule schedule) =>
        schedule == HostedBackupSchedule.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);

    /// <summary>
    /// Сколько проход ждёт чтения папки копий. Сетевая папка, которая не отвечает (SMB — десятки секунд, NFS с hard —
    /// бесконечно), не должна держать проход: с ним стоят оповещения всех серверов. Не дождались — чтение идёт дальше
    /// само, а его итог заберёт один из следующих проходов. Тесты укорачивают.
    /// </summary>
    internal TimeSpan BackupReadWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Чтение последней своей копии в папке; null — настоящее (<see cref="LastOwn"/>). Тесты придерживают его.</summary>
    internal Func<HostedWorldBackups, DateTime?>? ReadLastOwn { get; set; }

    private readonly Dictionary<string, BackupPlan> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _following = new(StringComparer.Ordinal);
    private DateTime? _backupsSince; // первый проход после запуска окна (UTC)

    /// <summary>Итог копии по расписанию разобран (оповещение, если нужно, поставлено) — для тестов.</summary>
    internal Task BackupFollowedAsync(string profileId) => _following.GetValueOrDefault(profileId) ?? Task.CompletedTask;

    /// <summary>Что сторож помнит о копиях профиля по расписанию. Только в памяти окна: время последней копии — по папке.</summary>
    private sealed class BackupPlan
    {
        public string? Dir;           // папка, к которой относится Last
        public DateTime? Last;        // последняя своя копия в папке (UTC); null — копий нет
        public bool Read;             // папку уже читали
        public DateTime RetryAt;      // раньше не пробовать (UTC)
        public DateTime? DueSince;    // с какого прохода копию пора сделать (UTC)
        public bool Notified;         // о неудаче этой полосы уже сказали
        public Task<DateTime?>? Reading; // чтение папки, не успевшее за свой проход
        public string? ReadingDir;    // какой папки
    }

    /// <summary>
    /// Профилям с включённым расписанием, которым пора (последняя своя копия в папке старше суток или недели), — скачать
    /// копию тем же путём, что кнопка (<see cref="HostedWorldJobs.StartBackup"/>). Одна копия по расписанию за раз; у
    /// профиля уже идёт операция (кнопка, восстановление) или вкладка его придержала (<see cref="HostedWorldJobs.Hold"/>) —
    /// не трогаем. Время последней копии — по папке копий: так оно переживает перезапуск окна; папку читаем в пуле
    /// потоков, только когда по памяти уже пора, и ждём не дольше <see cref="BackupReadWait"/> (папка бывает сетевой).
    /// </summary>
    private async Task CheckBackupsAsync()
    {
        if (_jobs is not { } jobs) return;
        var now = UtcNow;
        _backupsSince ??= now;
        var ids = Profiles.Select(p => p.Model).Where(p => p.IsHosted).Select(p => p.Id).ToList();
        foreach (var gone in _plans.Keys.Except(ids, StringComparer.Ordinal).ToList()) _plans.Remove(gone); // профиль удалили
        if (now - _backupsSince < BackupFirstDelay || jobs.AnyScheduled) return;
        foreach (var id in ids)
        {
            if (Plan(id) is not { } planned) continue;
            var (target, period, plan) = planned;
            if (jobs.Running(id) is not null || jobs.IsHeld(id) || now < plan.RetryAt) continue;
            var backups = new HostedWorldBackups(target) { Time = _time };
            if (!plan.Read || plan.Dir != backups.Dir || plan.Last is not { } known || now - known >= period)
            {
                // по памяти пора (или не знаем) — свериться с папкой: копию могли сделать кнопкой или в другом окне
                if (!await ReadLastAsync(plan, backups)) continue; // папка молчит
                now = UtcNow;
                // пока читали, могли удалить профиль, выключить расписание или сменить папку, начать копию кнопкой, придержать
                // профиль восстановлением, а неудачная копия по расписанию — назначить повтор: берём то, что сейчас
                if (Plan(id) is not { } fresh || !ReferenceEquals(fresh.Plan, plan)) continue;
                (target, period) = (fresh.Target, fresh.Period);
                if (new HostedWorldBackups(target).Dir != plan.Dir) continue; // папку сменили — прочитаем новую следующим проходом
                if (jobs.Running(id) is not null || jobs.IsHeld(id) || now < plan.RetryAt) continue;
            }
            if (plan.Last is { } last && now - last < period)
            {
                plan.DueSince = null;
                continue;
            }
            plan.DueSince ??= now;
            if (jobs.AnyScheduled) return; // пока читали папку, началась другая копия по расписанию
            if (jobs.StartBackup(target, scheduled: true) is { } job)
            {
                _following[id] = FollowAsync(job, id, period);
                return;
            }
        }
    }

    /// <summary>
    /// Расписание профиля сейчас: профиль есть, он у хостера, копии по расписанию включены и пароль читается — цель,
    /// срок и что о нём помнится (нет — заводится). Иначе — null, и память о нём забыта: включат снова — с чистого листа.
    /// </summary>
    private (ModTarget Target, TimeSpan Period, BackupPlan Plan)? Plan(string profileId)
    {
        if (ScheduleOf(profileId) is not { } schedule)
        {
            _plans.Remove(profileId);
            return null;
        }
        if (!_plans.TryGetValue(profileId, out var plan)) _plans[profileId] = plan = new BackupPlan();
        return (schedule.Target, schedule.Period, plan);
    }

    private (ModTarget Target, TimeSpan Period)? ScheduleOf(string profileId)
    {
        var profile = Profiles.Select(p => p.Model).FirstOrDefault(p => p.Id == profileId && p.IsHosted);
        if (profile?.Hosted is not { BackupSchedule: HostedBackupSchedule.Daily or HostedBackupSchedule.Weekly } hosted) return null;
        return ModTarget.For(profile) is { IsHosted: true } target ? (target, PeriodOf(hosted.BackupSchedule)) : null;
    }

    /// <summary>
    /// Прочитать папку копий в пуле потоков и запомнить последнюю свою копию. Ждём не дольше <see cref="BackupReadWait"/>:
    /// не дождались — false, чтение продолжается. Следующие проходы нового не начинают и его не ждут, а только смотрят,
    /// кончилось ли оно, и забирают итог.
    /// </summary>
    private async Task<bool> ReadLastAsync(BackupPlan plan, HostedWorldBackups backups)
    {
        // shortcut: чтение, повисшее навсегда (NFS с hard), держит поток пула, и копий этого профиля по расписанию больше
        // не будет до перезапуска окна (оповещения и остальные профили идут). Переделать, если такое встретится
        var fresh = plan.Reading is null || plan.ReadingDir != backups.Dir;
        if (fresh)
        {
            var read = ReadLastOwn ?? LastOwn;
            plan.Reading = Task.Run(() => read(backups));
            plan.ReadingDir = backups.Dir;
        }
        var reading = plan.Reading!;
        if (fresh && !reading.IsCompleted) await Task.WhenAny(reading, Task.Delay(BackupReadWait));
        if (!reading.IsCompleted) return false;
        plan.Reading = null;
        plan.Last = await reading;
        plan.Dir = plan.ReadingDir;
        plan.Read = true;
        return true;
    }

    /// <summary>Последняя своя копия в папке (UTC, по отметке в имени); папки нет или своих копий нет — null.</summary>
    private static DateTime? LastOwn(HostedWorldBackups backups) =>
        backups.List().Where(b => b.IsOwn).Select(b => (DateTime?)Utc(b.Time)).Max();

    private static DateTime Utc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    /// <summary>
    /// Итог копии по расписанию. Удалась — без оповещения, следующая через срок. Отменили — попробовать через час, это
    /// не сбой. Скачать нечего (сервер работает, а свежей копии игры нет) — тоже через час и молча, пока это длится
    /// меньше срока расписания; дольше — это уже неудача. Неудача — оповещение «Копия мира не получилась» один раз, пока
    /// не случится удачная копия, и следующая попытка через час. Пока качали, профиль удалили или расписание выключили —
    /// итог ничей: ни оповещения, ни повтора.
    /// </summary>
    private async Task FollowAsync(HostedWorldJob job, string profileId, TimeSpan period)
    {
        var outcome = await job.Completion;
        var now = UtcNow;
        if (ScheduleOf(profileId) is null || !_plans.TryGetValue(profileId, out var plan))
        {
            _plans.Remove(profileId);
            return;
        }
        if (outcome.Backup is { } done)
        {
            plan.Last = Utc(done.Backup.Time);
            plan.Read = true;
            plan.RetryAt = DateTime.MinValue;
            plan.DueSince = null;
            plan.Notified = false;
            return;
        }
        plan.RetryAt = now + BackupRetry;
        if (outcome.Cancelled) return;
        // shortcut: «скачать нечего уже целый срок» считается с первой попытки в этом запуске окна — окно, которое
        // перезапускают каждый день, об этом не скажет. Переделать (помнить на диске), если окажется, что так бывает
        if (outcome.NoSource && !(plan.DueSince is { } since && now - since >= period)) return;
        if (plan.Notified) return;
        plan.Notified = true;
        var profile = Profiles.Select(p => p.Model).FirstOrDefault(p => p.Id == profileId) ?? job.Target.Profile;
        Notify(profile, NotifyEvent.BackupFailed, Loc.T("notify.ev.backupFailed"),
        [
            Line("▣", Loc.T("notify.lbl.backup"), Loc.T("notify.ev.backupToPc", Environment.MachineName)),
            Line("⚑", Loc.T("notify.lbl.reason"), outcome.Text),
        ]);
    }
}
