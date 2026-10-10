using Newtonsoft.Json;

namespace eViSTool.Core.Server;

/// <summary>
/// Сообщение в чат по расписанию: текст и раз в сколько минут — или, если заданы <see cref="Times"/>, в эти часы
/// каждый день («12:00» — «плановые работы»). Время — по часам машины с сервером (окно пересчитывает на свои).
/// </summary>
public sealed record Announcement(string Text, int IntervalMinutes = 30, bool Enabled = true, IReadOnlyList<string>? Times = null)
{
    public const int MinInterval = 1;
    public const int MaxInterval = 24 * 60;

    [JsonIgnore] public TimeSpan Interval => TimeSpan.FromMinutes(Math.Clamp(IntervalMinutes, MinInterval, MaxInterval));
    [JsonIgnore] public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Text);

    /// <summary>Говорится в заданные часы, а не через интервал.</summary>
    [JsonIgnore] public bool IsTimed => Times is { Count: > 0 };
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

    /// <summary>
    /// Объявление на заданный час опоздало больше чем на столько (сервер не работал, мешало другое) — уже не говорим:
    /// «плановые работы через 15 минут», сказанные через час, только запутают.
    /// </summary>
    public static readonly TimeSpan TimedWindow = TimeSpan.FromMinutes(5);

    // объявления на заданный час: когда какое время уже сказано (по тексту и времени суток)
    private readonly Dictionary<(string, TimeSpan), DateTime> _timedSaid = [];

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

        // на заданный час — раньше интервальных: у них нет запаса, а интервальные подождут минуту
        if (TimedDue(settings, now, playersOnline) is { } timed) return timed;

        var active = settings.Items.Where(a => a.IsActive && !a.IsTimed).ToList();
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

    /// <summary>Объявление на заданный час, которое пора сказать: час наступил, не опоздали и сегодня его ещё не было.</summary>
    private string? TimedDue(ServerAnnouncements settings, DateTime now, int playersOnline)
    {
        if (now - _lastAny < Gap) return null;
        foreach (var a in settings.Items.Where(a => a.IsActive && a.IsTimed))
            foreach (var text in a.Times!)
            {
                if (!ServerAutomation.TryTimeOfDay(text, out var time)) continue;
                var at = now.Date + time;
                if (now < at || now - at > TimedWindow) continue;
                var key = (a.Text.Trim(), time);
                if (_timedSaid.TryGetValue(key, out var said) && said >= at) continue;
                _timedSaid[key] = now; // и когда никого нет: пустому серверу не говорим, но и позже не догоняем
                if (settings.OnlyWithPlayers && playersOnline == 0) continue;
                _lastAny = now;
                return a.Text.Trim();
            }
        return null;
    }
}
