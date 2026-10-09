using System.Reflection;
using System.Runtime.InteropServices;
using eViSTool.Core;
using eViSTool.Core.Game;
using eViSTool.Core.Diagnostics;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using eViSTool.Core.Server.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

// eViSTool.Agent — держит процесс сервера VS и принимает команды по HTTP.
// Окно eViSTool можно закрыть или уронить: сервер продолжит работать, сторож — сторожить.
//
//   eViSTool.Agent.exe --profile <id> --exe <VintagestoryServer.exe> --data <папка данных> [--arg <доп. аргумент>]…
//   eViSTool.Agent [--game <папка игры>] [--data <папка данных>] [--profile <id>] [--start]   — без окна (Linux)
//   eViSTool.Agent <команда> …   — setup, remote, status, start, stop, restart, command, service (см. Cli/Commands.cs)
//
// Без окна пути не передаёт никто: сервер ищет ServerLocator (рядом, в соседних папках, по server.sh), профиль — «server».
// Слушает только 127.0.0.1, каждый запрос — с ключом профиля. Адрес пишет в data/agents/<id>.json.

var cli = AgentArgs.Parse(args);
// язык сообщений: окно передаёт свой, без окна — язык системы (нет такого словаря — английский)
eViSTool.Core.Localization.Loc.Instance.SetLanguage(cli.Language ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
// команды (setup, remote, status…, service) — выполнить и выйти; без команды агент работает дальше
if (Commands.Run(cli, args) is { } exitCode) return exitCode;

var tried = new List<string>();
if (AgentOptions.From(cli, tried) is not { } opts)
{
    Console.Error.WriteLine(eViSTool.Core.Localization.Loc.T("agent.notFound", string.Join(", ", tried)));
    Console.Error.WriteLine(eViSTool.Core.Localization.Loc.T("agent.notFoundHint"));
    return 2;
}

// один агент на профиль; копия, запущенная после обновления, ждёт, пока прежняя освободит профиль и выйдет
using var profileLock = ProfileLock.Take(opts.ProfileId, opts.AgentsDir ?? AgentProtocol.DefaultAgentsDir,
    opts.AfterUpdate ? TimeSpan.FromSeconds(90) : TimeSpan.Zero);
if (profileLock is null)
{
    Console.Error.WriteLine(eViSTool.Core.Localization.Loc.T("agent.alreadyRunning", opts.ProfileId));
    return 3;
}
if (opts.Located is { } located)
    Console.WriteLine(located.Script is { } script
        ? eViSTool.Core.Localization.Loc.T("agent.foundScript", located.GameDir, located.DataDir, script)
        : eViSTool.Core.Localization.Loc.T("agent.found", located.GameDir, located.DataDir));

// Ctrl+C, который мы шлём серверу через общую консоль, самого агента ронять не должен.
// На Linux общей консоли нет: Ctrl+C и SIGTERM — просьба агенту выйти (ниже, рядом с shutdown)
if (OperatingSystem.IsWindows()) Console.CancelKeyPress += (_, e) => e.Cancel = true;

var key = AgentProtocol.GetOrCreateKey(opts.ProfileId, opts.AgentsDir);
var version = typeof(AgentOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
var host = new ServerHost(new ServerHostOptions
{
    ExePath = opts.ExePath,
    DataPath = opts.DataPath,
    ExtraArgs = opts.ExtraArgs,
}, new SharedConsoleCtrlC());

// кто на сервере — по строкам консоли; остановился или запускается заново — никого
var players = new PlayerTracker();
host.Console.LineAdded += line =>
{
    if (line.Kind == ConsoleLineKind.Output) players.Process(line.Text, line.Time);
};
host.StateChanged += state =>
{
    if (state is ServerState.Stopped or ServerState.Starting) players.Reset();
};


// резервные копии по расписанию: копию делает сам сервер (/genbackup), агент решает когда и убирает старые
var automation = ServerAutomation.Load(opts.ProfileId, opts.AgentsDir);
var automationFile = ServerAutomation.FileFor(opts.ProfileId, opts.AgentsDir);
var automationStamp = File.Exists(automationFile) ? File.GetLastWriteTimeUtc(automationFile) : default;
// объявления по расписанию — свой файл (вкладка «Объявления»), подхватываем правки так же
var announcements = ServerAnnouncements.Load(opts.ProfileId, opts.AgentsDir);
var announcementsStamp = ServerAnnouncements.ChangedAt(opts.ProfileId, opts.AgentsDir);
var announcer = new AnnouncementScheduler();

// оповещения (вкладка «Оповещения»): настройки читаются при каждом событии, отправка — в фоне, сбой — строкой в консоль
using var notifyHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var notifier = new eViSTool.Core.Notifications.ServerNotifier(
    () => eViSTool.Core.Notifications.ServerNotifySettings.Load(opts.ProfileId, opts.AgentsDir), notifyHttp,
    (channel, error) => host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("notify.sendFailed", channel, error)));
void Notify(eViSTool.Core.Notifications.NotifyEvent e, string title, params eViSTool.Core.Notifications.NotifyLine[] details) =>
    notifier.Notify(e, title, details);
static eViSTool.Core.Notifications.NotifyLine Line(string icon, string labelKey, string text) =>
    new(icon, eViSTool.Core.Localization.Loc.T(labelKey), text);

// запуск и остановка; «дошёл ли до работы» — чтобы отличить падение при запуске от падения в работе
var reachedRunning = false;
host.StateChanged += state =>
{
    switch (state)
    {
        case ServerState.Starting:
            reachedRunning = false;
            break;
        case ServerState.Running:
            reachedRunning = true;
            Notify(eViSTool.Core.Notifications.NotifyEvent.ServerStarted, eViSTool.Core.Localization.Loc.T("notify.ev.started"));
            break;
        case ServerState.Stopped when !host.LastExitOnItsOwn:
            Notify(eViSTool.Core.Notifications.NotifyEvent.ServerStopped, eViSTool.Core.Localization.Loc.T("notify.ev.stopped"));
            break;
    }
};

// «сервер не успевает»: много «Server overloaded» за короткое время — с подсказкой, памяти ли не хватает
var overloads = new OverloadWatch();
host.Console.LineAdded += line =>
{
    if (line.Kind != ConsoleLineKind.Output || overloads.Add(line.Text, DateTime.Now) is not { } count) return;
    var details = new List<eViSTool.Core.Notifications.NotifyLine>
    {
        Line("◷", "notify.lbl.warnings", eViSTool.Core.Localization.Loc.T("notify.ev.overloadCount", count, (int)OverloadWatch.Window.TotalMinutes)),
    };
    if (SystemMemory.Status() is { } mem)
        details.Add(SystemMemory.IsLow(mem)
            ? Line("▣", "notify.lbl.memory", eViSTool.Core.Localization.Loc.T("notify.ev.memoryLow", mem.LoadPercent, mem.FreeMb))
            : Line("⚑", "notify.lbl.hint", eViSTool.Core.Localization.Loc.T("notify.ev.overloadHint")));
    Notify(eViSTool.Core.Notifications.NotifyEvent.Overloaded, eViSTool.Core.Localization.Loc.T("notify.ev.overloaded"), [.. details]);
};

// чат игры → Discord: общий чат игроков, по желанию — входы и выходы; настройки читаются на каждое сообщение
var chatRelay = new eViSTool.Core.Notifications.ChatRelay(
    () => eViSTool.Core.Notifications.ServerNotifySettings.Load(opts.ProfileId, opts.AgentsDir).ChatChannel, notifyHttp,
    error => host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("chat.consoleFailed", error)));
