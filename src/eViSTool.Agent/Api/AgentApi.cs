using System.Runtime.InteropServices;
using eViSTool.Core.Localization;
using eViSTool.Core.Mods;
using eViSTool.Core.Notifications;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

/// <summary>
/// Точки API — одни и те же для окна на этой машине и для удалённого клиента (кроме выключения агента). Маршруты
/// разложены по областям, каждая — в своём файле рядом (AgentApi.*.cs); здесь — порядок подключения и общие помощники.
/// Ответы — через Newtonsoft (<see cref="Json"/>, <see cref="Problem"/>): System.Text.Json в обрезанном релизном
/// агенте без рефлексии не работает.
/// </summary>
internal sealed partial class AgentApi(
    AgentState state,
    AgentJournal journal,
    AgentNotifications notify,
    ServerSchedule schedule,
    KnownCommands commands,
    ModErrorWatch modErrors,
    CrashWatch crash,
    AgentSelfUpdate selfUpdate,
    RemoteEntry remote)
{
    private AgentOptions Opts => state.Options;
    private ServerHost Host => state.Host;
    private ServerFiles Files => state.Files;
    private ServerMods Mods => state.Mods;
    private ServerPlayers PlayerLists => state.PlayerLists;
    private ModConfigService ModConfigs => state.ModConfigs;

    /// <summary>
    /// Подключить точки к входу. Сначала журнал (он должен видеть ответ каждой точки), за ним маршруты по областям;
    /// выключение агента — только на локальном входе. Порядок областей на выбор точки не влияет: все адреса — точные
    /// строки без параметров, двух точек на один адрес и метод нет.
    /// </summary>
    public void Map(WebApplication web, bool isRemote)
    {
        UseJournal(web, isRemote);
        MapServer(web);
        MapJournal(web);
        MapBackups(web);
        MapNotify(web);
        MapMods(web);
        MapPlayers(web);
        MapModConfig(web);
        MapConfig(web);
        MapStats(web);
        MapSelfUpdate(web);
        if (isRemote) return;

        MapShutdown(web);
    }

    /// <summary>Статус агента и сервера — ответ на /status и на любое действие.</summary>
    public AgentStatus Status() => new()
    {
        LastCrash = crash.LastCrash,
        SelfUpdate = selfUpdate.Stage,
        SelfUpdateError = selfUpdate.Error,
        ModErrors = modErrors.Report,
        GameVersion = state.GameVersion,
        State = Host.State,
        ServerPid = Host.Pid,
        StartedAt = Host.StartedAt,
        MemoryMb = Host.MemoryMb,
        LastExitCode = Host.LastExitCode,
        RestartScheduledAt = Host.RestartScheduledAt,
        LastSeq = Host.Console.LastSeq,
        AgentPid = Environment.ProcessId,
        AgentVersion = state.Version,
        Os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription,
        UtcOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
        Players = state.Players.Players,
        LastBackupAt = schedule.LastBackupAt,
        NextBackupAt = schedule.NextBackupAt(),
        NextRestartAt = RestartScheduler.NextAt(state.Automation, Host.State, Host.StartedAt),
        RemotePort = remote.Port,
        AutomationChangedAt = File.Exists(state.AutomationFile) ? File.GetLastWriteTimeUtc(state.AutomationFile) : null,
        ConfigChangedAt = File.Exists(Files.ConfigPath) ? File.GetLastWriteTimeUtc(Files.ConfigPath) : null,
        ModsChangedAt = Mods.ChangedAt(),
        PlayersChangedAt = PlayerLists.ChangedAt(),
        AnnouncementsChangedAt = ServerAnnouncements.ChangedAt(Opts.ProfileId, Opts.AgentsDir),
        NotifyChangedAt = ServerNotifySettings.ChangedAt(Opts.ProfileId, Opts.AgentsDir),
        JournalChangedAt = File.Exists(journal.FilePath) ? File.GetLastWriteTimeUtc(journal.FilePath) : null,
        GamePort = eViSTool.Core.Game.PlayTargets.GamePortOf(Opts.DataPath),
        CommandCount = commands.Count,
        RemoteError = remote.Error,
    };

    private static IResult Json(object value) => Results.Text(JsonConvert.SerializeObject(value), "application/json");

    // Ошибка для окна — problem+json с понятным текстом в «detail». Пишем сами: Results.Problem сериализует через
    // System.Text.Json, а в обрезанном релизном агенте рефлексия для него выключена — вместо 409 с текстом уходил пустой 500,
    // и окно показывало «500 Internal Server Error» (так было и в релизах для Windows)
    private static IResult Problem(string detail, int status = StatusCodes.Status409Conflict) => Results.Text(
        new Newtonsoft.Json.Linq.JObject
            {
                ["title"] = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status), ["status"] = status, ["detail"] = detail,
            }
            .ToString(Formatting.None), "application/problem+json", System.Text.Encoding.UTF8, status);

    // действие с сервером: в ответе — статус, ошибка — «409» с понятным текстом
    private async Task<IResult> Run(Func<Task> action)
    {
        state.LastActivity = DateTime.Now;
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
    private IResult Guard(Func<object> action)
    {
        state.LastActivity = DateTime.Now;
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
    private IResult WhenStopped(Func<object> action) =>
        Host.State != ServerState.Stopped
            ? Problem(Loc.T("sched.needStopped"))
            : Guard(action);

    private static async Task<T?> ReadBody<T>(HttpContext ctx) where T : class
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

    // действие окна для истории модов: его id, источник и что откатывает — изменения агента встанут в ту же пачку
    private static IDisposable HistoryOp(HttpContext ctx) => ModHistory.Join(
        ctx.Request.Headers[AgentClient.OpHeader].ToString(), ctx.Request.Headers[AgentClient.OpSourceHeader].ToString(),
        ctx.Request.Headers[AgentClient.OpUndoesHeader].ToString());

    private static async Task<string?> ReadName(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        return JsonConvert.DeserializeObject<BackupNameRequest>(await reader.ReadToEndAsync())?.Name;
    }
}
