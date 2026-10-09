using Newtonsoft.Json;

namespace eViSTool.Core.Server;

/// <summary>Сообщение в чат по расписанию: текст и раз в сколько минут.</summary>
public sealed record Announcement(string Text, int IntervalMinutes = 30, bool Enabled = true)
{
    public const int MinInterval = 1;
    public const int MaxInterval = 24 * 60;

    [JsonIgnore] public TimeSpan Interval => TimeSpan.FromMinutes(Math.Clamp(IntervalMinutes, MinInterval, MaxInterval));
    [JsonIgnore] public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Объявления сервера по расписанию (вкладка «Объявления»). Свой файл рядом с настройками расписания, чтобы правки
/// двух вкладок не затирали друг друга; его читает агент и сам отправляет сообщения — окно можно закрыть.
/// </summary>
public sealed record ServerAnnouncements
{
    /// <summary>Только если на сервере кто-то есть: пустому серверу говорить незачем.</summary>
    public bool OnlyWithPlayers { get; init; } = true;

    public IReadOnlyList<Announcement> Items { get; init; } = [];

    public static string FileFor(string profileId, string? agentsDir = null) =>
        Path.Combine(agentsDir ?? AgentProtocol.DefaultAgentsDir, $"{profileId}.announcements.json");

    /// <summary>Файла нет или он испорчен — пусто.</summary>
    public static ServerAnnouncements Load(string profileId, string? agentsDir = null)
    {
        try
        {
            var file = FileFor(profileId, agentsDir);
            return File.Exists(file) ? JsonConvert.DeserializeObject<ServerAnnouncements>(File.ReadAllText(file)) ?? new() : new();
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

    public static DateTime? ChangedAt(string profileId, string? agentsDir = null) =>
        File.Exists(FileFor(profileId, agentsDir)) ? File.GetLastWriteTimeUtc(FileFor(profileId, agentsDir)) : null;
}

/// <summary>
/// Когда какое объявление сказать. У каждого свой отсчёт от запуска сервера (или от минуты, когда его добавили),
/// со сдвигом на минуту на каждое следующее,
/// чтобы они не шли пачкой; два объявления не уходят в одну минуту (второе ждёт). Пока на сервере никого нет (если так
/// настроено), отсчёт идёт заново — зашедший игрок не получит пачку накопившихся сообщений.
/// </summary>
public sealed class AnnouncementScheduler
{
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(1);

    // когда объявление говорили (или начали ждать) — по тексту и интервалу: правка сообщения начинает его отсчёт заново
    private readonly Dictionary<(string, int), DateTime> _last = [];
    private DateTime? _startedAt;
    private DateTime _lastAny = DateTime.MinValue;

    /// <summary>Вызывать регулярно. Возвращает текст, который пора сказать, или null.</summary>
    public string? Tick(ServerAnnouncements settings, DateTime now, ServerState state, DateTime? startedAt, int playersOnline)
    {
        if (state != ServerState.Running || startedAt is not { } started)
        {
            _startedAt = null;
            _last.Clear();
            return null;
        }
        if (_startedAt != started)
        {
            _startedAt = started; // сервер (пере)запущен — отсчёт с начала
            _last.Clear();
        }

        var active = settings.Items.Where(a => a.IsActive).ToList();
        // объявления, которых больше нет, забываем; новое (или после запуска сервера) начинает отсчёт с этой минуты,
        // каждое следующее — на минуту позже
        foreach (var key in _last.Keys.Where(k => !active.Any(a => Key(a) == k)).ToList()) _last.Remove(key);
        for (var i = 0; i < active.Count; i++)
            if (!_last.ContainsKey(Key(active[i])))
                _last[Key(active[i])] = now + Gap * i;

        if (settings.OnlyWithPlayers && playersOnline == 0)
        {
            foreach (var a in active) _last[Key(a)] = now; // никого нет — не копим
            return null;
        }
        if (now - _lastAny < Gap) return null;

        var due = active.Where(a => now - _last[Key(a)] >= a.Interval).OrderBy(a => _last[Key(a)]).FirstOrDefault();
        if (due is null) return null;
        _last[Key(due)] = now;
        _lastAny = now;
        return due.Text.Trim();
    }

    private static (string, int) Key(Announcement a) => (a.Text.Trim(), a.IntervalMinutes);
}
