using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>
/// Аудит 4.1: расписания при переводе часов. Рига: 2026-10-25 в 04:00 часы назад на 03:00 (01:00 UTC),
/// 2027-03-28 в 03:00 вперёд на 04:00 (01:00 UTC). Время без пояса — по часам в Риге, с Utc — момент.
/// </summary>
public sealed class DaylightSavingTests
{
    private static readonly TimeZoneInfo Riga = TimeZoneInfo.FindSystemTimeZoneById("Europe/Riga");

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void WallClock_CountsRealHours()
    {
        // ночью осенью на часах 00:00 → 06:00, а прошло 7 часов
        Assert.Equal(TimeSpan.FromHours(7), WallClock.Between(new(2026, 10, 25, 0, 0, 0), new(2026, 10, 25, 6, 0, 0), Riga));
        Assert.Equal(TimeSpan.FromHours(5), WallClock.Between(new(2027, 3, 28, 0, 0, 0), new(2027, 3, 28, 6, 0, 0), Riga));
        // 03:30 весной нет — на час позже; осенью было дважды — первое (летнее, +3)
        Assert.Equal(Utc(2027, 3, 28, 1, 30), WallClock.UtcAt(new(2027, 3, 28), new TimeSpan(3, 30, 0), Riga));
        Assert.Equal(Utc(2026, 10, 25, 0, 30), WallClock.UtcAt(new(2026, 10, 25), new TimeSpan(3, 30, 0), Riga));
    }

    [Fact]
    public void IntervalRestart_IsRealHours_InAutumn()
    {
        var every6h = new ServerAutomation { RestartMode = RestartMode.Interval, RestartIntervalHours = 6 };
        // запущен в полночь — через 6 настоящих часов на часах 05:00, а не 06:00
        Assert.Equal(new DateTime(2026, 10, 25, 5, 0, 0),
            RestartScheduler.NextAt(every6h, ServerState.Running, new DateTime(2026, 10, 25, 0, 0, 0), Riga));
    }

    [Fact]
    public void DailyRestart_AtMissingSpringTime_StillWarns()
    {
        var daily = new ServerAutomation { RestartMode = RestartMode.Daily, RestartTimes = ["3:30"] };
        var started = new DateTime(2027, 3, 27, 12, 0, 0);
        Assert.Equal(new DateTime(2027, 3, 28, 4, 30, 0), RestartScheduler.NextAt(daily, ServerState.Running, started, Riga));

        var s = new RestartScheduler();
        Assert.Null(s.Tick(daily, Utc(2027, 3, 28, 1, 0), ServerState.Running, started, Riga));
        Assert.Equal(new RestartStep(false, 10), s.Tick(daily, Utc(2027, 3, 28, 1, 20), ServerState.Running, started, Riga));
        Assert.Equal(new RestartStep(true, 0), s.Tick(daily, Utc(2027, 3, 28, 1, 30), ServerState.Running, started, Riga));
    }

    [Fact]
    public void DailyRestart_InRepeatedAutumnHour_FiresOnce()
    {
        var daily = new ServerAutomation { RestartMode = RestartMode.Daily, RestartTimes = ["3:30"] };
        var started = new DateTime(2026, 10, 24, 12, 0, 0);
        var s = new RestartScheduler();
        Assert.Equal(new RestartStep(true, 0), s.Tick(daily, Utc(2026, 10, 25, 0, 30), ServerState.Running, started, Riga));
        Assert.Null(s.Tick(daily, Utc(2026, 10, 25, 1, 30), ServerState.Running, started, Riga)); // второе 03:30 на часах
    }

    [Fact]
    public void Backup_IntervalIsRealHours()
    {
        var auto = new ServerAutomation { BackupEnabled = true, BackupIntervalHours = 2, BackupOnlyWhenPlayed = false };
        var b = new BackupScheduler();
        var started = new DateTime(2026, 10, 25, 2, 0, 0); // летнее, +3 → 23:00 UTC
        // 2 часа спустя — 01:00 UTC, на часах уже зимнее 03:00 (а не 04:00)
        Assert.Equal(new DateTime(2026, 10, 25, 3, 0, 0), b.NextAt(auto, ServerState.Running, started, Riga));
        Assert.False(b.IsDue(auto, Utc(2026, 10, 25, 0, 59), ServerState.Running, started, Riga));
        Assert.True(b.IsDue(auto, Utc(2026, 10, 25, 1, 0), ServerState.Running, started, Riga));
    }

    [Fact]
    public void Announcements_KeepTalking_WhenClocksGoBack()
    {
        var settings = new ServerAnnouncements { Items = [new Announcement("rules", 10)] };
        var a = new AnnouncementScheduler();
        var started = Utc(2026, 10, 24, 23, 0);
        var said = 0;
        // с 00:00 до 02:00 UTC каждые 10 минут, через перевод часов в 01:00 UTC
        for (var m = 0; m <= 120; m++)
            if (a.Tick(settings, Utc(2026, 10, 25, 0).AddMinutes(m), ServerState.Running, started, 1, Riga) is not null) said++;
        Assert.InRange(said, 11, 13);
    }

    [Fact]
    public void TimedAnnouncement_InRepeatedHour_IsSaidOnce()
    {
        var settings = new ServerAnnouncements { Items = [new Announcement("maintenance", Times: ["03:30"])] };
        var a = new AnnouncementScheduler();
        var started = Utc(2026, 10, 24, 12, 0);
        var said = 0;
        for (var m = 0; m <= 180; m++)
            if (a.Tick(settings, Utc(2026, 10, 24, 23, 0).AddMinutes(m), ServerState.Running, started, 1, Riga) is not null) said++;
        Assert.Equal(1, said);
    }
}
