namespace eViSTool.Core.Server;

/// <summary>Общее для агента и окна: где агент пишет свой адрес, как называются заголовки и ответы.</summary>
public static class AgentProtocol
{
    public const string KeyHeader = "X-eViSTool-Key";
    public const string ExeName = "eViSTool.Agent.exe";

    public static string DefaultAgentsDir => Path.Combine(AppPaths.Root, "agents");

    /// <summary>Версия этой сборки eViSTool: окно и агент выпускаются вместе и должны совпадать.</summary>
    public static string AppVersion { get; } =
        typeof(AgentProtocol).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>
    /// Агент прежней версии: eViSTool обновили, а он работает со старым файлом (обновление его не трогает, чтобы не
    /// уронить сервер). Новых функций он не знает.
    /// </summary>
    public static bool IsOutdated(AgentStatus status) => status.AgentVersion.Length > 0 && status.AgentVersion != AppVersion;

    /// <summary>Файл с адресом работающего агента профиля.</summary>
    public static string StateFile(string profileId, string? dir = null) => Path.Combine(dir ?? DefaultAgentsDir, $"{profileId}.json");

    /// <summary>Постоянный ключ профиля (на этапе 5 — ключ удалённого подключения).</summary>
    public static string KeyFile(string profileId, string? dir = null) => Path.Combine(dir ?? DefaultAgentsDir, $"{profileId}.key");

    /// <summary>Ключ профиля: создаётся один раз, дальше читается.</summary>
    public static string GetOrCreateKey(string profileId, string? dir = null)
    {
        var path = KeyFile(profileId, dir);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        File.WriteAllText(path, key);
        return key;
    }
}

/// <summary>Содержимое agents/&lt;профиль&gt;.json.</summary>
public sealed record AgentEndpoint(int Pid, int Port, DateTime StartedAt, string Version);

/// <summary>Ответ GET /status.</summary>
/// <summary>
/// Сервер остановился сам (упал или выключился от ошибок) — что разобрал агент: причина, ошибка, стек и, если нашёлся,
/// мод-виновник (путь — на машине сервера: по нему окно выключает мод через агента). Id — новое падение для окна.
/// </summary>
public sealed record ServerCrashInfo
{
    public string Id { get; init; } = "";
    public DateTime At { get; init; }
    public string Reason { get; init; } = "";
    public string? Error { get; init; }
    public IReadOnlyList<string> Stack { get; init; } = [];
    public int ErrorCount { get; init; }
    public string? ModId { get; init; }
    public string? ModName { get; init; }
    public string? ModVersion { get; init; }
    public string? ModPath { get; init; }
    public Diagnostics.CulpritSource? Source { get; init; }

    public static ServerCrashInfo From(Diagnostics.CrashFinding f, DateTime at) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        At = at,
        Reason = f.Reason,
        Error = f.Error,
        Stack = [.. f.Stack.Take(40)],
        ErrorCount = f.ErrorCount,
        ModId = f.Culprit?.ModId,
        ModName = f.Culprit?.Mod?.Name ?? f.Culprit?.ModId,
        ModVersion = f.Culprit?.Version,
        ModPath = f.Culprit?.Mod?.Path,
        Source = f.Culprit?.Source,
    };
}

public sealed record AgentStatus
{
    /// <summary>Последнее падение сервера, разобранное агентом (null — не падал с запуска агента).</summary>
    public ServerCrashInfo? LastCrash { get; init; }

    /// <summary>«Ошибки модов» за текущий (или последний) запуск сервера — для «!» у профиля в окне.</summary>
    public Diagnostics.ModErrorReport? ModErrors { get; init; }

    public ServerState State { get; init; }
    public int? ServerPid { get; init; }
    public DateTime? StartedAt { get; init; }
    public long? MemoryMb { get; init; }
    public int? LastExitCode { get; init; }
    public DateTime? RestartScheduledAt { get; init; }
    public long LastSeq { get; init; }
    public int AgentPid { get; init; }
    public string AgentVersion { get; init; } = "";

    /// <summary>Версия игры сервера (по его exe) — удалённому профилю её больше неоткуда взять.</summary>
    public string? GameVersion { get; init; }

    /// <summary>Последняя резервная копия мира и когда агент сделает следующую (null — расписание выключено).</summary>
    public DateTime? LastBackupAt { get; init; }
    public DateTime? NextBackupAt { get; init; }

    /// <summary>Когда последний раз менялись моды сервера (папки модов или включение/выключение): удалённое окно перечитывает список.</summary>
    public DateTime? ModsChangedAt { get; init; }

    /// <summary>Сколько команд сервера известно (из его /help) — поменялось, значит окну пора перечитать список для подсказок.</summary>
    public int CommandCount { get; init; }

    /// <summary>Когда последний раз менялся serverconfig.json (null — его нет): удалённое окно перечитывает конфиг.</summary>
    public DateTime? ConfigChangedAt { get; init; }

    /// <summary>Когда последний раз менялись настройки расписания — окна по ней замечают чужую правку и перечитывают их.</summary>
    public DateTime? AutomationChangedAt { get; init; }

    /// <summary>Когда менялись игроки и списки (Playerdata, режим белого списка): окна перечитывают вкладку «Игроки».</summary>
    public DateTime? PlayersChangedAt { get; init; }

    /// <summary>Ближайший перезапуск по расписанию (null — расписание выключено или сервер не работает).</summary>
    public DateTime? NextRestartAt { get; init; }

    /// <summary>Удалённый доступ: агент принимает подключения из сети на этом порту (null — выключен или не удалось открыть порт).</summary>
    public int? RemotePort { get; init; }

    /// <summary>Почему удалённый доступ не заработал (порт занят и т. п.).</summary>
    public string? RemoteError { get; init; }

    /// <summary>Кто сейчас на сервере (по времени входа).</summary>
    public IReadOnlyList<OnlinePlayer> Players { get; init; } = [];
}

public sealed record CommandRequest(string Text);
