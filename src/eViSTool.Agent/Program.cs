using System.Reflection;
using System.Runtime.InteropServices;
using eViSTool.Core.Server;
using Newtonsoft.Json;

// eViSTool.Agent — держит процесс сервера VS и принимает команды по HTTP.
// Окно eViSTool можно закрыть или уронить: сервер продолжит работать, сторож — сторожить.
//
//   eViSTool.Agent.exe --profile <id> --exe <VintagestoryServer.exe> --data <папка данных> [--arg <доп. аргумент>]…
//   eViSTool.Agent [--game <папка игры>] [--data <папка данных>] [--profile <id>] [--start|--no-start]   — без окна (Linux)
//   eViSTool.Agent <команда> …   — setup, remote, status, start, stop, restart, command, service (см. Cli/Commands.cs)
//
// Без окна пути не передаёт никто: каждое значение — из ключа, без ключа — из data/agent.json (его пишет setup), а папки
// сервера, которых нет и там, ищет ServerLocator (рядом, в соседних папках, по server.sh); профиль по умолчанию — «server».
// Слушает только 127.0.0.1, каждый запрос — с ключом профиля. Адрес пишет в data/agents/<id>.json.
//
// Здесь — разбор параметров и сборка частей; сами части — в соседних файлах: AgentState (общее состояние), Watch/*
// (подписки на консоль и состояние сервера), ServerSchedule (копии, перезапуски, объявления), AgentSelfUpdate, AgentJournal,
// LocalEntry и RemoteEntry (входы), Api/AgentApi*.cs (точки API по областям), AgentLoop (главный цикл).

