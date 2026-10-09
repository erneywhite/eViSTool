using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;
using eViSTool.Core.Server;

/// <summary>
/// Ключи командной строки как есть. Без окна (Linux) части нет — их даёт data/agent.json (<see cref="AgentConfig"/>,
/// пишет команда setup), а папки сервера, которых нет и там, найдёт <see cref="ServerLocator"/>.
/// </summary>
internal sealed class AgentArgs
{
    /// <summary>Профиль агента, запущенного без окна: окно даёт свой id, а здесь он один на папку eViSTool.</summary>
    public const string DefaultProfile = AgentConfig.DefaultProfile;

    public string? ProfileId { get; private set; }
    public string? ExePath { get; private set; }
    public string? DataPath { get; private set; }
    public string? GameDir { get; private set; }
    public List<string> ExtraArgs { get; } = [];
    public bool StartServer { get; private set; }

    /// <summary>--no-start: не запускать сервер, даже если в agent.json сказано запускать.</summary>
    public bool NoStart { get; private set; }
    public bool AfterUpdate { get; private set; }
    public TimeSpan? IdleExit { get; private set; }
    public string? AgentsDir { get; private set; }
    public string? Language { get; private set; }
    public string? BackupName { get; private set; }
    public bool Help { get; private set; }

    /// <summary>Первое слово без «-» — команда (setup, remote, status…, service — см. Cli/Commands.cs).</summary>
    public string? Command { get; private set; }

    /// <summary>Запуск из окна: оно передаёт --exe и --data, и data/agent.json тогда не читается.</summary>
    public bool FromWindow => ExePath is not null && DataPath is not null;

    /// <summary>data/agent.json (после <see cref="LoadConfig"/>); null — запуск из окна, файла нет или он испорчен.</summary>
    public AgentConfig? Config { get; private set; }

    /// <summary>Где лежит прочитанный agent.json (null — его нет).</summary>
    public string? ConfigFile { get; private set; }

    /// <summary>agent.json есть, но не читается: что с ним не так (null — всё в порядке).</summary>
    public string? ConfigError { get; private set; }

    /// <summary>Профиль: ключ --profile, иначе Profile из agent.json, иначе «server». Командам — его, а не <see cref="ProfileId"/>.</summary>
    public string Profile => AgentConfig.Pick(ProfileId, Config?.Profile) ?? DefaultProfile;

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
                case "--no-start": a.NoStart = true; break;
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

    /// <summary>
    /// Прочитать data/agent.json — без окна (с окном пути даёт оно). Ничего не создаёт: команду могли запустить от root,
    /// а папка data, созданная от его имени, осталась бы недоступной службе. Испорченный файл — <see cref="ConfigError"/>.
    /// </summary>
    public void LoadConfig()
    {
        if (FromWindow || AgentConfig.Find(AgentsDir) is not { } file) return;
        ConfigFile = file;
        try
        {
            Config = AgentConfig.Load(file);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ConfigError = ex is InvalidDataException ? ex.Message : Loc.T("agent.configBroken", file, ex.Message);
        }
    }
}

internal sealed record AgentOptions(string ProfileId, string ExePath, string DataPath, IReadOnlyList<string> ExtraArgs, bool StartServer, TimeSpan IdleExit, string? AgentsDir, string? Language, string? BackupName, bool AfterUpdate = false)
{
    /// <summary>Сервер нашёл сам агент (запуск без окна); null — пути дало окно.</summary>
    public ServerLocation? Located { get; init; }

    /// <summary>Папки сервера (одна или обе) взяты из этого agent.json; null — из ключей или найдены поиском.</summary>
    public string? ConfigFile { get; init; }

    /// <summary>Имя сервера для заголовков оповещений, если окно его не задало (без окна — из agent.json или serverconfig.json).</summary>
    public string? ServerName { get; init; }

    /// <summary>
    /// Ключи → параметры агента. Окно передаёт --exe и --data — как и раньше, берутся как есть. Без них (запуск без окна)
    /// каждое значение — из ключа, без ключа — из agent.json; папки, которых нет и там, ищет <see cref="ServerLocator"/>:
    /// --game или папка из --exe — где игра, --data — где данные. Не нашёл — null, а в tried — куда заглядывал.
    /// </summary>
    public static AgentOptions? From(AgentArgs a, ICollection<string> tried)
    {
        var profile = a.Profile;
        var backupName = string.IsNullOrWhiteSpace(a.BackupName) ? null : a.BackupName;
        if (a.FromWindow)
            return new AgentOptions(profile, a.ExePath!, a.DataPath!, a.ExtraArgs, a.StartServer, a.IdleExit ?? TimeSpan.FromMinutes(2),
                a.AgentsDir, a.Language, backupName, a.AfterUpdate);

        var config = a.Config;
        var gameKey = a.GameDir ?? (a.ExePath is { } exe ? Path.GetDirectoryName(Path.GetFullPath(exe)) : null);
        var game = AgentConfig.Pick(gameKey, config?.GameDir);
        var data = AgentConfig.Pick(a.DataPath, config?.DataDir);
        if (ServerLocator.Locate(game, data, AppContext.BaseDirectory, tried) is not { } found) return null;
        var fromConfig = (string.IsNullOrWhiteSpace(gameKey) && config?.GameDir is not null)
                         || (string.IsNullOrWhiteSpace(a.DataPath) && config?.DataDir is not null);
        // файл сервера — тем же помощником, что и у окна: какой файл запускать на этой системе, решает он
        var serverExe = a.ExePath ?? AgentLauncher.ServerExe(new GameProfile { Kind = ProfileKind.Server, GameDir = found.GameDir });
        // сервер вместе с агентом: --start или --no-start, без них — как в agent.json (там по умолчанию «да»). Копию после
        // обновления запускает прежняя и сама решает, нужен ли --start: сервер работал до обновления — будет и после
        var start = a.StartServer || (!a.NoStart && !a.AfterUpdate && (config?.StartServer ?? false));
        // без окна некому перезапустить агента, когда понадобится: простой его не завершает (если не попросили ключом)
        return new AgentOptions(profile, serverExe, found.DataDir, a.ExtraArgs.Count > 0 ? a.ExtraArgs : config?.ServerArgs ?? [], start,
            a.IdleExit ?? TimeSpan.MaxValue, a.AgentsDir, a.Language ?? config?.Language,
            // имя копий мира: «<имя сервера>-<время>.vcdbs» — из agent.json или serverconfig.json, иначе «world»
            backupName ?? AgentConfig.BackupNameFor(config, found.DataDir), a.AfterUpdate)
        {
            Located = found,
            ConfigFile = fromConfig ? a.ConfigFile : null,
            ServerName = AgentConfig.DisplayNameFor(config, found.DataDir),
        };
    }
}
