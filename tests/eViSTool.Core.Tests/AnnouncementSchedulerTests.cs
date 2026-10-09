using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

/// <summary>Объявления по расписанию: когда что сказать.</summary>
public sealed class AnnouncementSchedulerTests
{
    private static readonly DateTime Start = new(2026, 10, 9, 12, 0, 0);

    private static ServerAnnouncements Two(bool onlyWithPlayers = true) => new()
    {
        OnlyWithPlayers = onlyWithPlayers,
        Items = [new Announcement("rules", 10), new Announcement("discord", 10), new Announcement("off", 1, Enabled: false)],
    };

    /// <summary>Прогнать планировщик поминутно (как агент — каждые несколько секунд) и собрать сказанное.</summary>
    private static List<(int Minute, string Text)> Run(AnnouncementScheduler s, ServerAnnouncements settings, int minutes, int players = 1)
    {
        var said = new List<(int, string)>();
        for (var sec = 0; sec <= minutes * 60; sec += 5)
            if (s.Tick(settings, Start.AddSeconds(sec), ServerState.Running, Start, players) is { } text)
                said.Add((sec / 60, text));
        return said;
    }

    [Fact]
    public void EachOnItsOwnInterval_StaggeredByAMinute_DisabledNever()
    {
        var said = Run(new AnnouncementScheduler(), Two(), 30);

        Assert.Equal([(10, "rules"), (11, "discord"), (20, "rules"), (21, "discord"), (30, "rules")], said);
    }

    [Fact]
    public void NobodyOnline_NothingIsSaid_AndNothingPilesUp()
    {
        var s = new AnnouncementScheduler();
        Assert.Empty(Run(s, Two(), 60, players: 0));

        // пришёл игрок — не пачка накопившегося, а через полный интервал
        var t = Start.AddMinutes(60);
        Assert.Null(s.Tick(Two(), t, ServerState.Running, Start, 1));
        Assert.Null(s.Tick(Two(), t.AddMinutes(9), ServerState.Running, Start, 1));
        Assert.Equal("rules", s.Tick(Two(), t.AddMinutes(10), ServerState.Running, Start, 1));
    }

    [Fact]
    public void EmptyServerAllowed_WhenTheSwitchIsOff()
    {
        Assert.NotEmpty(Run(new AnnouncementScheduler(), Two(onlyWithPlayers: false), 15, players: 0));
    }

    [Fact]
    public void TwoDueAtOnce_GoAMinuteApart()
    {
        var settings = new ServerAnnouncements { Items = [new Announcement("a", 5), new Announcement("b", 4)] };
        var said = Run(new AnnouncementScheduler(), settings, 6);

        // «b» сдвинуто на минуту: срок у обоих — 5-я минута, но второе ждёт минуту
        Assert.Equal([(5, "a"), (6, "b")], said);
    }

    [Fact]
    public void ServerStopped_NothingIsSaid()
    {
        var s = new AnnouncementScheduler();
        Assert.Null(s.Tick(Two(), Start.AddHours(1), ServerState.Stopped, null, 3));
    }
}
