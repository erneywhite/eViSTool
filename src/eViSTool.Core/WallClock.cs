namespace eViSTool.Core;

/// <summary>
/// Время по часам на стене и прошедшее время. Расписания хранят и показывают местное время, но «через 6 часов» — это
/// 6 часов настоящих, а не по циферблату: при переводе часов местное время прыгает на час. Поэтому всё
/// арифметическое — в UTC, а «в 03:00» переводится в UTC для каждого дня отдельно.
/// </summary>
/// <remarks>
/// Время с <see cref="DateTimeKind.Utc"/> берётся как есть; местное и без пояса — как время на часах в поясе
/// <c>zone</c> (по умолчанию — пояс этого компьютера). Тесты передают свой пояс и время без пояса.
/// </remarks>
public static class WallClock
{
    /// <summary>Момент в UTC.</summary>
    public static DateTime ToUtc(DateTime t, TimeZoneInfo? zone = null)
    {
        if (t.Kind == DateTimeKind.Utc) return t;
        // DateTime.Now в «повторный» час помнит, какой из двух часов, — ToUniversalTime это учитывает
        if (IsLocal(zone)) return t.ToUniversalTime();
        var wall = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(wall - OffsetAt(wall, zone!), DateTimeKind.Utc);
    }

    /// <summary>Момент <paramref name="utc"/> — в том же виде, что <paramref name="like"/> (UTC или местное время).</summary>
    public static DateTime FromUtc(DateTime utc, DateTime like, TimeZoneInfo? zone = null)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (like.Kind == DateTimeKind.Utc) return utc;
        return IsLocal(zone) ? utc.ToLocalTime() : TimeZoneInfo.ConvertTimeFromUtc(utc, zone!);
    }

    /// <summary>Через <paramref name="span"/> настоящего времени после <paramref name="t"/>, в том же виде.</summary>
    public static DateTime After(DateTime t, TimeSpan span, TimeZoneInfo? zone = null) =>
        FromUtc(ToUtc(t, zone) + span, t, zone);

    /// <summary>Сколько настоящего времени прошло от <paramref name="from"/> до <paramref name="to"/>.</summary>
    public static TimeSpan Between(DateTime from, DateTime to, TimeZoneInfo? zone = null) => ToUtc(to, zone) - ToUtc(from, zone);

    /// <summary>Дата на часах в поясе для момента <paramref name="t"/>.</summary>
    public static DateTime DateOf(DateTime t, TimeZoneInfo? zone = null)
    {
        var utc = ToUtc(t, zone);
        return (IsLocal(zone) ? utc.ToLocalTime() : TimeZoneInfo.ConvertTimeFromUtc(utc, zone!)).Date;
    }

    /// <summary>
    /// Момент (UTC), когда на часах в этот день <paramref name="timeOfDay"/>. Такого времени нет (весной часы прыгнули
    /// вперёд) — на столько же позже, сколько пропустили; оно было дважды (осенью) — первое.
    /// </summary>
    public static DateTime UtcAt(DateTime date, TimeSpan timeOfDay, TimeZoneInfo? zone = null)
    {
        var wall = DateTime.SpecifyKind(date.Date + timeOfDay, DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(wall - OffsetAt(wall, zone ?? TimeZoneInfo.Local), DateTimeKind.Utc);
    }

    private static TimeSpan OffsetAt(DateTime wall, TimeZoneInfo zone)
    {
        if (zone.IsAmbiguousTime(wall)) return zone.GetAmbiguousTimeOffsets(wall).Max(); // первый из двух — летний
        // пропущенное время — по смещению до прыжка: 03:30 при прыжке 03:00 → 04:00 становится 04:30
        if (zone.IsInvalidTime(wall)) return zone.GetUtcOffset(wall.AddHours(-3));
        return zone.GetUtcOffset(wall);
    }

    private static bool IsLocal(TimeZoneInfo? zone) => zone is null || zone.Equals(TimeZoneInfo.Local);
}