host.Console.LineAdded += line =>
{
    if (line.Kind != ConsoleLineKind.Output || eViSTool.Core.Notifications.ChatBridge.Parse(line.Text) is not { } post) return;
    if (eViSTool.Core.Notifications.ServerNotifySettings.Load(opts.ProfileId, opts.AgentsDir).ChatChannel is not null) chatRelay.Post(post);
};
void ChatJoinLeave(string text)
{
    var s = eViSTool.Core.Notifications.ServerNotifySettings.Load(opts.ProfileId, opts.AgentsDir);
    if (s.ChatChannel is not null && s.ChatJoins)
        chatRelay.Post(new eViSTool.Core.Notifications.ChatPost(string.IsNullOrWhiteSpace(s.ServerName) ? "Vintage Story" : s.ServerName, text));
}

// статистика (если включена): раз в минуту — игроки, память, процессор; плюс входы, выходы, запуски и вылеты
var stats = new StatsStore(StatsStore.DirFor(opts.ProfileId, opts.AgentsDir));
var cpuMeter = new CpuMeter();
var nextSample = DateTime.Now.AddMinutes(1);
var nextStatsPrune = DateTime.Now.AddMinutes(2);
void Stat(StatsEntry entry)
{
    try
    {
        if (stats.IsEnabled) stats.Append(entry);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // не записалось — статистика не повод мешать серверу
    }
}
host.StateChanged += state =>
{
    if (state == ServerState.Running) Stat(new StatsEntry(DateTime.Now, StatsKind.Up));
    // сам остановился после запуска — упал (как и для оповещения «сервер упал»)
    else if (state == ServerState.Stopped)
        Stat(new StatsEntry(DateTime.Now, host.LastExitOnItsOwn && reachedRunning ? StatsKind.Crash : StatsKind.Down));
};

// кто зашёл и вышел: разница составов (сервер останавливается — это не «все вышли»)
var knownPlayers = new HashSet<string>(StringComparer.Ordinal);
players.Changed += () =>
{
    var now = players.Players.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    if (host.State == ServerState.Running)
    {
        foreach (var name in now.Except(knownPlayers))
            Stat(new StatsEntry(DateTime.Now, StatsKind.Join, Name: name));
        foreach (var name in knownPlayers.Except(now))
            Stat(new StatsEntry(DateTime.Now, StatsKind.Leave, Name: name));
        foreach (var name in now.Except(knownPlayers))
            ChatJoinLeave(eViSTool.Core.Localization.Loc.T("chat.joined", name, now.Count));
        foreach (var name in knownPlayers.Except(now))
            ChatJoinLeave(eViSTool.Core.Localization.Loc.T("chat.left", name, now.Count));
        foreach (var name in now.Except(knownPlayers))
            Notify(eViSTool.Core.Notifications.NotifyEvent.PlayerJoined, eViSTool.Core.Localization.Loc.T("notify.ev.joined", name),
                Line("◦", "notify.lbl.online", now.Count.ToString()));
        foreach (var name in knownPlayers.Except(now))
            Notify(eViSTool.Core.Notifications.NotifyEvent.PlayerLeft, eViSTool.Core.Localization.Loc.T("notify.ev.left", name),
                Line("◦", "notify.lbl.online", now.Count.ToString()));
    }
    knownPlayers = now;
};
// папка для копий берётся из расписания — её могут поменять, пока агент работает
BackupStore Backups() => new(opts.DataPath, opts.BackupName, automation.BackupDir);
var files = new ServerFiles(opts.ProfileId, opts.DataPath, opts.BackupName, opts.AgentsDir); // для окна на другой машине
// команды сервера — из его ответа на /help (с командами модов); помним между запусками, чтобы подсказки были сразу
var commandsFile = ServerCommands.FileFor(opts.ProfileId, opts.AgentsDir);
var commands = ServerCommands.Load(commandsFile).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
var commandsDirty = false;
var mods = new ServerMods(opts.ProfileId, Path.GetDirectoryName(Path.GetFullPath(opts.ExePath)) ?? "", opts.DataPath);
var playerLists = new ServerPlayers(opts.DataPath); // вкладка «Игроки» окна на другой машине

// оповещения о здоровье сервера: место на диске, обновления модов
var lowDisk = new LowDiskWatch();
var nextDiskCheck = DateTime.Now.AddMinutes(1);
var nextModCheck = DateTime.Now.AddMinutes(10); // не сразу при запуске: агент мог подняться ради одной команды
// какой список обновлений уже присылали — помним между запусками агента, чтобы не повторять одно и то же
var modUpdatesFile = Path.Combine(opts.AgentsDir ?? AgentProtocol.DefaultAgentsDir, $"{opts.ProfileId}.modupdates.json");

async Task CheckModUpdatesAsync()
{
    var settings = eViSTool.Core.Notifications.ServerNotifySettings.Load(opts.ProfileId, opts.AgentsDir);
    if (!settings.ChannelsFor(eViSTool.Core.Notifications.NotifyEvent.ModUpdates).Any()) return;
    try
    {
        using var db = new eViSTool.Core.ModDb.ModDbClient();
        var list = await mods.AvailableUpdatesAsync(automation.UpdatePolicy, automation.UpdateAllowUnstable, db);
        var seen = File.Exists(modUpdatesFile) ? JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(modUpdatesFile)) ?? [] : [];
        if (list.Count > 0 && !list.SequenceEqual(seen))
            Notify(eViSTool.Core.Notifications.NotifyEvent.ModUpdates, eViSTool.Core.Localization.Loc.T("notify.ev.modUpdates", list.Count),
                [.. list.Take(30).Select(l => new eViSTool.Core.Notifications.NotifyLine("• " + l)),
                 .. list.Count > 30 ? [new eViSTool.Core.Notifications.NotifyLine(eViSTool.Core.Localization.Loc.T("notify.ev.andMore", list.Count - 30))] : Array.Empty<eViSTool.Core.Notifications.NotifyLine>(),
                 Line("⚑", "notify.lbl.hint", eViSTool.Core.Localization.Loc.T(automation.RestartUpdateMods ? "notify.ev.modUpdatesAuto" : "notify.ev.modUpdatesHint"))]);
        File.WriteAllText(modUpdatesFile, JsonConvert.SerializeObject(list));
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                   or UnauthorizedAccessException or JsonException)
    {
        // модбаза недоступна — попробуем завтра; это не повод тревожить
    }
}
// настройки модов (ModConfig) — для окна на другой машине; прежние версии хранятся здесь, рядом с файлами
var modConfigs = new ModConfigService(opts.DataPath,
    new ModConfigBackups(Path.Combine(eViSTool.Core.AppPaths.ModConfigBackups, opts.ProfileId)), mods.Locals);

