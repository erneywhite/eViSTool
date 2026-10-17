using eViSTool.Core.Server;
using Newtonsoft.Json;

namespace eViSTool.Core.Notifications;

/// <summary>
/// Что окно помнит об оповещениях сервера у хостера между своими запусками: когда последний раз проверяло обновления
/// модов и какой список нашло — один и тот же список второй раз не приходит, а после перезапуска окна проверка не
/// начинается заново раньше срока. Небольшой файл рядом с настройками оповещений профиля.
/// </summary>
public sealed record HostedNotifyState
{
    /// <summary>Последняя удачная проверка обновлений модов, UTC; не проверяли — null.</summary>
    public DateTime? ModsCheckedAt { get; init; }

    /// <summary>Что нашла эта проверка: «Название: 1.2.0 → 1.3.0».</summary>
    public List<string> ModUpdates { get; init; } = [];

    public static string FileFor(string profileId, string? agentsDir = null) =>
        Path.Combine(agentsDir ?? AgentProtocol.DefaultAgentsDir, $"{profileId}.hosted-notify.json");

    /// <summary>Файла нет или он испорчен — пустое состояние (проверка пройдёт как первая).</summary>
    public static HostedNotifyState Load(string profileId, string? agentsDir = null)
    {
        try
        {
            var file = FileFor(profileId, agentsDir);
            return File.Exists(file) ? JsonConvert.DeserializeObject<HostedNotifyState>(File.ReadAllText(file)) ?? new() : new();
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
        Platform.SafeFile.Replace(tmp, file);
    }
}
