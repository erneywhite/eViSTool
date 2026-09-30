using System.Reflection;
using eViSTool.Core;
using eViSTool.Core.Server;
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
//
// Слушает только 127.0.0.1, каждый запрос — с ключом профиля. Адрес пишет в data/agents/<id>.json.

var opts = AgentOptions.Parse(args);
if (opts is null)
{
    Console.Error.WriteLine("usage: eViSTool.Agent --profile <id> --exe <server exe> --data <data dir> [--arg <extra>]...");
    return 2;
}

// один агент на профиль
using var mutex = new Mutex(initiallyOwned: true, $"eViSTool.Agent.{opts.ProfileId}", out var createdNew);
if (!createdNew)
{
    Console.Error.WriteLine("agent for this profile is already running");
    return 3;
}

eViSTool.Core.Localization.Loc.Instance.SetLanguage(opts.Language);

// Ctrl+C, который мы шлём серверу через общую консоль, самого агента ронять не должен
Console.CancelKeyPress += (_, e) => e.Cancel = true;

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
var backups = new BackupStore(opts.DataPath, opts.BackupName);
var scheduler = new BackupScheduler();
scheduler.Seed(backups.List().FirstOrDefault(b => b.IsOwn)?.Time);
string? pendingBackup = null; // имя копии, которую сервер делает по нашей просьбе

// Копия мира на работающем сервере: её делает сам сервер, мы задаём имя «<профиль>-<время>.vcdbs»
// (без имени сервер назвал бы её по файлу мира — «default-…», и было бы не понять, чей это мир).
async Task RequestBackup()
{
    var now = DateTime.Now;
    var name = backups.NameFor(now);
    pendingBackup = name;
    scheduler.MarkDone(now, players.Players.Count); // чтобы следующий тик расписания не запустил копию повторно
    await host.SendCommandAsync("/genbackup " + name);
}

host.Console.LineAdded += line =>
{
    // сервер закончил копию (по расписанию, по кнопке или по команде из консоли)
    if (line.Kind != ConsoleLineKind.Output || !line.Text.EndsWith("Backup complete!", StringComparison.Ordinal)) return;
    scheduler.MarkDone(line.Time, players.Players.Count);
    var name = pendingBackup;
    pendingBackup = null;
    var settings = automation;
    _ = Task.Run(async () =>
    {
        try
        {
            // копию просили из консоли без имени — это самый свежий файл в папке
            var file = (name is null ? null : backups.Find(name)) ?? backups.List().FirstOrDefault();
            if (file is not null)
            {
                var size = eViSTool.Core.Localization.SizeText.Format(file.Size);
                host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("backup.created", file.Name, size));
                // игрокам — в чат: что копия есть, как называется и сколько весит
                if (settings.BackupAnnounce && host.State == ServerState.Running)
                    await host.SendCommandAsync("/announce " + eViSTool.Core.Localization.Loc.T("backup.announce", file.Name, size, file.Time.ToString("dd.MM.yyyy HH:mm")));
            }

            if (!settings.BackupEnabled || settings.BackupKeep <= 0) return;
            var removed = backups.Prune(settings.BackupKeep);
            if (removed.Count > 0) host.Console.Add(ConsoleLineKind.System, eViSTool.Core.Localization.Loc.T("backup.pruned", removed.Count, settings.BackupKeep));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            host.Console.Add(ConsoleLineKind.System, ex.Message);
        }
    });
};

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0)); // свободный порт выберет система
var app = builder.Build();

// ключ — на каждом запросе
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Headers.TryGetValue(AgentProtocol.KeyHeader, out var got) || got != key)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

var shutdown = new CancellationTokenSource();
var lastActivity = DateTime.Now;

AgentStatus Status() => new()
{
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
};

IResult Json(object value) => Results.Text(JsonConvert.SerializeObject(value), "application/json");

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
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
}

app.MapGet("/status", () => Json(Status()));

app.MapGet("/console", async (long since, int? wait, HttpContext ctx) =>
{
    var lines = await host.Console.WaitSinceAsync(since, TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 30)), ct: ctx.RequestAborted);
    return Json(lines);
});

app.MapPost("/start", () => Run(host.StartAsync));
// остановка может идти минуты (большой мир сохраняется) — отвечаем сразу, окно следит за статусом
app.MapPost("/stop", () => Run(() => { _ = host.StopAsync(); return Task.CompletedTask; }));
app.MapPost("/restart", () => Run(() => { _ = host.RestartAsync(); return Task.CompletedTask; }));
app.MapPost("/kill", () => Run(() => { host.Kill(); return Task.CompletedTask; }));
app.MapPost("/command", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var req = JsonConvert.DeserializeObject<CommandRequest>(await reader.ReadToEndAsync());
    if (string.IsNullOrWhiteSpace(req?.Text)) return Results.BadRequest();
    return await Run(() => host.SendCommandAsync(req.Text.Trim()));
});
// копия мира сейчас (кнопка в окне); на остановленном сервере копию делает само окно
app.MapPost("/backup", () => Run(RequestBackup));
app.MapPost("/shutdown", () =>
{
    shutdown.Cancel(); // сервер остановим при выходе (finally ниже)
    return Json(Status());
});

await app.StartAsync();
var port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

var stateFile = AgentProtocol.StateFile(opts.ProfileId, opts.AgentsDir);
Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
File.WriteAllText(stateFile, JsonConvert.SerializeObject(new AgentEndpoint(Environment.ProcessId, port, DateTime.Now, version)));
Console.WriteLine($"eViSTool.Agent {version}: profile {opts.ProfileId}, http://127.0.0.1:{port}");

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

        // настройки расписания поменяли в окне — подхватываем
        var stamp = File.Exists(automationFile) ? File.GetLastWriteTimeUtc(automationFile) : default;
        if (stamp != automationStamp)
        {
            automationStamp = stamp;
            automation = ServerAutomation.Load(opts.ProfileId, opts.AgentsDir);
        }

        scheduler.NotePlayers(players.Players.Count);
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
    await app.StopAsync();
    try { File.Delete(stateFile); } catch (IOException) { }
}
return 0;

internal sealed record AgentOptions(string ProfileId, string ExePath, string DataPath, IReadOnlyList<string> ExtraArgs, bool StartServer, TimeSpan IdleExit, string? AgentsDir, string? Language, string? BackupName)
{
    public static AgentOptions? Parse(string[] args)
    {
        string? profile = null, exe = null, data = null;
        var extra = new List<string>();
        var start = false;
        var idle = TimeSpan.FromMinutes(2);
        string? agentsDir = null, lang = null, backupName = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--profile": profile = Next(); break;
                case "--exe": exe = Next(); break;
                case "--data": data = Next(); break;
                case "--arg": extra.Add(Next()); break;
                case "--start": start = true; break;
                case "--idle-exit": idle = TimeSpan.FromSeconds(int.Parse(Next())); break;
                case "--agents-dir": agentsDir = Next(); break;
                case "--lang": lang = Next(); break;
                case "--backup-name": backupName = Next(); break;
            }
        }
        return profile is null || exe is null || data is null ? null : new AgentOptions(profile, exe, data, extra, start, idle, agentsDir, lang, string.IsNullOrWhiteSpace(backupName) ? null : backupName);
    }
}