// «ошибки модов» за запуск сервера: раз в полминуты — новые строки вывода в сборщик (консоль хранит только
// последние строки, поэтому копим по ходу), сводка — в статусе. Новый запуск — счёт с нуля
var errorCollector = new ModErrorCollector();
DateTime? errorRun = null;
long errorSeq = 0;
var nextErrorScan = DateTime.MinValue;
ModFingerprints? errorPrints = null;
ModErrorReport? modErrors = null;
var errorLock = new object();

// из главного цикла и при остановке сервера — по очереди
void ScanModErrors()
{
    lock (errorLock) ScanModErrorsLocked();
}

void ScanModErrorsLocked()
{
    if (errorRun is null) return;
    var lines = host.Console.GetSince(errorSeq, 100_000);
    if (lines.Count == 0) return;
    errorSeq = lines[^1].Seq;
    errorCollector.Add(lines.Where(l => l.Kind is ConsoleLineKind.Output or ConsoleLineKind.Error).Select(l => l.Text),
        final: host.State == ServerState.Stopped);
    if (errorCollector.Errors.Count == 0) return;
    errorPrints ??= ModFingerprints.Build(mods.Locals()); // моды читаем раз за запуск — zip-ов может быть сотня
    modErrors = ModErrorReport.Build(errorCollector.Errors, errorPrints, errorRun.Value);
}

// сервер остановился сам (упал, выключился от ошибок) — разобрать его вывод за этот запуск: какой мод виноват.
// Результат — в статусе (LastCrash): окна всех, кто следит за этим сервером, покажут оповещение
ServerCrashInfo? lastCrash = null;
// новый запуск — счёт ошибок с нуля, с его первой строки (сервер может упасть раньше первого обхода)
host.StateChanged += state =>
{
    if (state != ServerState.Starting) return;
    lock (errorLock)
        (errorCollector, errorRun, errorSeq, errorPrints) = (new ModErrorCollector(), DateTime.Now, host.SessionStartSeq, null);
};

// сервер остановился (как угодно) — дочитать его ошибки сразу, не дожидаясь обхода раз в полминуты
host.StateChanged += state =>
{
    if (state != ServerState.Stopped) return;
    _ = Task.Run(() =>
    {
        try { ScanModErrors(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    });
};

host.StateChanged += state =>
{
    if (state != ServerState.Stopped || !host.LastExitOnItsOwn) return;
    var (startSeq, crashed, started) = (host.SessionStartSeq, host.LastExitCrashed, reachedRunning);
    _ = Task.Run(() =>
    {
        try
        {
            var lines = host.Console.GetSince(startSeq, 100_000)
                .Where(l => l.Kind is ConsoleLineKind.Output or ConsoleLineKind.Error).Select(l => l.Text).ToList();
            string? report = null;
            if (CrashAnalyzer.CrashFilePath(lines) is { } path && File.Exists(path)) report = File.ReadAllText(path);
            var finding = CrashAnalyzer.AnalyzeServer(GameLog.Parse(lines), report, crashed, ModFingerprints.Build(mods.Locals()));
            var cause = finding?.Culprit is { } c
                ? eViSTool.Core.Localization.Loc.T("srv.crashCulprit", c.Mod?.Name ?? c.ModId, c.Version ?? "")
                : eViSTool.Core.Localization.Loc.T("srv.crashNoCulprit");
            // в оповещении — без ссылок на «строки выше»: консоли там не видно. Сторож поднимет сервер сам — так и скажем
            var why = finding?.Culprit is { } culprit
                ? eViSTool.Core.Localization.Loc.T("notify.ev.culprit", culprit.Mod?.Name ?? culprit.ModId, culprit.Version ?? "")
                : eViSTool.Core.Localization.Loc.T("notify.ev.noCulprit");
            var details = new List<eViSTool.Core.Notifications.NotifyLine> { Line("⚑", "notify.lbl.mod", why) };
            if (host.RestartScheduledAt is { } at)
                details.Add(Line("↻", "notify.lbl.watchdog",
                    eViSTool.Core.Localization.Loc.T("notify.ev.restartIn", Math.Max(1, (int)(at - DateTime.Now).TotalSeconds))));
            Notify(started ? eViSTool.Core.Notifications.NotifyEvent.ServerCrashed : eViSTool.Core.Notifications.NotifyEvent.StartFailed,
                started ? eViSTool.Core.Localization.Loc.T("notify.ev.crashed") : eViSTool.Core.Localization.Loc.T("notify.ev.startFailed"),
                [.. details]);
            if (finding is null) return;
            lastCrash = ServerCrashInfo.From(finding, DateTime.Now);
            host.Console.Add(ConsoleLineKind.System, cause);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    });
};
var scheduler = new BackupScheduler();
scheduler.Seed(Backups().List().FirstOrDefault(b => b.IsOwn)?.Time);
string? pendingBackup = null; // имя копии, которую сервер делает по нашей просьбе
var pendingSince = DateTime.MinValue; // когда попросили: сервер мог и не взяться — тогда отметка не должна висеть вечно
var restarts = new RestartScheduler(); // перезапуски по расписанию с предупреждениями в чат
DateTime? restartNotified = null; // о каком перезапуске уже оповестили
DateTime? restartAfterBackup = null; // перезапуск ждёт копию мира — до этого срока

// Копия мира на работающем сервере: её делает сам сервер, мы задаём имя «<профиль>-<время>.vcdbs»
// (без имени сервер назвал бы её по файлу мира — «default-…», и было бы не понять, чей это мир).
async Task RequestBackup()
{
    var now = DateTime.Now;
    var name = Backups().NameFor(now);
    pendingBackup = name;
    pendingSince = now;
    scheduler.MarkDone(now, players.Players.Count); // чтобы следующий тик расписания не запустил копию повторно
    await host.SendCommandAsync("/genbackup " + name);
}

// Сам перезапуск по расписанию: последнее слово игрокам — и сервер уходит на перезапуск.
async Task RestartBySchedule()
{
    host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("restart.now"));
    try
    {
        await host.SendCommandAsync("/announce " + eViSTool.Core.Localization.Loc.T("restart.announceNow"));
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException)
    {
        // не дошло объявление — перезапуску это не мешает
    }
    _ = automation.RestartUpdateMods ? RestartWithModUpdatesAsync() : host.RestartAsync();
}

// Перезапуск с обновлением модов: остановить → поставить вышедшие обновления (сервер не держит файлы) → запустить.
// Модбаза недоступна или что-то не встало — сервер всё равно поднимается, итог — в консоли.
async Task RestartWithModUpdatesAsync()
{
    await host.StopAsync();
    host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("autoupd.checking"));
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var db = new eViSTool.Core.ModDb.ModDbClient();
        var result = await mods.UpdateAllAsync(automation.UpdatePolicy, automation.UpdateAllowUnstable,
            new eViSTool.Core.Mods.ModUpdater(db), db, cts.Token);
        foreach (var line in result.Updated)
            host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("autoupd.updated", line));
        foreach (var line in result.Skipped)
            host.Console.Add(ConsoleLineKind.System, line);
        foreach (var line in result.Failed)
            host.Console.Add(ConsoleLineKind.Error, eViSTool.Core.Localization.Loc.T("autoupd.failed", line));
        if (result.Updated.Count + result.Skipped.Count + result.Failed.Count == 0)
            host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("autoupd.none"));
        if (result.Updated.Count + result.Failed.Count > 0)
            // значок — у заголовка, сами моды — простым списком (значки на каждой строке рябили бы)
            Notify(eViSTool.Core.Notifications.NotifyEvent.ModsUpdated,
                eViSTool.Core.Localization.Loc.T("notify.ev.modsUpdated", result.Updated.Count),
                [.. result.Updated.Select(l => new eViSTool.Core.Notifications.NotifyLine("• " + l)),
                    .. result.Failed.Select(l => new eViSTool.Core.Notifications.NotifyLine("✗ " + eViSTool.Core.Localization.Loc.T("autoupd.failed", l)))]);
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or TaskCanceledException
                                   or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
    {
        host.Console.Add(ConsoleLineKind.Error, eViSTool.Core.Localization.Loc.T("autoupd.error", ex.Message));
    }
    await host.StartAsync();
}

