using System.Diagnostics;
using System.Text;
using eViSTool.Core.Localization;

namespace eViSTool.Core.Server;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

public sealed record ServerHostOptions
{
    public required string ExePath { get; init; }
    public required string DataPath { get; init; }
    /// <summary>Дополнительные аргументы (например, --addModPath для мира).</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>Сколько ждать после /stop (большой мир сохраняется долго — ViSST ждал 20 с, и в этом была беда).</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Сколько ждать после Ctrl+C, прежде чем убивать.</summary>
    public TimeSpan CtrlCTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Сторож: поднимать сервер, если он упал сам.</summary>
    public bool Watchdog { get; init; } = true;
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Больше стольких падений за час — сторож сдаётся (иначе бесконечный цикл падений).</summary>
    public int MaxRestartsPerHour { get; init; } = 3;

    /// <summary>Кодировка вывода сервера. null — определить по системе (консоль Windows с кириллицей — OEM 866).</summary>
    public Encoding? OutputEncoding { get; init; }
}

/// <summary>Отправка Ctrl+C процессу (мягкая остановка VS). Реализация — в агенте (нужен отдельный процесс).</summary>
public interface ICtrlCSender
{
    bool SendCtrlC(int pid);
}

/// <summary>
/// Владеет процессом VintagestoryServer: запуск, консоль, корректная остановка, сторож.
/// Остановка ждёт реального завершения процесса, а не фиксированное время:
/// /stop → ждём → Ctrl+C → ждём → только потом kill.
/// </summary>
public sealed class ServerHost : IAsyncDisposable
{
    private const string ReadyMarker = "now running on Port";      // «Выделенный сервер now running on Port …» — начало переводится
    private const string StoppingMarker = "Server stop requested";

    private readonly ServerHostOptions _options;
    private readonly ICtrlCSender? _ctrlC;
    private readonly object _lock = new();
    private readonly List<DateTime> _crashes = [];

    private Process? _process;
    private StreamWriter? _stdin;
    private bool _stopRequested;
    private TaskCompletionSource? _exited;
    private CancellationTokenSource? _restartCts;
    private WorldLock? _world; // мир занят сервером — от запуска до выхода процесса

    public ServerConsole Console { get; } = new();
    public ServerState State { get; private set; } = ServerState.Stopped;
    public DateTime? StartedAt { get; private set; }
    public int? Pid => _process is { HasExited: false } p ? p.Id : null;
    public int? LastExitCode { get; private set; }

    /// <summary>Когда сторож перезапустит сервер (если ждёт).</summary>
    public DateTime? RestartScheduledAt { get; private set; }

    public event Action<ServerState>? StateChanged;

    public ServerHost(ServerHostOptions options, ICtrlCSender? ctrlC = null)
    {
        _options = options;
        _ctrlC = ctrlC;
    }

    public long? MemoryMb
    {
        get
        {
            try
            {
                if (_process is not { HasExited: false } p) return null;
                p.Refresh();
                return p.WorkingSet64 / 1024 / 1024;
            }
            catch (InvalidOperationException) { return null; }
        }
    }

