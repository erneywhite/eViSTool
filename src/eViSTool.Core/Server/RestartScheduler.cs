namespace eViSTool.Core.Server;

/// <summary>Что делать сейчас: предупредить игроков (осталось MinutesLeft минут) или перезапускать.</summary>
public sealed record RestartStep(bool Restart, int MinutesLeft);

/// <summary>
/// Перезапуск сервера по расписанию. Момент перезапуска считается от запуска сервера: «каждые N часов» — это
/// N часов работы; «по времени суток» — ближайшее из заданных времён, но не раньше чем через
/// <see cref="ServerAutomation.MinRestartUptime"/> после запуска (иначе сервер, поднятый в 04:59, тут же упал бы в 05:00).
/// Перед перезапуском — предупреждения в чат за заданное число минут.
/// </summary>
public sealed class RestartScheduler
{
    private DateTime? _target;
    private readonly HashSet<int> _warned = [];
    private bool _fired;

    /// <summary>Когда ближайший перезапуск (null — расписание выключено или сервер не работает).</summary>
    /// <remarks>Интервал — настоящее время, «в 03:00» — по часам на этот день (переход на летнее время учтён, <see cref="WallClock"/>).</remarks>
    public static DateTime? NextAt(ServerAutomation settings, ServerState state, DateTime? startedAt, TimeZoneInfo? zone = null) =>
        NextUtc(settings, state, startedAt, zone) is { } next && startedAt is { } started ? WallClock.FromUtc(next, started, zone) : null;

    /// <summary>То же, что <see cref="NextAt"/>, но моментом в UTC: местное время в «повторный» час неоднозначно.</summary>
    private static DateTime? NextUtc(ServerAutomation settings, ServerState state, DateTime? startedAt, TimeZoneInfo? zone)
    {
        if (state != ServerState.Running || startedAt is not { } started) return null;
        switch (settings.RestartMode)
        {
            case RestartMode.Interval:
                return WallClock.ToUtc(started, zone) + settings.RestartInterval;

            case RestartMode.Daily:
                var times = settings.RestartTimesOfDay;
                if (times.Count == 0) return null;
                var earliest = WallClock.ToUtc(started, zone) + ServerAutomation.MinRestartUptime;
                var date = WallClock.DateOf(earliest, zone);
                for (var day = 0; day <= 1; day++)
                    foreach (var time in times)
                        if (WallClock.UtcAt(date.AddDays(day), time, zone) is var candidate && candidate >= earliest)
                            return candidate;
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Вызывать регулярно. Возвращает шаг, который пора сделать, — каждый ровно один раз: предупреждение
    /// (если пропущено несколько сразу — одно, с настоящим остатком минут) или сам перезапуск.
    /// </summary>
    public RestartStep? Tick(ServerAutomation settings, DateTime now, ServerState state, DateTime? startedAt, TimeZoneInfo? zone = null)
    {
        var target = NextUtc(settings, state, startedAt, zone);
        if (target != _target)
        {
            // сервер перезапустили, остановили или поменяли расписание — счёт предупреждений заново
            _target = target;
            _warned.Clear();
            _fired = false;
        }
        if (target is not { } at || _fired) return null;
        var nowUtc = WallClock.ToUtc(now, zone);

        if (nowUtc >= at)
        {
            _fired = true;
            return new RestartStep(Restart: true, MinutesLeft: 0);
        }

        var due = settings.RestartWarnings.Where(m => nowUtc >= at.AddMinutes(-m)).ToList();
        if (due.All(_warned.Contains)) return null;
        foreach (var m in due) _warned.Add(m);
        return new RestartStep(Restart: false, MinutesLeft: (int)Math.Ceiling((at - nowUtc).TotalMinutes));
    }
}
