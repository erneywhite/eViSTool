using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

/// <summary>Ключи командной строки как есть. Без окна (Linux) части нет — папки сервера найдёт <see cref="ServerLocator"/>.</summary>
internal sealed class AgentArgs
{
    /// <summary>Профиль агента, запущенного без окна: окно даёт свой id, а здесь он один на папку eViSTool.</summary>
    public const string DefaultProfile = "server";

    public string? ProfileId { get; private set; }
    public string? ExePath { get; private set; }
    public string? DataPath { get; private set; }
    public string? GameDir { get; private set; }
    public List<string> ExtraArgs { get; } = [];
    public bool StartServer { get; private set; }
    public bool AfterUpdate { get; private set; }
    public TimeSpan? IdleExit { get; private set; }
    public string? AgentsDir { get; private set; }
    public string? Language { get; private set; }
    public string? BackupName { get; private set; }
    public bool Help { get; private set; }

    /// <summary>Первое слово без «-» — команда (setup, remote, status…, service — см. Cli/Commands.cs).</summary>
    public string? Command { get; private set; }

    public static AgentArgs Parse(string[] args)
    {
        var a = new AgentArgs { Command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null };
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--profile": a.ProfileId = Next(); break;
                case "--exe": a.ExePath = Next(); break;
                case "--data": a.DataPath = Next(); break;
                case "--game": a.GameDir = Next(); break;
                case "--arg": a.ExtraArgs.Add(Next()); break;
                case "--start": a.StartServer = true; break;
                case "--after-update": a.AfterUpdate = true; break;
                case "--idle-exit": a.IdleExit = TimeSpan.FromSeconds(int.Parse(Next())); break;
                case "--agents-dir": a.AgentsDir = Next(); break;
                case "--lang": a.Language = Next(); break;
                case "--backup-name": a.BackupName = Next(); break;
                case "--help" or "-h": a.Help = true; break;
            }
        }
        return a;
    }
}

internal sealed record AgentOptions(string ProfileId, string ExePath, string DataPath, IReadOnlyList<string> ExtraArgs, bool StartServer, TimeSpan IdleExit, string? AgentsDir, string? Language, string? BackupName, bool AfterUpdate = false)
{
    /// <summary>Сервер нашёл сам агент (запуск без окна); null — пути дало окно.</summary>
    public ServerLocation? Located { get; init; }

    /// <summary>
    /// Ключи → параметры агента. Окно передаёт --exe и --data — как и раньше, берутся как есть. Без них (запуск без окна)
    /// сервер ищет <see cref="ServerLocator"/>: --game или папка из --exe — где игра, --data — где данные. Не нашёл —
    /// null, а в tried — куда заглядывал.
    /// </summary>
    public static AgentOptions? From(AgentArgs a, ICollection<string> tried)
    {
        var profile = string.IsNullOrWhiteSpace(a.ProfileId) ? AgentArgs.DefaultProfile : a.ProfileId;
        var backupName = string.IsNullOrWhiteSpace(a.BackupName) ? null : a.BackupName;
        if (a.ExePath is not null && a.DataPath is not null)
            return new AgentOptions(profile, a.ExePath, a.DataPath, a.ExtraArgs, a.StartServer, a.IdleExit ?? TimeSpan.FromMinutes(2),
                a.AgentsDir, a.Language, backupName, a.AfterUpdate);

        var game = a.GameDir ?? (a.ExePath is { } exe ? Path.GetDirectoryName(Path.GetFullPath(exe)) : null);
        if (ServerLocator.Locate(game, a.DataPath, AppContext.BaseDirectory, tried) is not { } found) return null;
        // файл сервера — тем же помощником, что и у окна: какой файл запускать на этой системе, решает он
        var serverExe = a.ExePath ?? AgentLauncher.ServerExe(new GameProfile { Kind = ProfileKind.Server, GameDir = found.GameDir });
        // без окна некому перезапустить агента, когда понадобится: простой его не завершает (если не попросили ключом)
        return new AgentOptions(profile, serverExe, found.DataDir, a.ExtraArgs, a.StartServer, a.IdleExit ?? TimeSpan.MaxValue,
            a.AgentsDir, a.Language, backupName, a.AfterUpdate) { Located = found };
    }
}
