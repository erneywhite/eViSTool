using eViSTool.Core.Diagnostics;
using eViSTool.Core.Localization;
using eViSTool.Core.ModDb;
using eViSTool.Core.Notifications;
using eViSTool.Core.Server;
using Newtonsoft.Json;

/// <summary>
/// Оповещения о здоровье сервера: «не успевает» (перегрузка), мало места на диске, вышли обновления модов.
/// </summary>
internal sealed class HealthWatch(AgentState state, AgentNotifications notify)
{
    // «сервер не успевает»: много «Server overloaded» за короткое время — с подсказкой, памяти ли не хватает
    private readonly OverloadWatch _overloads = new();
    private readonly LowDiskWatch _lowDisk = new();
    private DateTime _nextDiskCheck = DateTime.Now.AddMinutes(1);
    private DateTime _nextModCheck = DateTime.Now.AddMinutes(10); // не сразу при запуске: агент мог подняться ради одной команды

    // какой список обновлений уже присылали — помним между запусками агента, чтобы не повторять одно и то же
    private readonly string _modUpdatesFile = Path.Combine(state.Options.AgentsDir ?? AgentProtocol.DefaultAgentsDir,
        $"{state.Options.ProfileId}.modupdates.json");

    /// <summary>Строка консоли: считать «Server overloaded»; набралось много за короткое время — оповестить.</summary>
    public void WatchOverload(ConsoleLine line)
    {
        if (line.Kind != ConsoleLineKind.Output || _overloads.Add(line.Text, DateTime.Now) is not { } count) return;
        var details = new List<NotifyLine>
        {
            AgentNotifications.Line("◷", "notify.lbl.warnings", Loc.T("notify.ev.overloadCount", count, (int)OverloadWatch.Window.TotalMinutes)),
        };
        if (SystemMemory.Status() is { } mem)
            details.Add(SystemMemory.IsLow(mem)
                ? AgentNotifications.Line("▣", "notify.lbl.memory", Loc.T("notify.ev.memoryLow", mem.LoadPercent, mem.FreeMb))
                : AgentNotifications.Line("⚑", "notify.lbl.hint", Loc.T("notify.ev.overloadHint")));
        notify.Notify(NotifyEvent.Overloaded, Loc.T("notify.ev.overloaded"), [.. details]);
    }

    /// <summary>Из главного цикла: мало места на диске с данными сервера — раз в 10 минут.</summary>
    public void CheckDisk()
    {
        if (DateTime.Now < _nextDiskCheck) return;
        _nextDiskCheck = DateTime.Now.AddMinutes(10);
        var dataPath = state.Options.DataPath;
        if (LowDiskWatch.FreeBytes(dataPath) is { } free && _lowDisk.Check(free))
            notify.Notify(NotifyEvent.LowDisk, Loc.T("notify.ev.lowDisk"),
                AgentNotifications.Line("▣", "notify.lbl.disk", Loc.T("notify.ev.diskFree",
                    Path.GetPathRoot(Path.GetFullPath(dataPath)) ?? dataPath, SizeText.Format(free))));
    }

    /// <summary>Из главного цикла: вышли обновления модов — раз в сутки, и только если это оповещение кому-то включено.</summary>
    public void CheckModUpdates()
    {
        if (DateTime.Now < _nextModCheck) return;
        _nextModCheck = DateTime.Now.AddHours(24);
        _ = Task.Run(CheckModUpdatesAsync);
    }

    private async Task CheckModUpdatesAsync()
    {
        var settings = ServerNotifySettings.Load(state.Options.ProfileId, state.Options.AgentsDir);
        if (!settings.ChannelsFor(NotifyEvent.ModUpdates).Any()) return;
        try
        {
            using var db = new ModDbClient();
            var list = await state.Mods.AvailableUpdatesAsync(state.Automation.UpdatePolicy, state.Automation.UpdateAllowUnstable, db);
            var seen = File.Exists(_modUpdatesFile) ? JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(_modUpdatesFile)) ?? [] : [];
            if (list.Count > 0 && !list.SequenceEqual(seen))
                notify.Notify(NotifyEvent.ModUpdates, Loc.T("notify.ev.modUpdates", list.Count),
                    [.. list.Take(30).Select(l => new NotifyLine("• " + l)),
                     .. list.Count > 30 ? [new NotifyLine(Loc.T("notify.ev.andMore", list.Count - 30))] : Array.Empty<NotifyLine>(),
                     AgentNotifications.Line("⚑", "notify.lbl.hint", Loc.T(state.Automation.RestartUpdateMods ? "notify.ev.modUpdatesAuto" : "notify.ev.modUpdatesHint"))]);
            File.WriteAllText(_modUpdatesFile, JsonConvert.SerializeObject(list));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException or JsonException)
        {
            // модбаза недоступна — попробуем завтра; это не повод тревожить
        }
    }
}
