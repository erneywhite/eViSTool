using eViSTool.Core.Mods;
using eViSTool.Core.Server;

/// <summary>
/// Статистика (если включена): раз в минуту — игроки, память, процессор; плюс входы, выходы, запуски и вылеты.
/// </summary>
internal sealed class AgentStats(AgentState state)
{
    private readonly StatsStore _stats = new(StatsStore.DirFor(state.Options.ProfileId, state.Options.AgentsDir));
    private readonly CpuMeter _cpuMeter = new();
    private DateTime _nextSample = DateTime.Now.AddMinutes(1);
    private DateTime _nextPrune = DateTime.Now.AddMinutes(2);

    // кто в игре по записям статистики: сбор включили (или агент перезапустился), когда игроки уже были на сервере, —
    // их вход в записи не попал, и без входа сеанс не засчитался бы. Замер раз в минуту дописывает недостающие входы
    private readonly HashSet<string> _online = new(StringComparer.Ordinal);

    public void Stat(StatsEntry entry)
    {
        // зовут из разных потоков: смена игроков, смена состояния сервера, замер в главном цикле
        lock (_online)
        try
        {
            if (!_stats.IsEnabled)
            {
                _online.Clear(); // сбор выключен — после включения входы запишутся заново
                return;
            }
            switch (entry.Kind)
            {
                case StatsKind.Join when entry.Name is { } joined:
                    if (!_online.Add(joined)) return; // уже записан замером
                    break;
                case StatsKind.Leave when entry.Name is { } left:
                    _online.Remove(left);
                    break;
                case StatsKind.Up or StatsKind.Down or StatsKind.Crash:
                    _online.Clear();
                    break;
            }
            _stats.Append(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // не записалось — статистика не повод мешать серверу
        }
    }

    /// <summary>Запуск и остановка сервера — в статистику.</summary>
    public void OnServerState(ServerState serverState)
    {
        var host = state.Host;
        if (serverState == ServerState.Running)
        {
            Stat(new StatsEntry(DateTime.Now, StatsKind.Up));
            // запуск сервера закрывает строку истории модов: всё, что поменяли до него, — одним сеансом
            ModHistory.MarkLaunch(ModHistory.FileFor(state.Options.ProfileId));
        }
        // сам остановился после запуска — упал (как и для оповещения «сервер упал»)
        else if (serverState == ServerState.Stopped)
            Stat(new StatsEntry(DateTime.Now, host.LastExitOnItsOwn && state.ReachedRunning ? StatsKind.Crash : StatsKind.Down));
    }

    /// <summary>Из главного цикла: замер раз в минуту, старые дни — раз в сутки.</summary>
    public void Tick()
    {
        var host = state.Host;
        var players = state.Players;
        if (DateTime.Now >= _nextSample)
        {
            var at = DateTime.Now;
            _nextSample = at.AddMinutes(1);
            var cpu = _cpuMeter.Next(host.State == ServerState.Running ? host.ProcessorTime : null, at);
            if (host.State == ServerState.Running && cpu is { } load && host.MemoryMb is { } memory)
            {
                foreach (var p in players.Players) Stat(new StatsEntry(at, StatsKind.Join, Name: p.Name)); // известных пропустит
                Stat(new StatsEntry(at, StatsKind.Sample, players.Players.Count, memory, Math.Round(load, 1)));
            }
        }
        if (DateTime.Now >= _nextPrune)
        {
            _nextPrune = DateTime.Now.AddHours(24);
            try { _stats.Prune(DateTime.Now); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
