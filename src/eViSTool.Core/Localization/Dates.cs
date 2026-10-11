using System.Globalization;
using System.Text.RegularExpressions;

namespace eViSTool.Core.Localization;

/// <summary>
/// Даты и время в окне. Цифры — по региону Windows (<see cref="CultureInfo.CurrentCulture"/>), как привык человек:
/// латыш с английским интерфейсом видит «11.10.2026 16:58», американец — «10/11/2026 4:58 PM». Названия дней недели —
/// на языке интерфейса (<see cref="Loc.Culture"/>), чтобы не смешивать языки в одной строке.
/// </summary>
public static partial class Dates
{
    private static CultureInfo Region => CultureInfo.CurrentCulture;

    /// <summary>Дата и время: «11.10.2026 16:58».</summary>
    public static string Stamp(DateTime t) => t.ToString("g", Region);

    /// <summary>Только дата: «11.10.2026».</summary>
    public static string Day(DateTime t) => t.ToString("d", Region);

    /// <summary>Только время: «16:58» (или «4:58 PM»).</summary>
    public static string Clock(DateTime t) => t.ToString("t", Region);

    /// <summary>Время с секундами: «16:58:07».</summary>
    public static string ClockSeconds(DateTime t) => t.ToString("T", Region);

    /// <summary>День и месяц цифрами, без года: «11.10» (или «10/11»).</summary>
    public static string DayMonth(DateTime t) => t.ToString(DayMonthPattern(Region), Region);

    /// <summary>«11.10 16:58» — для недавних событий, где год очевиден.</summary>
    public static string DayMonthClock(DateTime t) => $"{DayMonth(t)} {Clock(t)}";

    /// <summary>Короткое имя дня недели на языке интерфейса: «чт», «Thu».</summary>
    public static string Weekday(DateTime t) => t.ToString("ddd", Loc.Culture);

    /// <summary>«сегодня, 21:14», «вчера, 23:48», «чт 08.10, 21:14».</summary>
    public static string Relative(DateTime t)
    {
        var time = Clock(t);
        if (t.Date == DateTime.Today) return Loc.T("stats.today", time);
        if (t.Date == DateTime.Today.AddDays(-1)) return Loc.T("stats.yesterday", time);
        return $"{Weekday(t)} {DayMonth(t)}, {time}";
    }

    /// <summary>Сколько работает сервер или играет человек: «2 д 3 ч 05 мин», «3 ч 05 мин», «5 мин 07 с».</summary>
    public static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? Loc.T("time.dhm", (int)t.TotalDays, t.Hours, t.Minutes.ToString("00"))
        : t.TotalHours >= 1 ? Loc.T("time.hm", (int)t.TotalHours, t.Minutes.ToString("00"))
        : Loc.T("time.ms", Math.Max(0, t.Minutes), Math.Max(0, t.Seconds).ToString("00"));

    /// <summary>
    /// Короткая дата региона без года: «dd.MM.yyyy» → «dd.MM», «M/d/yyyy» → «M/d», «yyyy-MM-dd» → «MM-dd». Год убирается
    /// вместе с разделителями рядом с ним.
    /// </summary>
    public static string DayMonthPattern(CultureInfo culture)
    {
        var pattern = Year().Replace(culture.DateTimeFormat.ShortDatePattern, "").Trim();
        return pattern.Contains('d') && pattern.Contains('M') ? pattern : "dd.MM";
    }

    [GeneratedRegex("[^dM]*y+[^dM]*")]
    private static partial Regex Year();
}