host.Console.LineAdded += line =>
{
    // ответ на /help — список команд для подсказок в консоли окна
    if (line.Kind == ConsoleLineKind.Output && ServerCommands.TryParseHelpLine(line.Text, out var command))
        lock (commands)
            if (!commands.TryGetValue(command.Name, out var known) || known != command)
            {
                commands[command.Name] = command;
                commandsDirty = true;
            }
};

host.Console.LineAdded += line =>
{
    // сервер взялся за копию (по расписанию, по кнопке или по команде из консоли). Конец копии узнаём по файлу,
    // а не по строке «Backup complete!»: её сервер пишет на своём языке, и на русском сервере она не находилась
    if (line.Kind != ConsoleLineKind.Output || !BackupStore.IsBackupCommand(line.Text, out var requested)) return;
    var backups = Backups();
    var before = backups.List().Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    _ = Task.Run(async () =>
    {
        var file = await backups.WaitForAsync(requested, before, BackupWaitOf());
        if (requested is not null && requested == pendingBackup) pendingBackup = null;
        if (file is null)
        {
            host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("backup.notFound", requested ?? "?"));
            Notify(eViSTool.Core.Notifications.NotifyEvent.BackupFailed, eViSTool.Core.Localization.Loc.T("notify.ev.backupFailed"),
                Line("▣", "notify.lbl.backup", eViSTool.Core.Localization.Loc.T("notify.ev.backupMissing", requested ?? "?")));
            return;
        }
        scheduler.MarkDone(DateTime.Now, players.Players.Count);
        var beforeRestart = restartAfterBackup is not null; // этой копии ждал перезапуск по расписанию
        restartAfterBackup = null;
        var settings = automation;
        try
        {
            {
                var size = eViSTool.Core.Localization.SizeText.Format(file.Size);
                host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("backup.created", file.Name, size));
                // мир сохранил сервер; данные модов рядом с миром (Saves/XLeveling, ModData) упаковываем сами — сразу после
                if (backups.PackModData(file) is > 0 and var modSize)
                    host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("backup.modData", eViSTool.Core.Localization.SizeText.Format(modSize)));
                // своя папка для копий: перенести туда (и то, что застряло раньше); недоступна — копия остаётся на сервере
                if (backups.IsElsewhere)
                    try
                    {
                        file = await Task.Run(() => backups.Relocate(file));
                        host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("backupdir.moved", backups.Dir));
                        if (await Task.Run(backups.RelocateLeftovers) is > 0 and var leftovers)
                            host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("backupdir.leftovers", leftovers));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        var why = ex is UnauthorizedAccessException ? eViSTool.Core.Localization.Loc.T("backupdir.denied") : ex.Message;
                        host.Console.Add(ConsoleLineKind.Error, eViSTool.Core.Localization.Loc.T("backupdir.moveFailed", why));
                        Notify(eViSTool.Core.Notifications.NotifyEvent.BackupFailed, eViSTool.Core.Localization.Loc.T("notify.ev.backupStuck"),
                            Line("▣", "notify.lbl.backup", file.Name),
                            Line("⚑", "notify.lbl.reason", eViSTool.Core.Localization.Loc.T("notify.ev.backupStuckWhy", backups.Dir, why)));
                    }
                // игрокам — в чат: что копия есть, как называется и сколько весит
                if (settings.BackupAnnounce && host.State == ServerState.Running)
                    await host.SendCommandAsync("/announce " + eViSTool.Core.Localization.Loc.T("backup.announce", file.Name, size, file.Time.ToString("dd.MM.yyyy HH:mm")));
            }

            // ротация — при копиях по расписанию; копии перед перезапуском тоже не должны копиться без конца
            if ((settings.BackupEnabled || beforeRestart) && settings.BackupKeep > 0)
            {
                var removed = backups.Prune(settings.BackupKeep);
                if (removed.Count > 0) host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("backup.pruned", removed.Count, settings.BackupKeep));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            host.Console.Add(ConsoleLineKind.System, ex.Message);
        }
        if (beforeRestart && host.State == ServerState.Running) await RestartBySchedule();
    });
};

// большой мир сервер копирует минутами — ждём файл с запасом
static TimeSpan BackupWaitOf() => TimeSpan.FromMinutes(30);

var shutdown = new CancellationTokenSource();

// Linux: SIGTERM (kill, systemctl stop) и Ctrl+C в терминале — штатный выход, как /shutdown: сервер останавливается
// с сохранением мира (finally в конце), файл адреса убирается. Без этого .NET завершил бы агента сразу, бросив сервер
var signals = new List<PosixSignalRegistration>();
if (!OperatingSystem.IsWindows())
    foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
        signals.Add(PosixSignalRegistration.Create(signal, ctx =>
        {
            ctx.Cancel = true;
            shutdown.Cancel();
        }));

// ---- обновление eViSTool на этом компьютере по просьбе окна с другого
string? selfUpdate = null, selfUpdateError = null;

