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
    }

    /// <summary>
    /// Профилям с включённым расписанием, которым пора (последняя своя копия в папке старше суток или недели), — скачать
    /// копию тем же путём, что кнопка (<see cref="HostedWorldJobs.StartBackup"/>). Одна копия по расписанию за раз; у
    /// профиля уже идёт операция (кнопка, восстановление) — не трогаем. Время последней копии — по папке копий: так оно
    /// переживает перезапуск окна; папку читаем в пуле потоков и только когда по памяти уже пора (папка бывает сетевой).
    /// </summary>
    private async Task CheckBackupsAsync()
    {
        if (_jobs is not { } jobs) return;
        var now = UtcNow;
        _backupsSince ??= now;
        if (now - _backupsSince < BackupFirstDelay || jobs.AnyScheduled) return;
        foreach (var profile in Profiles.Select(p => p.Model).Where(p => p.IsHosted).ToList())
        {
            if (profile.Hosted?.BackupSchedule is not (HostedBackupSchedule.Daily or HostedBackupSchedule.Weekly) || ModTarget.For(profile) is not
                { IsHosted: true } target)
            {
                _plans.Remove(profile.Id); // выключили — включат снова с чистого листа
                continue;
            }
            if (jobs.Running(profile.Id) is not null) continue;
            if (!_plans.TryGetValue(profile.Id, out var plan)) _plans[profile.Id] = plan = new BackupPlan();
            if (now < plan.RetryAt) continue;
            var period = PeriodOf(profile.Hosted.BackupSchedule);
            var backups = new HostedWorldBackups(target) { Time = _time };
            if (!plan.Read || plan.Dir != backups.Dir || plan.Last is not { } known || now - known >= period)
            {
                // по памяти пора (или не знаем) — свериться с папкой: копию могли сделать кнопкой или в другом окне
                plan.Last = await Task.Run(() => LastOwn(backups));
                plan.Dir = backups.Dir;
                plan.Read = true;
                now = UtcNow;
            }
            if (plan.Last is { } last && now - last < period)
            {
                plan.DueSince = null;
                continue;
            }
            plan.DueSince ??= now;
            if (jobs.Running(profile.Id) is not null || jobs.AnyScheduled) return; // пока читали папку, начали копию
            if (jobs.StartBackup(target, scheduled: true) is { } job)
            {
                _following[profile.Id] = FollowAsync(job, profile.Id, period);
                return;
            }
        }
    }

    /// <summary>Последняя своя копия в папке (UTC, по отметке в имени); папки нет или своих копий нет — null.</summary>
    private static DateTime? LastOwn(HostedWorldBackups backups) =>
        backups.List().Where(b => b.IsOwn).Select(b => (DateTime?)Utc(b.Time)).Max();

    private static DateTime Utc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    /// <summary>
    /// Итог копии по расписанию. Удалась — без оповещения, следующая через срок. Отменили — попробовать через час, это
    /// не сбой. Скачать нечего (сервер работает, а свежей копии игры нет) — тоже через час и молча, пока это длится
    /// меньше срока расписания; дольше — это уже неудача. Неудача — оповещение «Копия мира не получилась» один раз, пока
    /// не случится удачная копия, и следующая попытка через час.
    /// </summary>
    private async Task FollowAsync(HostedWorldJob job, string profileId, TimeSpan period)
    {
        var outcome = await job.Completion;
        var now = UtcNow;
        if (!_plans.TryGetValue(profileId, out var plan)) return; // расписание выключили, пока качали
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
