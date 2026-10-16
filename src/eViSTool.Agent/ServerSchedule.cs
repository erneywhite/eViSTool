using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Notifications;
using eViSTool.Core.Server;

/// <summary>
/// Расписание сервера: копии мира, перезапуски с предупреждениями в чат (и, по желанию, с копией и обновлением модов
/// перед ними), объявления. Шаги зовёт главный цикл раз в несколько секунд; когда сервер закончил копию — узнаём
/// по строкам его консоли (<see cref="WatchBackup"/>).
/// </summary>
internal sealed class ServerSchedule
{
    // большой мир сервер копирует минутами — ждём файл с запасом
    private static readonly TimeSpan BackupWait = TimeSpan.FromMinutes(30);

    private readonly AgentState _state;
    private readonly AgentNotifications _notify;
    private readonly BackupScheduler _scheduler = new();
    private string? _pendingBackup; // имя копии, которую сервер делает по нашей просьбе
    private DateTime _pendingSince = DateTime.MinValue; // когда попросили: сервер мог и не взяться — тогда отметка не должна висеть вечно
    private readonly RestartScheduler _restarts = new(); // перезапуски по расписанию с предупреждениями в чат
    private DateTime? _restartNotified; // о каком перезапуске уже оповестили
    private DateTime? _restartAfterBackup; // перезапуск ждёт копию мира — до этого срока

    // объявления по расписанию — свой файл (вкладка «Объявления»), подхватываем правки так же, как расписание
    private ServerAnnouncements _announcements;
    private DateTime? _announcementsStamp;
    private readonly AnnouncementScheduler _announcer = new();

    public ServerSchedule(AgentState state, AgentNotifications notify)
    {
        _state = state;
        _notify = notify;
        _announcements = ServerAnnouncements.Load(state.Options.ProfileId, state.Options.AgentsDir);
        _announcementsStamp = ServerAnnouncements.ChangedAt(state.Options.ProfileId, state.Options.AgentsDir);
        _scheduler.Seed(state.Backups().List().FirstOrDefault(b => b.IsOwn)?.Time);
    }

    private ServerHost Host => _state.Host;

    public DateTime? LastBackupAt => _scheduler.LastBackupAt;

    public DateTime? NextBackupAt() => _scheduler.NextAt(_state.Automation, Host.State, Host.StartedAt);

    /// <summary>
    /// Копия мира на работающем сервере: её делает сам сервер, мы задаём имя «&lt;профиль&gt;-&lt;время&gt;.vcdbs»
    /// (без имени сервер назвал бы её по файлу мира — «default-…», и было бы не понять, чей это мир).
    /// </summary>
    public async Task RequestBackup()
    {
        var now = DateTime.Now;
        var name = _state.Backups().NameFor(now);
        _pendingBackup = name;
        _pendingSince = now;
        _scheduler.MarkDone(now, _state.Players.Players.Count); // чтобы следующий тик расписания не запустил копию повторно
        await Host.SendCommandAsync("/genbackup " + name);
    }