var cli = AgentArgs.Parse(args);
cli.LoadConfig(); // data/agent.json — только без окна
// язык сообщений: окно передаёт свой, без окна — из agent.json или язык системы (нет такого словаря — английский)
eViSTool.Core.Localization.Loc.Instance.SetLanguage(cli.Language ?? cli.Config?.Language
    ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
// запустили двойным щелчком из Проводника: это не то, что человек хотел — объяснить и дождаться клавиши,
// а не мигнуть консолью (без окна агент искал бы сервер рядом и тут же закрылся бы с ошибкой)
if (args.Length == 0 && DoubleClick.Detected())
{
    // новое окно консоли открывается в кодировке системы по умолчанию (часто без кириллицы) — иначе вместо букв «?»;
    // консоль своя, только для агента, так что это больше ни на что не влияет
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine(eViSTool.Core.Localization.Loc.T("agent.doubleClick"));
    Console.WriteLine();
    Console.WriteLine(eViSTool.Core.Localization.Loc.T("agent.pressKey"));
    try { Console.ReadKey(intercept: true); }
    catch (InvalidOperationException) { } // ввод перенаправлен — просто выйти
    return 0;
}

// команды (setup, remote, status…, service) — выполнить и выйти; без команды агент работает дальше
if (Commands.Run(cli, args) is { } exitCode) return exitCode;

// agent.json испорчен: сказать, где ошибка, и выйти, иначе агент молча взял бы другие папки
if (cli.ConfigError is { } configError)
{
    Console.Error.WriteLine(configError);
    return 2;
}
var tried = new List<string>();
if (AgentOptions.From(cli, tried) is not { } opts)
{
    Console.Error.WriteLine(eViSTool.Core.Localization.Loc.T("agent.notFound", string.Join(", ", tried)));
    Console.Error.WriteLine(cli.Config is { GameDir: not null } or { DataDir: not null }
        ? eViSTool.Core.Localization.Loc.T("agent.notFoundConfig", cli.ConfigFile ?? "")
        : eViSTool.Core.Localization.Loc.T("agent.notFoundHint"));
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
    Console.WriteLine((located.Script ?? opts.ConfigFile) is { } source
        ? eViSTool.Core.Localization.Loc.T("agent.foundScript", located.GameDir, located.DataDir, source)
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

// части работающего агента — каждая в своём файле: общее состояние, оповещения, статистика, расписание, самообновление,
// журнал, входы (локальный и удалённый) и точки API на них, главный цикл
var state = new AgentState(opts, version, host);
using var notify = new AgentNotifications(opts, host);
var health = new HealthWatch(state, notify);
var stats = new AgentStats(state);
var playerEvents = new PlayerEvents(state, stats, notify);
var modErrors = new ModErrorWatch(host, state.Mods);
var crash = new CrashWatch(state, notify);
var commands = new KnownCommands(opts);
var schedule = new ServerSchedule(state, notify);
var selfUpdate = new AgentSelfUpdate(state);
var journal = new AgentJournal(opts);
var remote = new RemoteEntry(state, journal);
var api = new AgentApi(state, journal, notify, schedule, commands, modErrors, crash, selfUpdate, remote);
remote.Serve(api); // удалённый вход отвечает теми же точками, что и локальный

// Подписки на сервер. Порядок важен: обработчики одного события зовутся по очереди подписки (кто на сервере — раньше
// статистики и оповещений, «дошёл до работы» — раньше разбора вылета), а сбой обработчика строки консоли не пускает
// строку к следующим
if (!cli.FromWindow) host.Console.LineAdded += ConsoleMirror.Write;
host.Console.LineAdded += state.TrackPlayers;
host.StateChanged += state.ResetPlayers;
host.StateChanged += state.TrackRunning;
host.StateChanged += notify.OnServerState;
host.Console.LineAdded += health.WatchOverload;
host.Console.LineAdded += notify.RelayChat;
host.StateChanged += stats.OnServerState;
state.Players.Changed += playerEvents.OnPlayersChanged;
host.StateChanged += modErrors.OnServerState;
host.StateChanged += crash.OnServerState;
host.Console.LineAdded += commands.Collect;
host.Console.LineAdded += schedule.WatchBackup;

// Linux: SIGTERM (kill, systemctl stop) и Ctrl+C в терминале — штатный выход, как /shutdown: сервер останавливается
// с сохранением мира (finally в конце), файл адреса убирается. Без этого .NET завершил бы агента сразу, бросив сервер
var signals = new List<PosixSignalRegistration>();
if (!OperatingSystem.IsWindows())
    foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
        signals.Add(PosixSignalRegistration.Create(signal, ctx =>
        {
            ctx.Cancel = true;
            state.Shutdown.Cancel();
        }));

var local = new LocalEntry(state, key, remote, api);
var port = await local.StartAsync();

var stateFile = AgentProtocol.StateFile(opts.ProfileId, opts.AgentsDir);
Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
File.WriteAllText(stateFile, JsonConvert.SerializeObject(new AgentEndpoint(Environment.ProcessId, port, DateTime.Now, version)));
Console.WriteLine($"eViSTool.Agent {version}: profile {opts.ProfileId}, http://127.0.0.1:{port}");
if (opts.AfterUpdate)
{
    eViSTool.Core.AppUpdate.AppUpdater.CleanupOld(AppContext.BaseDirectory); // прежние exe и dll, переименованные при установке
    host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("selfupd.done", version));
}

await remote.ApplyAsync();

// служба после самообновления: в юните --start, но остановленный до обновления сервер остаётся остановленным
var startServer = opts.StartServer;
if (AgentProtocol.TakeAfterUpdate(opts.ProfileId, opts.AgentsDir) is { } wasRunning)
{
    eViSTool.Core.AppUpdate.AppUpdater.CleanupOld(AppContext.BaseDirectory);
    host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("selfupd.done", version));
    startServer = wasRunning;
}

if (startServer)
{
    try { await host.StartAsync(); }
    catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        host.Console.Add(ConsoleLineKind.System, ex.Message);
    }
}

// Агент не висит без дела: сервер остановлен, перезапуск не ждём, и 2 минуты ничего не происходит — выходим.
host.StateChanged += _ => state.LastActivity = DateTime.Now;
try
{
    await new AgentLoop(state, stats, health, modErrors, schedule, remote, commands).RunAsync();
}
finally
{
    if (host.State != ServerState.Stopped) await host.StopAsync();
    await remote.StopAsync();
    await local.StopAsync();
    try { File.Delete(stateFile); } catch (IOException) { }
}
GC.KeepAlive(signals); // подписки на сигналы живут, пока жив их объект
return state.ExitCode;