async Task SelfUpdateAsync(eViSTool.Core.Versioning.ModVersion target)
{
    var appDir = AppContext.BaseDirectory;
    try
    {
        host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("selfupd.started", version, target));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var updater = new eViSTool.Core.AppUpdate.AppUpdater(http);
        var release = await updater.FindReleaseAsync(target)
                      ?? throw new InvalidOperationException(eViSTool.Core.Localization.Loc.T("selfupd.noRelease", target));
        // архив с проверкой отпечатка — тем же путём, что и самообновление окна
        var zip = await updater.DownloadAsync(release, Path.Combine(Path.GetTempPath(), "eViSTool-update"));

        // работающий сервер держит консоль агента — останавливаем (мир сохранится) и потом запускаем снова
        var wasRunning = host.State != ServerState.Stopped;
        if (wasRunning)
        {
            selfUpdate = "stop";
            try { await host.SendCommandAsync("/announce " + eViSTool.Core.Localization.Loc.T("selfupd.announce")); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
            await Task.Delay(TimeSpan.FromSeconds(3));
            await host.StopAsync();
        }

        selfUpdate = "install";
        eViSTool.Core.AppUpdate.AppUpdater.Install(zip, appDir);

        // новая копия агента — с теми же параметрами; она дождётся, пока эта выйдет и освободит профиль (и порт удалённого доступа)
        selfUpdate = "restart";
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(appDir, AgentProtocol.ExeName))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = appDir,
        };
        foreach (var a in eViSTool.Core.AppUpdate.AppUpdater.RelaunchArgs(Environment.GetCommandLineArgs().Skip(1), wasRunning))
            psi.ArgumentList.Add(a);
        using (System.Diagnostics.Process.Start(psi)) { }
        host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("selfupd.restarting", target));
        await Task.Delay(TimeSpan.FromSeconds(1)); // окно успеет увидеть «перезапуск»
        shutdown.Cancel();
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or UnauthorizedAccessException
                                   or TaskCanceledException or System.ComponentModel.Win32Exception or InvalidDataException)
    {
        selfUpdate = "failed";
        selfUpdateError = ex.Message;
        host.Console.Add(ConsoleLineKind.Error, eViSTool.Core.Localization.Loc.T("selfupd.failed", ex.Message));
    }
}
var lastActivity = DateTime.Now;

// удалённый доступ: второй вход — из сети, по TLS и со своим ключом; включается и выключается файлом настроек
WebApplication? remoteApp = null;
string? remoteError = null;
var remote = new RemoteSettings();
var remoteFile = RemoteAccess.FileFor(opts.ProfileId, opts.AgentsDir);
DateTime? remoteStamp = null;
var failures = new Dictionary<string, (int Count, DateTime BlockedUntil)>();

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseKestrel(k =>
{
    k.Limits.MaxRequestBodySize = 1L << 30; // архив мода бывает и сотни мегабайт
    k.Listen(System.Net.IPAddress.Loopback, 0); // свободный порт выберет система
});
var app = builder.Build();

// ключ — на каждом запросе. При включённом удалённом доступе запросы окна с этой машины держат агента живым:
// окно открыто — к серверу можно подключиться снаружи. Доступ выключен — агент, как и прежде, уходит после простоя.
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Headers.TryGetValue(AgentProtocol.KeyHeader, out var got) || got != key)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    if (remote.Enabled) lastActivity = DateTime.Now;
    await next();
});

var gameVersion = GameInstall.DetectVersion(Path.GetDirectoryName(Path.GetFullPath(opts.ExePath)) ?? "")?.ToString();

AgentStatus Status() => new()
{
    LastCrash = lastCrash,
    SelfUpdate = selfUpdate,
    SelfUpdateError = selfUpdateError,
    ModErrors = modErrors,
    GameVersion = gameVersion,
    State = host.State,
    ServerPid = host.Pid,
    StartedAt = host.StartedAt,
    MemoryMb = host.MemoryMb,
    LastExitCode = host.LastExitCode,
    RestartScheduledAt = host.RestartScheduledAt,
    LastSeq = host.Console.LastSeq,
    AgentPid = Environment.ProcessId,
    AgentVersion = version,
    Players = players.Players,
    LastBackupAt = scheduler.LastBackupAt,
    NextBackupAt = scheduler.NextAt(automation, host.State, host.StartedAt),
    NextRestartAt = RestartScheduler.NextAt(automation, host.State, host.StartedAt),
    RemotePort = remoteApp is not null ? remote.Port : null,
    AutomationChangedAt = File.Exists(automationFile) ? File.GetLastWriteTimeUtc(automationFile) : null,
    ConfigChangedAt = File.Exists(files.ConfigPath) ? File.GetLastWriteTimeUtc(files.ConfigPath) : null,
    ModsChangedAt = mods.ChangedAt(),
    PlayersChangedAt = playerLists.ChangedAt(),
    AnnouncementsChangedAt = ServerAnnouncements.ChangedAt(opts.ProfileId, opts.AgentsDir),
    NotifyChangedAt = eViSTool.Core.Notifications.ServerNotifySettings.ChangedAt(opts.ProfileId, opts.AgentsDir),
    GamePort = eViSTool.Core.Game.PlayTargets.GamePortOf(opts.DataPath),
    CommandCount = commands.Count,
    RemoteError = remoteError,
};

IResult Json(object value) => Results.Text(JsonConvert.SerializeObject(value), "application/json");

// Ошибка для окна — problem+json с понятным текстом в «detail». Пишем сами: Results.Problem сериализует через
// System.Text.Json, а в обрезанном релизном агенте рефлексия для него выключена — вместо 409 с текстом уходил пустой 500,
// и окно показывало «500 Internal Server Error» (так было и в релизах для Windows)
static IResult Problem(string detail, int status = StatusCodes.Status409Conflict) => Results.Text(
    new Newtonsoft.Json.Linq.JObject
        {
            ["title"] = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status), ["status"] = status, ["detail"] = detail,
        }
        .ToString(Formatting.None), "application/problem+json", System.Text.Encoding.UTF8, status);

async Task<IResult> Run(Func<Task> action)
{
    lastActivity = DateTime.Now;
    try
    {
        await action();
        return Json(Status());
    }
    catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IOException or System.ComponentModel.Win32Exception)
    {
        return Problem(ex.Message);
    }
}

// ответ или «409» с понятным текстом: копии, восстановление, настройки
IResult Guard(Func<object> action)
{
    lastActivity = DateTime.Now;
    try
    {
        return Json(action());
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException
                                   or InvalidDataException)
    {
        return Problem(ex.Message);
    }
}

// файл мира трогаем только у полностью остановленного сервера
IResult WhenStopped(Func<object> action) =>
    host.State != ServerState.Stopped
        ? Problem(eViSTool.Core.Localization.Loc.T("sched.needStopped"))
        : Guard(action);

async Task<T?> ReadBody<T>(HttpContext ctx) where T : class
{
    using var reader = new StreamReader(ctx.Request.Body);
    try
    {
        return JsonConvert.DeserializeObject<T>(await reader.ReadToEndAsync());
    }
    catch (JsonException)
    {
        return null;
    }
}