    // Сам перезапуск по расписанию: последнее слово игрокам — и сервер уходит на перезапуск.
    private async Task RestartBySchedule()
    {
        Host.Console.Add(ConsoleLineKind.System, Loc.T("restart.now"));
        try
        {
            await Host.SendCommandAsync("/announce " + Loc.T("restart.announceNow"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // не дошло объявление — перезапуску это не мешает
        }
        _ = _state.Automation.RestartUpdateMods ? RestartWithModUpdatesAsync() : Host.RestartAsync();
    }

    // Перезапуск с обновлением модов: остановить → поставить вышедшие обновления (сервер не держит файлы) → запустить.
    // Модбаза недоступна или что-то не встало — сервер всё равно поднимается, итог — в консоли.
    private async Task RestartWithModUpdatesAsync()
    {
        await Host.StopAsync();
        Host.Console.Add(ConsoleLineKind.System, Loc.T("autoupd.checking"));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var db = new ModDbClient();
            var result = await _state.Mods.UpdateAllAsync(_state.Automation.UpdatePolicy, _state.Automation.UpdateAllowUnstable,
                new ModUpdater(db), db, cts.Token);
            foreach (var line in result.Updated)
                Host.Console.Add(ConsoleLineKind.System, Loc.T("autoupd.updated", line));
            foreach (var line in result.Skipped)
                Host.Console.Add(ConsoleLineKind.System, line);
            foreach (var line in result.Failed)
                Host.Console.Add(ConsoleLineKind.Error, Loc.T("autoupd.failed", line));
            if (result.Updated.Count + result.Skipped.Count + result.Failed.Count == 0)
                Host.Console.Add(ConsoleLineKind.System, Loc.T("autoupd.none"));
            if (result.Updated.Count + result.Failed.Count > 0)
                // значок — у заголовка, сами моды — простым списком (значки на каждой строке рябили бы)
                _notify.Notify(NotifyEvent.ModsUpdated,
                    Loc.T("notify.ev.modsUpdated", result.Updated.Count),
                    [.. result.Updated.Select(l => new NotifyLine("• " + l)),
                        .. result.Failed.Select(l => new NotifyLine("✗ " + Loc.T("autoupd.failed", l)))]);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or TaskCanceledException
                                       or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            Host.Console.Add(ConsoleLineKind.Error, Loc.T("autoupd.error", ex.Message));
        }
        await Host.StartAsync();
    }

    /// <summary>
    /// Строка консоли: сервер взялся за копию (по расписанию, по кнопке или по команде из консоли). Конец копии узнаём
    /// по файлу, а не по строке «Backup complete!»: её сервер пишет на своём языке, и на русском сервере она не находилась.
    /// </summary>
    public void WatchBackup(ConsoleLine line)
    {
        if (line.Kind != ConsoleLineKind.Output || !BackupStore.IsBackupCommand(line.Text, out var requested)) return;
        var backups = _state.Backups();
        var before = backups.List().Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(async () =>
        {
            var file = await backups.WaitForAsync(requested, before, BackupWait);
            if (requested is not null && requested == _pendingBackup) _pendingBackup = null;
            if (file is null)
            {
                Host.Console.Add(ConsoleLineKind.System, Loc.T("backup.notFound", requested ?? "?"));
                _notify.Notify(NotifyEvent.BackupFailed, Loc.T("notify.ev.backupFailed"),
                    AgentNotifications.Line("▣", "notify.lbl.backup", Loc.T("notify.ev.backupMissing", requested ?? "?")));
                return;
            }
            _scheduler.MarkDone(DateTime.Now, _state.Players.Players.Count);
            var beforeRestart = _restartAfterBackup is not null; // этой копии ждал перезапуск по расписанию
            _restartAfterBackup = null;
            var settings = _state.Automation;
            try
            {
                {
                    var size = SizeText.Format(file.Size);
                    Host.Console.Add(ConsoleLineKind.System, Loc.T("backup.created", file.Name, size));
                    // мир сохранил сервер; данные модов рядом с миром (Saves/XLeveling, ModData) упаковываем сами — сразу после
                    if (backups.PackModData(file) is > 0 and var modSize)
                        Host.Console.Add(ConsoleLineKind.System, Loc.T("backup.modData", SizeText.Format(modSize)));
                    // своя папка для копий: перенести туда (и то, что застряло раньше); недоступна — копия остаётся на сервере
                    if (backups.IsElsewhere)
                        try
                        {
                            file = await Task.Run(() => backups.Relocate(file));
                            Host.Console.Add(ConsoleLineKind.System, Loc.T("backupdir.moved", backups.Dir));
                            if (await Task.Run(backups.RelocateLeftovers) is > 0 and var leftovers)
                                Host.Console.Add(ConsoleLineKind.System, Loc.T("backupdir.leftovers", leftovers));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            var why = ex is UnauthorizedAccessException ? Loc.T("backupdir.denied") : ex.Message;
                            Host.Console.Add(ConsoleLineKind.Error, Loc.T("backupdir.moveFailed", why));
                            _notify.Notify(NotifyEvent.BackupFailed, Loc.T("notify.ev.backupStuck"),
                                AgentNotifications.Line("▣", "notify.lbl.backup", file.Name),
                                AgentNotifications.Line("⚑", "notify.lbl.reason", Loc.T("notify.ev.backupStuckWhy", backups.Dir, why)));
                        }
                    // игрокам — в чат: что копия есть, как называется и сколько весит
                    if (settings.BackupAnnounce && Host.State == ServerState.Running)
                        await Host.SendCommandAsync("/announce " + Loc.T("backup.announce", file.Name, size, file.Time.ToString("dd.MM.yyyy HH:mm")));
                }

                // ротация — при копиях по расписанию; копии перед перезапуском тоже не должны копиться без конца
                if ((settings.BackupEnabled || beforeRestart) && settings.BackupKeep > 0)
                {
                    var removed = backups.Prune(settings.BackupKeep);
                    if (removed.Count > 0) Host.Console.Add(ConsoleLineKind.System, Loc.T("backup.pruned", removed.Count, settings.BackupKeep));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                Host.Console.Add(ConsoleLineKind.System, ex.Message);
            }
            if (beforeRestart && Host.State == ServerState.Running) await RestartBySchedule();
        });
    }

    /// <summary>Из главного цикла: объявления по расписанию — правки подхватываем, в срок — /announce.</summary>
    public async Task AnnounceAsync()
    {
        var changedAt = ServerAnnouncements.ChangedAt(_state.Options.ProfileId, _state.Options.AgentsDir);
        if (changedAt != _announcementsStamp)
        {
            _announcementsStamp = changedAt;
            _announcements = ServerAnnouncements.Load(_state.Options.ProfileId, _state.Options.AgentsDir);
        }
        if (_announcer.Tick(_announcements, DateTime.Now, Host.State, Host.StartedAt, _state.Players.Players.Count) is { } say)
        {
            try { await Host.SendCommandAsync("/announce " + say); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { Host.Console.Add(ConsoleLineKind.System, ex.Message); }
        }
    }

    /// <summary>Из главного цикла: перезапуск по расписанию — сначала предупреждения игрокам, в срок — сам перезапуск.</summary>
    public async Task RestartAsync()
    {
        var automation = _state.Automation;
        if (_restarts.Tick(automation, DateTime.Now, Host.State, Host.StartedAt) is not { } step) return;
        try
        {
            if (!step.Restart)
            {
                Host.Console.Add(ConsoleLineKind.System, Loc.T("restart.warn", step.MinutesLeft));
                // оповещение — одно на перезапуск, по первому предупреждению (дальше игроков предупреждают каждую минуту)
                if (RestartScheduler.NextAt(automation, Host.State, Host.StartedAt) is { } restartAt && restartAt != _restartNotified)
                {
                    _restartNotified = restartAt;
                    _notify.Notify(NotifyEvent.RestartSoon,
                        Loc.T("notify.ev.restartSoon", step.MinutesLeft),
                        AgentNotifications.Line("◷", "notify.lbl.time", restartAt.ToString("HH:mm")),
                        AgentNotifications.Line("◦", "notify.lbl.online", _state.Players.Players.Count.ToString()));
                }
                await Host.SendCommandAsync("/announce " + Loc.Plural("restart.announce", step.MinutesLeft));
            }
            else if (!automation.RestartBackup)
            {
                await RestartBySchedule();
            }
            else if (!_scheduler.NeedsCopy(automation))
            {
                Host.Console.Add(ConsoleLineKind.System, Loc.T("restart.backupSkipped"));
                await RestartBySchedule();
            }
            else
            {
                // сначала копия мира; перезапуск — когда сервер её закончит (файл копии готов — см. WatchBackup)
                _restartAfterBackup = DateTime.Now + ServerAutomation.RestartBackupWait;
                Host.Console.Add(ConsoleLineKind.System, Loc.T("restart.backupFirst"));
                await Host.SendCommandAsync("/announce " + Loc.T("restart.announceBackup"));
                // копия уже идёт — ждём её; отметка старше получаса — сервер за копию так и не взялся, просим заново
                if (_pendingBackup is null || DateTime.Now - _pendingSince > BackupWait) await RequestBackup();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Host.Console.Add(ConsoleLineKind.System, ex.Message);
            if (_restartAfterBackup is not null) _restartAfterBackup = DateTime.Now; // копию запросить не вышло — не ждём её
        }
    }

    /// <summary>
    /// Из главного цикла: перезапуск ждёт копию. Сервер за это время остановили — перезапускать нечего; копия
    /// не успела — идём без неё.
    /// </summary>
    public async Task RestartAfterBackupAsync()
    {
        if (_restartAfterBackup is not { } deadline) return;
        if (Host.State != ServerState.Running)
        {
            _restartAfterBackup = null;
        }
        else if (DateTime.Now >= deadline)
        {
            _restartAfterBackup = null;
            Host.Console.Add(ConsoleLineKind.System, Loc.T("restart.backupFailed"));
            _notify.Notify(NotifyEvent.BackupFailed, Loc.T("notify.ev.backupFailed"),
                AgentNotifications.Line("▣", "notify.lbl.backup", Loc.T("notify.ev.backupBeforeRestart")));
            await RestartBySchedule();
        }
    }

    /// <summary>Из главного цикла: сколько игроков на сервере — копия «только если играли» смотрит на это.</summary>
    public void NotePlayers() => _scheduler.NotePlayers(_state.Players.Players.Count);

    /// <summary>Из главного цикла: подошло время копии по расписанию — попросить сервер.</summary>
    public async Task BackupAsync()
    {
        if (!_scheduler.IsDue(_state.Automation, DateTime.Now, Host.State, Host.StartedAt)) return;
        try
        {
            Host.Console.Add(ConsoleLineKind.System, Loc.T("backup.scheduled"));
            await RequestBackup();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Host.Console.Add(ConsoleLineKind.System, ex.Message);
        }
    }
}
