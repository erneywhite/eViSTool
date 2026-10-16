using eViSTool.Core;
using eViSTool.Core.Game;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

/// <summary>
/// Общее состояние работающего агента: то, что читают и меняют сразу несколько частей — маршруты API, главный цикл,
/// подписки на сервер. Хост сервера, кто на нём, его файлы и моды, расписание (окно правит его на ходу), отметки
/// активности и выхода.
/// </summary>
internal sealed class AgentState
{
    private DateTime _automationStamp;

    public AgentState(AgentOptions options, string version, ServerHost host)
    {
        Options = options;
        Version = version;
        Host = host;
        // резервные копии по расписанию: копию делает сам сервер (/genbackup), агент решает когда и убирает старые
        Automation = ServerAutomation.Load(options.ProfileId, options.AgentsDir);
        AutomationFile = ServerAutomation.FileFor(options.ProfileId, options.AgentsDir);
        _automationStamp = File.Exists(AutomationFile) ? File.GetLastWriteTimeUtc(AutomationFile) : default;
        Files = new ServerFiles(options.ProfileId, options.DataPath, options.BackupName, options.AgentsDir); // для окна на другой машине
        Mods = new ServerMods(options.ProfileId, Path.GetDirectoryName(Path.GetFullPath(options.ExePath)) ?? "", options.DataPath);
        PlayerLists = new ServerPlayers(options.DataPath); // вкладка «Игроки» окна на другой машине
        // настройки модов (ModConfig) — для окна на другой машине; прежние версии хранятся здесь, рядом с файлами
        ModConfigs = new ModConfigService(options.DataPath,
            new ModConfigBackups(Path.Combine(AppPaths.ModConfigBackups, options.ProfileId)), Mods.Locals);
        GameVersion = GameInstall.DetectVersion(Path.GetDirectoryName(Path.GetFullPath(options.ExePath)) ?? "")?.ToString();
    }

    public AgentOptions Options { get; }

    /// <summary>Версия агента (без хвоста «+коммит»).</summary>
    public string Version { get; }

    public ServerHost Host { get; }

    /// <summary>Кто на сервере — по строкам консоли (<see cref="TrackPlayers"/>, <see cref="ResetPlayers"/>).</summary>
    public PlayerTracker Players { get; } = new();

    /// <summary>Расписание копий, перезапусков и обновлений модов; правки из окна подхватывает <see cref="ReloadAutomation"/>.</summary>
    public ServerAutomation Automation { get; private set; }

    public string AutomationFile { get; }
    public ServerFiles Files { get; }
    public ServerMods Mods { get; }
    public ServerPlayers PlayerLists { get; }
    public ModConfigService ModConfigs { get; }
    public string? GameVersion { get; }

    /// <summary>
    /// Текущий запуск сервера дошёл до работы — чтобы отличить падение при запуске от падения в работе
    /// (оповещение «сервер упал» и запись вылета в статистике).
    /// </summary>
    public bool ReachedRunning { get; private set; }

    /// <summary>Когда агент последний раз был нужен: по нему агент уходит после простоя.</summary>
    public DateTime LastActivity { get; set; } = DateTime.Now;

    /// <summary>Просьба агенту выйти: /shutdown, сигнал на Linux, самообновление.</summary>
    public CancellationTokenSource Shutdown { get; } = new();

    /// <summary>Код выхода агента: 75 — обновился под systemd, служба поднимет новую версию.</summary>
    public int ExitCode { get; set; }

    /// <summary>Копии мира. Папка для копий берётся из расписания — её могут поменять, пока агент работает.</summary>
    public BackupStore Backups() => new(Options.DataPath, Options.BackupName, Automation.BackupDir);

    /// <summary>Настройки расписания поменяли в окне — подхватить (из главного цикла).</summary>
    public void ReloadAutomation()
    {
        var stamp = File.Exists(AutomationFile) ? File.GetLastWriteTimeUtc(AutomationFile) : default;
        if (stamp != _automationStamp)
        {
            _automationStamp = stamp;
            Automation = ServerAutomation.Load(Options.ProfileId, Options.AgentsDir);
        }
    }

    /// <summary>Строка консоли → кто на сервере.</summary>
    public void TrackPlayers(ConsoleLine line)
    {
        if (line.Kind == ConsoleLineKind.Output) Players.Process(line.Text, line.Time);
    }

    /// <summary>Сервер остановился или запускается заново — никого.</summary>
    public void ResetPlayers(ServerState state)
    {
        if (state is ServerState.Stopped or ServerState.Starting) Players.Reset();
    }

    /// <summary>Запуск начался — до работы ещё не дошёл; заработал — дошёл.</summary>
    public void TrackRunning(ServerState state)
    {
        switch (state)
        {
            case ServerState.Starting:
                ReachedRunning = false;
                break;
            case ServerState.Running:
                ReachedRunning = true;
                break;
        }
    }
}