async Task<string?> ReadName(HttpContext ctx)
{
    using var reader = new StreamReader(ctx.Request.Body);
    return JsonConvert.DeserializeObject<BackupNameRequest>(await reader.ReadToEndAsync())?.Name;
}

// Точки API — одни и те же для окна на этой машине и для удалённого клиента (кроме выключения агента).
void MapApi(WebApplication web, bool isRemote)
{
web.MapGet("/status", () => Json(Status()));

web.MapGet("/console", async (long since, int? wait, HttpContext ctx) =>
{
    var lines = await host.Console.WaitSinceAsync(since, TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 30)), ct: ctx.RequestAborted);
    return Json(lines);
});

web.MapPost("/start", () => Run(host.StartAsync));
// остановка может идти минуты (большой мир сохраняется) — отвечаем сразу, окно следит за статусом
web.MapPost("/stop", () => Run(() => { _ = host.StopAsync(); return Task.CompletedTask; }));
web.MapPost("/restart", () => Run(() => { _ = host.RestartAsync(); return Task.CompletedTask; }));
web.MapPost("/kill", () => Run(() => { host.Kill(); return Task.CompletedTask; }));
web.MapPost("/command", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var req = JsonConvert.DeserializeObject<CommandRequest>(await reader.ReadToEndAsync());
    if (string.IsNullOrWhiteSpace(req?.Text)) return Results.BadRequest();
    return await Run(() => host.SendCommandAsync(req.Text.Trim()));
});
// копия мира сейчас (кнопка в окне); на остановленном сервере копию делает само окно
web.MapPost("/backup", () => Run(RequestBackup));

// расписание и резервные копии — нужны окну на другой машине (своё окно работает с файлами напрямую)
web.MapGet("/automation", () => Json(files.LoadAutomation()));
web.MapPut("/automation", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var settings = JsonConvert.DeserializeObject<ServerAutomation>(await reader.ReadToEndAsync());
    return settings is null ? Results.BadRequest() : Guard(() => { files.SaveAutomation(settings); return Status(); });
});
// оповещения: окну на другой машине — без секретов; от него — с секретами (внутри TLS), шифруем их здесь
web.MapGet("/notify", () => Json(eViSTool.Core.Notifications.ServerNotifySettings.Load(opts.ProfileId, opts.AgentsDir).WithoutSecrets()));
web.MapPut("/notify", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var upload = JsonConvert.DeserializeObject<eViSTool.Core.Notifications.ServerNotifyUpload>(await reader.ReadToEndAsync());
    return upload is null ? Results.BadRequest() : Guard(() => { upload.ToSettings().Save(opts.ProfileId, opts.AgentsDir); return Status(); });
});
web.MapPost("/notify/test", async () => Json(await notifier.TestAsync(
    eViSTool.Core.Localization.Loc.T("notify.testTitle"), Line("◇", "notify.lbl.from", eViSTool.Core.Localization.Loc.T("notify.testFromServer", Environment.MachineName)))));
web.MapGet("/announcements", () => Json(ServerAnnouncements.Load(opts.ProfileId, opts.AgentsDir)));
web.MapPut("/announcements", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var settings = JsonConvert.DeserializeObject<ServerAnnouncements>(await reader.ReadToEndAsync());
    return settings is null ? Results.BadRequest() : Guard(() => { settings.Save(opts.ProfileId, opts.AgentsDir); return Status(); });
});
web.MapGet("/backups", () => Guard(files.ListBackups));
web.MapPost("/backups/copy", () => WhenStopped(() => files.CopyWorld(DateTime.Now)));
web.MapPost("/backups/restore", async (HttpContext ctx) =>
    await ReadName(ctx) is { Length: > 0 } name
        ? WhenStopped(() =>
        {
            // мир возвращают вручную — сервер, упавший перед этим, не должен подняться сам (сторож ждал паузу)
            host.CancelPendingRestart();
            return files.Restore(name, DateTime.Now);
        })
        : Results.BadRequest());
// моды сервера — для окна на другой машине: список, включение/выключение, удаление, установка присланного архива
web.MapGet("/mods", () => Guard(mods.List));
web.MapGet("/commands", () => { lock (commands) return Json(commands.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList()); });
web.MapPost("/mods/toggle", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var request = JsonConvert.DeserializeObject<ModToggleRequest>(await reader.ReadToEndAsync());
    return request is null ? Results.BadRequest() : Guard(() => { mods.SetEnabled(request.Path, request.Enabled); return Status(); });
});
web.MapPost("/mods/delete", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var request = JsonConvert.DeserializeObject<ModPathRequest>(await reader.ReadToEndAsync());
    return request is null ? Results.BadRequest() : Guard(() => { mods.Delete(request.Path); return Status(); });
});
// игроки: списки — всегда; правка файлов — только у остановленного (работающий держит списки в памяти и перезапишет их)
web.MapGet("/players", () => Guard(() => playerLists.Read()));
web.MapPost("/players/edit", async (HttpContext ctx) =>
    await ReadBody<PlayerFileEdit>(ctx) is { } edit ? WhenStopped(() => { playerLists.Apply(edit); return Status(); }) : Results.BadRequest());
// настройки модов сервера: список, чтение, запись (с проверкой, не поменяли ли файл), шаг назад, сброс
web.MapGet("/modconfig", () => Guard(modConfigs.List));
web.MapPost("/modconfig/read", async (HttpContext ctx) =>
    await ReadBody<ModConfigPathRequest>(ctx) is { } r ? Guard(() => modConfigs.Read(r.Path)) : Results.BadRequest());
web.MapPost("/modconfig/save", async (HttpContext ctx) =>
    await ReadBody<ModConfigSaveRequest>(ctx) is { } r ? Guard(() => modConfigs.Save(r)) : Results.BadRequest());
web.MapPost("/modconfig/undo", async (HttpContext ctx) =>
    await ReadBody<ModConfigPathRequest>(ctx) is { } r ? Guard(() => modConfigs.Undo(r.Path)) : Results.BadRequest());
web.MapPost("/modconfig/reset", async (HttpContext ctx) =>
    await ReadBody<ModConfigPathRequest>(ctx) is { } r ? Guard(() => modConfigs.Reset(r.Path)) : Results.BadRequest());
web.MapPost("/mods/install", async (HttpContext ctx) =>
{
    // имя файла — только имя, без пути: мод ляжет в папку модов под ним
    var name = Path.GetFileName(Uri.UnescapeDataString(ctx.Request.Headers[AgentClient.ModFileHeader].ToString()));
    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.Length <= 4) return Results.BadRequest();
    var dir = Path.Combine(Path.GetTempPath(), "evistool-upload-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, name);
        await using (var file = File.Create(zip))
            await ctx.Request.Body.CopyToAsync(file, ctx.RequestAborted);
        return Guard(() => mods.Install(zip));
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
});