    private void SetState(ServerState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Sys(string text) => Console.Add(ConsoleLineKind.System, text);

    public Task StartAsync()
    {
        lock (_lock)
        {
            if (State is ServerState.Starting or ServerState.Running or ServerState.Stopping) return Task.CompletedTask;

            // мир занят (идёт восстановление, копия) — не стартуем на меняющихся файлах; бронь — до выхода процесса.
            // До отмены ожидающего перезапуска: сторож, упёршийся в занятый мир, должен продолжить ждать
            var world = WorldLock.TryTake(_options.DataPath, WorldLock.Server)
                        ?? throw new WorldBusyException(WorldLock.BusyMessage(_options.DataPath));
            try
            {
                CancelScheduledRestart();
                if (!File.Exists(_options.ExePath))
                    throw new FileNotFoundException(Loc.T("srv.exeNotFound", _options.ExePath), _options.ExePath);
                // восстановление мира оборвалось посреди подмены — сервер не должен стартовать на половине старого и нового
                if (WorldRestore.Recover(_options.DataPath)) Sys(Loc.T("backup.recovered"));
            }
            catch
            {
                world.Dispose();
                throw;
            }
            _world = world;

            // есть своя консоль (агент) — делим её с сервером в UTF-8; нет — отдельная скрытая консоль в OEM-кодировке
            var shareConsole = ConsoleInterop.TryUseUtf8();
            if (shareConsole) ConsoleInterop.EnableCtrlCForChildren();
            var encoding = _options.OutputEncoding ?? (shareConsole ? new UTF8Encoding(false) : DetectConsoleEncoding());
            var psi = new ProcessStartInfo(_options.ExePath)
            {
                WorkingDirectory = Path.GetDirectoryName(_options.ExePath)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,   // и читаем его тоже: непрочитанный stderr рано или поздно вешает сервер
                CreateNoWindow = !shareConsole,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding,
                StandardInputEncoding = encoding,
            };
            psi.ArgumentList.Add("--dataPath");
            psi.ArgumentList.Add(_options.DataPath);
            foreach (var a in _options.ExtraArgs) psi.ArgumentList.Add(a);

            _stopRequested = false;
            _exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Exited += (_, _) => OnExited(process);

            SetState(ServerState.Starting);
            Sys(Loc.T("srv.starting"));
            bool started;
            try
            {
                started = process.Start();
            }
            catch
            {
                FailStart(process);
                throw; // исходная причина (нет файла, не exe, нет доступа) — тому, кто запускал
            }
            if (!started)
            {
                FailStart(process);
                throw new InvalidOperationException(Loc.T("srv.startFailed"));
            }

            _process = process;
            _stdin = process.StandardInput;
            _stdin.AutoFlush = true;
            StartedAt = DateTime.Now;
            _ = PumpAsync(process.StandardOutput, ConsoleLineKind.Output);
            _ = PumpAsync(process.StandardError, ConsoleLineKind.Error);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Процесс не запустился: всё как до попытки — «остановлен», мир свободен, объект процесса освобождён. Следующий
    /// запуск (исправили exe, дали доступ) проходит без перезапуска агента.
    /// </summary>
    private void FailStart(Process process)
    {
        process.Dispose();
        _exited?.TrySetResult();
        _exited = null;
        ReleaseWorld();
        SetState(ServerState.Stopped);
    }

    private async Task PumpAsync(StreamReader reader, ConsoleLineKind kind)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Console.Add(kind, line);
                if (kind != ConsoleLineKind.Output) continue;
                if (State == ServerState.Starting && line.Contains(ReadyMarker, StringComparison.Ordinal))
                    SetState(ServerState.Running);
                else if (line.Contains(StoppingMarker, StringComparison.Ordinal) && State == ServerState.Running)
                    SetState(ServerState.Stopping); // остановили изнутри (игрок-админ набрал /stop)
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // процесс завершился — поток закрыт
        }
    }

    private void OnExited(Process process)
    {
        int code;
        try { code = process.ExitCode; } catch (InvalidOperationException) { code = -1; }

        bool unexpected;
        lock (_lock)
        {
            if (!ReferenceEquals(process, _process)) return;
            LastExitCode = code;
            // остановка «изнутри» (/stop от админа в игре) — тоже штатная
            unexpected = !_stopRequested && State != ServerState.Stopping;
            _stdin = null;
            _process = null;
            StartedAt = null;
            ReleaseWorld(); // до «остановлен»: кто увидит это состояние, сразу может занять мир
            SetState(ServerState.Stopped);
            _exited?.TrySetResult();
        }

        if (!unexpected)
        {
            Sys(Loc.T("srv.stopped", code));
            return;
        }

        Sys(Loc.T("srv.crashed", code));
        if (_options.Watchdog) ScheduleRestart();
    }

    private void ReleaseWorld()
    {
        _world?.Dispose();
        _world = null;
    }

    /// <summary>
    /// Сторож: поднять сервер через паузу. Мир в это время занят (идёт восстановление) — не стартуем, а ждём:
    /// пробуем снова через ту же паузу, пока не освободится или ожидание не отменят.
    /// </summary>
    private void ScheduleRestart(bool afterCrash = true)
    {
        lock (_lock)
        {
            if (afterCrash)
            {
                _crashes.RemoveAll(t => DateTime.Now - t > TimeSpan.FromHours(1));
                _crashes.Add(DateTime.Now);
                if (_crashes.Count > _options.MaxRestartsPerHour)
                {
                    Sys(Loc.T("srv.watchdogGaveUp", _options.MaxRestartsPerHour));
                    return;
                }
                Sys(Loc.T("srv.watchdogRestart", (int)_options.RestartDelay.TotalSeconds));
            }

            _restartCts = new CancellationTokenSource();
            var ct = _restartCts.Token;
            RestartScheduledAt = DateTime.Now + _options.RestartDelay;
            _ = Task.Delay(_options.RestartDelay, ct).ContinueWith(async t =>
            {
                if (t.IsCanceled) return;
                RestartScheduledAt = null;
                try { await StartAsync().ConfigureAwait(false); }
                catch (WorldBusyException ex)
                {
                    if (afterCrash) Sys(Loc.T("srv.watchdogWaiting", ex.Message)); // один раз за ожидание, без спама
                    if (!ct.IsCancellationRequested) ScheduleRestart(afterCrash: false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Sys(Loc.T("srv.watchdogFailed", ex.Message));
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Отменить ожидающий перезапуск сторожа (восстановление мира: сервер не должен подняться сам).</summary>
    public void CancelPendingRestart()
    {
        lock (_lock)
        {
            if (_restartCts is null) return;
            CancelScheduledRestart();
            Sys(Loc.T("srv.restartCanceled"));
        }
    }

    private void CancelScheduledRestart()
    {
        _restartCts?.Cancel();
        _restartCts = null;
        RestartScheduledAt = null;
    }

    /// <summary>Команда в консоль сервера (как если бы её набрали в окне сервера).</summary>
    public async Task SendCommandAsync(string command)
    {
        var stdin = _stdin;
        if (stdin is null || State is ServerState.Stopped) throw new InvalidOperationException(Loc.T("srv.notRunning"));
        Console.Add(ConsoleLineKind.Input, command);
        await stdin.WriteLineAsync(command).ConfigureAwait(false);
    }

    /// <summary>Корректная остановка: /stop, ждём реального завершения; не вышел — Ctrl+C; не помогло — kill.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        Task exited;
        int pid;
        lock (_lock)
        {
            CancelScheduledRestart();
            if (_process is null || _exited is null) return;
            _stopRequested = true;
            exited = _exited.Task;
            pid = _process.Id;
            SetState(ServerState.Stopping);
        }

        Sys(Loc.T("srv.stopping"));
        try { await SendCommandAsync("/stop").ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { }

        if (await WaitAsync(exited, _options.StopTimeout, ct).ConfigureAwait(false)) return;

        if (_ctrlC is not null)
        {
            Sys(Loc.T("srv.stopTimeoutCtrlC", (int)_options.StopTimeout.TotalSeconds));
            _ctrlC.SendCtrlC(pid);
            if (await WaitAsync(exited, _options.CtrlCTimeout, ct).ConfigureAwait(false)) return;
        }

        Sys(Loc.T("srv.killing"));
        Kill();
        await WaitAsync(exited, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
    }

    /// <summary>Убить сразу. Несохранённое с последнего автосохранения пропадёт.</summary>
    public void Kill()
    {
        Process? p;
        lock (_lock)
        {
            CancelScheduledRestart();
            _stopRequested = true;
            p = _process;
        }
        try { p?.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct).ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    private static async Task<bool> WaitAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        return done == task;
    }

    /// <summary>
    /// Сервер без окна получает скрытую консоль с кодовой страницей OEM (на русской Windows — 866),
    /// и .NET внутри него пишет вывод в ней. Читаем в той же кодировке, иначе кириллица превратится в кракозябры.
    /// </summary>
    public static Encoding DetectConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var oem = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            return Encoding.GetEncoding(oem);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State != ServerState.Stopped) await StopAsync().ConfigureAwait(false);
    }
}
