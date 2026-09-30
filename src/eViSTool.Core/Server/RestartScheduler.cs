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
    public static DateTime? NextAt(ServerAutomation settings, ServerState state, DateTime? startedAt)
    {
        if (state != ServerState.Running || startedAt is not { } started) return null;
        switch (settings.RestartMode)
        {
            case RestartMode.Interval:
                return started + settings.RestartInterval;

            case RestartMode.Daily:
                var times = settings.RestartTimesOfDay;
                if (times.Count == 0) return null;
                var earliest = started + ServerAutomation.MinRestartUptime;
                for (var day = 0; day <= 1; day++)
                    foreach (var time in times)
                        if (earliest.Date.AddDays(day) + time is var candidate && candidate >= earliest)
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
    public RestartStep? Tick(ServerAutomation settings, DateTime now, ServerState state, DateTime? startedAt)
    {
        var target = NextAt(settings, state, startedAt);
        if (target != _target)
        {
            // сервер перезапустили, остановили или поменяли расписание — счёт предупреждений заново
            _target = target;
            _warned.Clear();
            _fired = false;
        }
        if (target is not { } at || _fired) return null;

        if (now >= at)
        {
            _fired = true;
            return new RestartStep(Restart: true, MinutesLeft: 0);
        }

        var due = settings.RestartWarnings.Where(m => now >= at.AddMinutes(-m)).ToList();
        if (due.All(_warned.Contains)) return null;
        foreach (var m in due) _warned.Add(m);
        return new RestartStep(Restart: false, MinutesLeft: (int)Math.Ceiling((at - now).TotalMinutes));
    }
}