// serverconfig.json — для окна на другой машине; писать можно только в остановленный сервер (иначе он перезапишет файл)
web.MapGet("/config", () => Guard(files.ReadConfig));
web.MapPut("/config", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var request = JsonConvert.DeserializeObject<ConfigSaveRequest>(await reader.ReadToEndAsync());
    return request is null ? Results.BadRequest() : WhenStopped(() => files.WriteConfig(request));
});
web.MapPost("/config/generate", async () =>
{
    if (host.State != ServerState.Stopped)
        return Problem(eViSTool.Core.Localization.Loc.T("sched.needStopped"));
    try
    {
        await eViSTool.Core.Server.Config.ServerConfigGenerator.GenerateAsync(opts.ExePath, opts.DataPath);
        return Json(files.ReadConfig());
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    {
        return Problem(ex.Message);
    }
});
web.MapGet("/stats", (HttpContext ctx) =>
    Enum.TryParse<StatsPeriod>(ctx.Request.Query["period"].ToString(), out var period)
        ? Guard(() => files.Stats.Report(period, DateTime.Now)) : Results.BadRequest());
web.MapPost("/stats/enabled", async (HttpContext ctx) =>
    await ReadBody<StatsToggle>(ctx) is { } toggle ? Guard(() => { files.Stats.SetEnabled(toggle.Enabled); return Status(); }) : Results.BadRequest());
web.MapPost("/stats/clear", () => Guard(() => { files.Stats.Clear(); return Status(); }));
web.MapPost("/backups/check-dir", async (HttpContext ctx) =>
    Json(new BackupDirCheck(await Task.Run(async () => BackupStore.CheckDir(await ReadName(ctx))))));
web.MapPost("/backups/delete", async (HttpContext ctx) =>
    await ReadName(ctx) is { Length: > 0 } name ? Guard(() => { files.DeleteBackup(name); return Status(); }) : Results.BadRequest());
// обновить eViSTool на этом компьютере до версии окна — кнопка «Обновить там» у окна на ДРУГОМ компьютере,
// поэтому точка есть и на удалённом входе (до 0.9.1 её там не было, и кнопка отвечала «не умеет обновляться»)
web.MapPost("/self-update", async (HttpContext ctx) =>
{
    if (await ReadBody<SelfUpdateRequest>(ctx) is not { } request
        || !eViSTool.Core.Versioning.ModVersion.TryParse(request.Version, out var target)) return Results.BadRequest();
    if (request.Version == version) return Problem(eViSTool.Core.Localization.Loc.T("selfupd.same"), StatusCodes.Status400BadRequest);
    if (selfUpdate is not (null or "failed")) return Problem(eViSTool.Core.Localization.Loc.T("selfupd.busy"));
    selfUpdate = "download";
    selfUpdateError = null;
    _ = Task.Run(() => SelfUpdateAsync(target));
    return Json(Status());
});
if (isRemote) return;

web.MapPost("/shutdown", () =>
{
    shutdown.Cancel(); // сервер остановим при выходе (finally ниже)
    return Json(Status());
});
}

MapApi(app, isRemote: false);

// Удалённый вход: поднять, пересоздать или погасить по файлу настроек (окно пишет его, агент подхватывает сам).
async Task ApplyRemoteAsync()
{
    var stamp = File.Exists(remoteFile) ? File.GetLastWriteTimeUtc(remoteFile) : default;
    if (stamp == remoteStamp) return;
    remoteStamp = stamp;

    var wasPort = remoteApp is not null ? remote.Port : (int?)null;
    remote = RemoteAccess.Load(opts.ProfileId, opts.AgentsDir);
    if (remoteApp is not null)
    {
        await remoteApp.StopAsync();
        await remoteApp.DisposeAsync();
        remoteApp = null;
    }
    remoteError = null;
    if (!remote.Enabled || remote.Port <= 0 || remote.Key.Length < 32)
    {
        // выключили — видно и в консоли: снаружи больше не подключиться
        if (wasPort is { } closed) host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("remote.closed", closed));
        return;
    }

    try
    {
        var cert = RemoteAccess.EnsureCertificate(opts.ProfileId, opts.AgentsDir);
        var remoteKey = System.Text.Encoding.UTF8.GetBytes(remote.Key);
        var rb = WebApplication.CreateSlimBuilder();
        rb.Logging.ClearProviders();
        rb.WebHost.UseKestrelHttpsConfiguration();
        rb.WebHost.UseKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 1L << 30; // архив мода бывает и сотни мегабайт
            k.Listen(System.Net.IPAddress.Any, remote.Port, o => o.UseHttps(cert));
        });
        var web = rb.Build();
        web.Use(async (ctx, next) =>
        {
            // неверный ключ несколько раз подряд — адрес ждёт минуту: подбирать ключ бессмысленно, но и шуметь незачем
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            lock (failures)
            {
                if (failures.TryGetValue(ip, out var f) && f.BlockedUntil > DateTime.Now)
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }
            }
            var got = ctx.Request.Headers.TryGetValue(AgentProtocol.KeyHeader, out var header) ? System.Text.Encoding.UTF8.GetBytes(header.ToString()) : [];
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(got, remoteKey))
            {
                lock (failures)
                {
                    var f = failures.GetValueOrDefault(ip);
                    failures[ip] = f.Count + 1 >= 5 ? (0, DateTime.Now.AddMinutes(1)) : (f.Count + 1, default);
                }
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            lock (failures) failures.Remove(ip);
            await next();
        });
        MapApi(web, isRemote: true);
        await web.StartAsync();
        remoteApp = web;
        host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("remote.listening", remote.Port));
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException
                                   or System.Net.Sockets.SocketException)
    {
        remoteError = ex.Message;
        host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("remote.failed", remote.Port, ex.Message));
    }
}

await app.StartAsync();
var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

var stateFile = AgentProtocol.StateFile(opts.ProfileId, opts.AgentsDir);
Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
File.WriteAllText(stateFile, JsonConvert.SerializeObject(new AgentEndpoint(Environment.ProcessId, port, DateTime.Now, version)));
Console.WriteLine($"eViSTool.Agent {version}: profile {opts.ProfileId}, http://127.0.0.1:{port}");
if (opts.AfterUpdate)
{
    eViSTool.Core.AppUpdate.AppUpdater.CleanupOld(AppContext.BaseDirectory); // прежние exe и dll, переименованные при установке
    host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("selfupd.done", version));
}

await ApplyRemoteAsync();

if (opts.StartServer)
{
    try { await host.StartAsync(); }
    catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        host.Console.Add(ConsoleLineKind.System, ex.Message);
    }
}

