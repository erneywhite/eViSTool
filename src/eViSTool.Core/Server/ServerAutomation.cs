using Newtonsoft.Json;

namespace eViSTool.Core.Server;

/// <summary>
/// Что агент делает с сервером сам, по расписанию. Лежит рядом с ключом агента (agents/&lt;профиль&gt;.automation.json):
/// окно пишет файл, агент перечитывает его, когда тот изменился, — так настройки действуют и при закрытом окне.
/// </summary>
public sealed record ServerAutomation
{
    /// <summary>Делать резервные копии мира по расписанию (пока сервер работает).</summary>
    public bool BackupEnabled { get; init; }

    /// <summary>Раз во сколько часов.</summary>
    public double BackupIntervalHours { get; init; } = 1;

    /// <summary>Сколько последних копий хранить; старые удаляются. 0 — не удалять.</summary>
    public int BackupKeep { get; init; } = 7;

    /// <summary>Пропускать копию, если с прошлой никто не заходил: мир не менялся, копия была бы той же самой.</summary>
    public bool BackupOnlyWhenPlayed { get; init; } = true;

    /// <summary>Интервал в допустимых пределах (не чаще раза в 5 минут).</summary>
    [JsonIgnore]
    public TimeSpan BackupInterval => TimeSpan.FromHours(Math.Clamp(BackupIntervalHours, 5.0 / 60, 24 * 30));

    public static string FileFor(string profileId, string? agentsDir = null) =>
        Path.Combine(agentsDir ?? AgentProtocol.DefaultAgentsDir, $"{profileId}.automation.json");

    /// <summary>Настройки профиля; файла нет или он испорчен — значения по умолчанию (всё выключено).</summary>
    public static ServerAutomation Load(string profileId, string? agentsDir = null)
    {
        try
        {
            var file = FileFor(profileId, agentsDir);
            return File.Exists(file) ? JsonConvert.DeserializeObject<ServerAutomation>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string profileId, string? agentsDir = null)
    {
        var file = FileFor(profileId, agentsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
        File.Move(tmp, file, overwrite: true);
    }
}
