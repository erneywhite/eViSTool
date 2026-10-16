using eViSTool.Core.Diagnostics;
using eViSTool.Core.Server;

/// <summary>
/// «Ошибки модов» за запуск сервера: раз в полминуты — новые строки вывода в сборщик (консоль хранит только последние
/// строки, поэтому копим по ходу), сводка — в статусе (<see cref="Report"/>). Новый запуск — счёт с нуля.
/// </summary>
internal sealed class ModErrorWatch(ServerHost host, ServerMods mods)
{
    private readonly object _sync = new();
    private ModErrorCollector _collector = new();
    private DateTime? _run;
    private long _seq;
    private DateTime _nextScan = DateTime.MinValue;
    private ModFingerprints? _prints;

    public ModErrorReport? Report { get; private set; }

    /// <summary>
    /// Новый запуск — счёт ошибок с нуля, с его первой строки (сервер может упасть раньше первого обхода).
    /// Сервер остановился (как угодно) — дочитать его ошибки сразу, не дожидаясь обхода раз в полминуты.
    /// </summary>
    public void OnServerState(ServerState state)
    {
        if (state == ServerState.Starting)
        {
            lock (_sync)
                (_collector, _run, _seq, _prints) = (new ModErrorCollector(), DateTime.Now, host.SessionStartSeq, null);
        }
        else if (state == ServerState.Stopped)
        {
            _ = Task.Run(() =>
            {
                try { Scan(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            });
        }
    }

    /// <summary>Из главного цикла: обход раз в полминуты.</summary>
    public void Tick()
    {
        if (DateTime.Now < _nextScan) return;
        _nextScan = DateTime.Now.AddSeconds(30);
        try { Scan(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    // из главного цикла и при остановке сервера — по очереди
    private void Scan()
    {
        lock (_sync) ScanLocked();
    }

    private void ScanLocked()
    {
        if (_run is null) return;
        var lines = host.Console.GetSince(_seq, 100_000);
        if (lines.Count == 0) return;
        _seq = lines[^1].Seq;
        _collector.Add(lines.Where(l => l.Kind is ConsoleLineKind.Output or ConsoleLineKind.Error).Select(l => l.Text),
            final: host.State == ServerState.Stopped);
        if (_collector.Errors.Count == 0) return;
        _prints ??= ModFingerprints.Build(mods.Locals()); // моды читаем раз за запуск — zip-ов может быть сотня
        Report = ModErrorReport.Build(_collector.Errors, _prints, _run.Value);
    }
}
