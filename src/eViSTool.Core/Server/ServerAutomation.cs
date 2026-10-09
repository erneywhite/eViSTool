using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace eViSTool.Core.Server;

/// <summary>Когда перезапускать сервер по расписанию.</summary>
public enum RestartMode
{
    Off,
    /// <summary>Каждые N часов работы.</summary>
    Interval,
    /// <summary>В заданное время суток.</summary>
    Daily,
}

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

    /// <summary>Сообщать игрокам в чат, когда копия готова (имя, размер, время).</summary>
    public bool BackupAnnounce { get; init; } = true;

    /// <summary>
    /// Куда складывать копии (в том числе сетевая папка «\\nas\share»); null — как раньше, Backups в папке данных
    /// сервера. Сервер всё равно пишет копию в Backups — агент переносит её сюда, когда она готова.
    /// </summary>
    public string? BackupDir { get; init; }

    /// <summary>Интервал в допустимых пределах (не чаще раза в 5 минут).</summary>
    [JsonIgnore]
    public TimeSpan BackupInterval => TimeSpan.FromHours(Math.Clamp(BackupIntervalHours, 5.0 / 60, 24 * 30));

    // ---- перезапуски

    [JsonConverter(typeof(StringEnumConverter))]
    public RestartMode RestartMode { get; init; }

    /// <summary>Для <see cref="RestartMode.Interval"/>: через сколько часов работы перезапускать.</summary>
    public double RestartIntervalHours { get; init; } = 12;

    /// <summary>Для <see cref="RestartMode.Daily"/>: времена суток «ЧЧ:ММ».</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] // иначе при чтении остался бы список по умолчанию
    public IReadOnlyList<string> RestartTimes { get; init; } = ["05:00"];

    /// <summary>За сколько минут до перезапуска предупреждать игроков в чате: за 10, за 5, а дальше — каждую минуту.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public IReadOnlyList<int> RestartWarnMinutes { get; init; } = [10, 5, 4, 3, 2, 1];

    /// <summary>Перед перезапуском по расписанию сделать резервную копию мира: перезапуск пройдёт неудачно — копия под рукой.</summary>
    public bool RestartBackup { get; init; } = true;

    // ---- обновление модов при перезапуске

    /// <summary>
    /// При перезапуске по расписанию, пока сервер остановлен, поставить вышедшие обновления модов (под версию игры
    /// сервера). По умолчанию выключено: кто-то держит набор модов неизменным нарочно.
    /// </summary>
    public bool RestartUpdateMods { get; init; }

    /// <summary>
    /// Пожелания к версиям модов из окна (закреплённые моды, пропущенные версии) и «предлагать пре-релизы» — агент
    /// настроек окна не видит, поэтому окно кладёт их сюда, когда сохраняет расписание или меняет закрепления.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, string> UpdatePinned { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, List<string>> UpdateBlocked { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool UpdateAllowUnstable { get; init; }

    [JsonIgnore]
    public Mods.ModPolicy UpdatePolicy => new(UpdatePinned, UpdateBlocked);

    /// <summary>Сколько ждать копию перед перезапуском; не успела — перезапуск идёт без неё.</summary>
    public static readonly TimeSpan RestartBackupWait = TimeSpan.FromMinutes(10);

    /// <summary>Сервер должен проработать хотя бы столько, прежде чем его перезапустят по расписанию.</summary>
    public static readonly TimeSpan MinRestartUptime = TimeSpan.FromMinutes(5);

    [JsonIgnore]
    public TimeSpan RestartInterval => TimeSpan.FromHours(Math.Clamp(RestartIntervalHours, MinRestartUptime.TotalHours, 24 * 30));

    /// <summary>Времена суток по возрастанию; нераспознанные записи пропускаются.</summary>
    [JsonIgnore]
    public IReadOnlyList<TimeSpan> RestartTimesOfDay =>
        [.. RestartTimes.Select(t => TryTimeOfDay(t, out var time) ? time : (TimeSpan?)null).OfType<TimeSpan>().Distinct().Order()];

    /// <summary>Минуты предупреждений: от 1 до 180, без повторов, от большего к меньшему.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> RestartWarnings => [.. RestartWarnMinutes.Where(m => m is >= 1 and <= 180).Distinct().OrderDescending()];

    /// <summary>«5:00», «05:00», «17.30» → время суток.</summary>
    public static bool TryTimeOfDay(string? text, out TimeSpan time)
    {
        time = default;
        var parts = (text ?? "").Trim().Replace('.', ':').Split(':');
        if (parts.Length != 2 || parts[1].Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) || h > 23 || m > 59)
            return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

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