// Агент не висит без дела: сервер остановлен, перезапуск не ждём, и 2 минуты ничего не происходит — выходим.
host.StateChanged += _ => lastActivity = DateTime.Now;
try
{
    while (!shutdown.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), shutdown.Token).ContinueWith(_ => { });

        // статистика: замер раз в минуту, старые дни — раз в сутки
        if (DateTime.Now >= nextSample)
        {
            var at = DateTime.Now;
            nextSample = at.AddMinutes(1);
            var cpu = cpuMeter.Next(host.State == ServerState.Running ? host.ProcessorTime : null, at);
            if (host.State == ServerState.Running && cpu is { } load && host.MemoryMb is { } memory)
                Stat(new StatsEntry(at, StatsKind.Sample, players.Players.Count, memory, Math.Round(load, 1)));
        }
        if (DateTime.Now >= nextStatsPrune)
        {
            nextStatsPrune = DateTime.Now.AddHours(24);
            try { stats.Prune(DateTime.Now); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        // мало места на диске с данными сервера — раз в 10 минут
        if (DateTime.Now >= nextDiskCheck)
        {
            nextDiskCheck = DateTime.Now.AddMinutes(10);
            if (LowDiskWatch.FreeBytes(opts.DataPath) is { } free && lowDisk.Check(free))
                Notify(eViSTool.Core.Notifications.NotifyEvent.LowDisk, eViSTool.Core.Localization.Loc.T("notify.ev.lowDisk"),
                    Line("▣", "notify.lbl.disk", eViSTool.Core.Localization.Loc.T("notify.ev.diskFree",
                        Path.GetPathRoot(Path.GetFullPath(opts.DataPath)) ?? opts.DataPath, eViSTool.Core.Localization.SizeText.Format(free))));
        }

        // вышли обновления модов — раз в сутки, и только если это оповещение кому-то включено
        if (DateTime.Now >= nextModCheck)
        {
            nextModCheck = DateTime.Now.AddHours(24);
            _ = Task.Run(CheckModUpdatesAsync);
        }

        if (DateTime.Now >= nextErrorScan)
        {
            nextErrorScan = DateTime.Now.AddSeconds(30);
            try { ScanModErrors(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }

        // настройки расписания поменяли в окне — подхватываем
        var stamp = File.Exists(automationFile) ? File.GetLastWriteTimeUtc(automationFile) : default;
        if (stamp != automationStamp)
        {
            automationStamp = stamp;
            automation = ServerAutomation.Load(opts.ProfileId, opts.AgentsDir);
        }

        // объявления по расписанию: правки подхватываем, в срок — /announce
        var announcementsNow = ServerAnnouncements.ChangedAt(opts.ProfileId, opts.AgentsDir);
        if (announcementsNow != announcementsStamp)
        {
            announcementsStamp = announcementsNow;
            announcements = ServerAnnouncements.Load(opts.ProfileId, opts.AgentsDir);
        }
        if (announcer.Tick(announcements, DateTime.Now, host.State, host.StartedAt, players.Players.Count) is { } say)
        {
            try { await host.SendCommandAsync("/announce " + say); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { host.Console.Add(ConsoleLineKind.System, ex.Message); }
        }

        // перезапуск по расписанию: сначала предупреждения игрокам, в срок — сам перезапуск
        if (restarts.Tick(automation, DateTime.Now, host.State, host.StartedAt) is { } step)
        {
            try
            {
                if (!step.Restart)
                {
                    host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("restart.warn", step.MinutesLeft));
                    // оповещение — одно на перезапуск, по первому предупреждению (дальше игроков предупреждают каждую минуту)
                    if (RestartScheduler.NextAt(automation, host.State, host.StartedAt) is { } restartAt && restartAt != restartNotified)
                    {
                        restartNotified = restartAt;
                        Notify(eViSTool.Core.Notifications.NotifyEvent.RestartSoon,
                            eViSTool.Core.Localization.Loc.T("notify.ev.restartSoon", step.MinutesLeft),
                            Line("◷", "notify.lbl.time", restartAt.ToString("HH:mm")),
                            Line("◦", "notify.lbl.online", players.Players.Count.ToString()));
                    }
                    await host.SendCommandAsync("/announce " + eViSTool.Core.Localization.Loc.Plural("restart.announce", step.MinutesLeft));
                }
                else if (!automation.RestartBackup)
                {
                    await RestartBySchedule();
                }
                else if (!scheduler.NeedsCopy(automation))
                {
                    host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("restart.backupSkipped"));
                    await RestartBySchedule();
                }
                else
                {
                    // сначала копия мира; перезапуск — когда сервер её закончит (файл копии готов — см. выше)
                    restartAfterBackup = DateTime.Now + ServerAutomation.RestartBackupWait;
                    host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("restart.backupFirst"));
                    await host.SendCommandAsync("/announce " + eViSTool.Core.Localization.Loc.T("restart.announceBackup"));
                    // копия уже идёт — ждём её; отметка старше получаса — сервер за копию так и не взялся, просим заново
                    if (pendingBackup is null || DateTime.Now - pendingSince > BackupWaitOf()) await RequestBackup();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                host.Console.Add(ConsoleLineKind.System, ex.Message);
                if (restartAfterBackup is not null) restartAfterBackup = DateTime.Now; // копию запросить не вышло — не ждём её
            }
        }

        // перезапуск ждёт копию: сервер за это время остановили — перезапускать нечего; копия не успела — идём без неё
        if (restartAfterBackup is { } deadline)
        {
            if (host.State != ServerState.Running)
            {
                restartAfterBackup = null;
            }
            else if (DateTime.Now >= deadline)
            {
                restartAfterBackup = null;
                host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("restart.backupFailed"));
                Notify(eViSTool.Core.Notifications.NotifyEvent.BackupFailed, eViSTool.Core.Localization.Loc.T("notify.ev.backupFailed"),
                    Line("▣", "notify.lbl.backup", eViSTool.Core.Localization.Loc.T("notify.ev.backupBeforeRestart")));
                await RestartBySchedule();
            }
        }

        await ApplyRemoteAsync();
        scheduler.NotePlayers(players.Players.Count);
        if (commandsDirty)
        {
            lock (commands)
            {
                commandsDirty = false;
                try { ServerCommands.Save(commandsFile, commands.Values); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { commandsDirty = true; } // в следующий раз
            }
        }
        if (scheduler.IsDue(automation, DateTime.Now, host.State, host.StartedAt))
        {
            try
            {
                host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("backup.scheduled"));
                await RequestBackup();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                host.Console.Add(ConsoleLineKind.System, ex.Message);
            }
        }
        var idle = host.State == ServerState.Stopped && host.RestartScheduledAt is null
                   && DateTime.Now - lastActivity > opts.IdleExit;
        if (idle) break;
    }
}
finally
{
    if (host.State != ServerState.Stopped) await host.StopAsync();
    if (remoteApp is not null) await remoteApp.StopAsync();
    await app.StopAsync();
    try { File.Delete(stateFile); } catch (IOException) { }
}
GC.KeepAlive(signals); // подписки на сигналы живут, пока жив их объект
return 0;
