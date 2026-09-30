using eViSTool.Core.Server;

namespace eViSTool.Core.Tests;

public sealed class RestartSchedulerTests
{
    private static readonly DateTime Start = new(2026, 9, 30, 12, 0, 0);
    private static readonly ServerAutomation Every6h = new() { RestartMode = RestartMode.Interval, RestartIntervalHours = 6 };

    [Fact]
    public void Interval_CountsFromServerStart()
    {
        Assert.Equal(Start.AddHours(6), RestartScheduler.NextAt(Every6h, ServerState.Running, Start));
        Assert.Null(RestartScheduler.NextAt(Every6h, ServerState.Stopped, null));
        Assert.Null(RestartScheduler.NextAt(Every6h, ServerState.Starting, Start));
        Assert.Null(RestartScheduler.NextAt(Every6h with { RestartMode = RestartMode.Off }, ServerState.Running, Start));
        // слишком маленький интервал поднимается до минимума — сервер не перезапускается без конца
        Assert.Equal(Start.AddMinutes(5), RestartScheduler.NextAt(Every6h with { RestartIntervalHours = 0 }, ServerState.Running, Start));
    }

    [Fact]
    public void Daily_PicksNearestTime_ButNotRightAfterStart()
    {
        var daily = new ServerAutomation { RestartMode = RestartMode.Daily, RestartTimes = ["17:30", "5:00", "чепуха"] };
        Assert.Equal(new DateTime(2026, 9, 30, 17, 30, 0), RestartScheduler.NextAt(daily, ServerState.Running, Start));
        // после 17:30 — завтра в 05:00
        Assert.Equal(new DateTime(2026, 10, 1, 5, 0, 0), RestartScheduler.NextAt(daily, ServerState.Running, Start.AddHours(6)));
        // запущен в 17:27 — до 17:30 меньше пяти минут работы: следующий раз
        Assert.Equal(new DateTime(2026, 10, 1, 5, 0, 0), RestartScheduler.NextAt(daily, ServerState.Running, new DateTime(2026, 9, 30, 17, 27, 0)));
        Assert.Null(RestartScheduler.NextAt(daily with { RestartTimes = ["25:00"] }, ServerState.Running, Start));
    }

    [Fact]
    public void Tick_WarnsAt10And5_ThenEveryMinute_ThenRestartsOnce()
    {
        var s = new RestartScheduler();
        var at = Start.AddHours(6);
        Assert.Null(s.Tick(Every6h, at.AddMinutes(-30), ServerState.Running, Start));

        Assert.Equal(new RestartStep(false, 10), s.Tick(Every6h, at.AddMinutes(-10), ServerState.Running, Start));
        Assert.Null(s.Tick(Every6h, at.AddMinutes(-9), ServerState.Running, Start)); // уже предупредили
        Assert.Null(s.Tick(Every6h, at.AddMinutes(-6), ServerState.Running, Start)); // между 10 и 5 — тишина
        foreach (var left in new[] { 5, 4, 3, 2, 1 })
        {
            Assert.Equal(new RestartStep(false, left), s.Tick(Every6h, at.AddMinutes(-left), ServerState.Running, Start));
            Assert.Null(s.Tick(Every6h, at.AddMinutes(-left).AddSeconds(5), ServerState.Running, Start)); // следующий тик — не повтор
        }

        Assert.Equal(new RestartStep(true, 0), s.Tick(Every6h, at.AddSeconds(2), ServerState.Running, Start));
        Assert.Null(s.Tick(Every6h, at.AddSeconds(7), ServerState.Running, Start)); // второй раз не перезапускаем

        // сервер поднялся заново — новый отсчёт и новые предупреждения
        var restarted = at.AddMinutes(1);
        Assert.Null(s.Tick(Every6h, restarted.AddHours(1), ServerState.Running, restarted));
        Assert.Equal(new RestartStep(false, 10), s.Tick(Every6h, restarted.AddHours(6).AddMinutes(-10), ServerState.Running, restarted));
    }

    [Fact]
    public void Tick_AfterLateStart_WarnsOnceWithRealTimeLeft()
    {
        // расписание включили, когда до перезапуска осталось три минуты: одно предупреждение «через 3»,
        // а не «через 10», «через 5» и «через 4» подряд
        var s = new RestartScheduler();
        var at = Start.AddHours(6);
        Assert.Equal(new RestartStep(false, 3), s.Tick(Every6h, at.AddMinutes(-3), ServerState.Running, Start));
        Assert.Null(s.Tick(Every6h, at.AddMinutes(-3).AddSeconds(5), ServerState.Running, Start));
        Assert.Equal(new RestartStep(false, 2), s.Tick(Every6h, at.AddMinutes(-2), ServerState.Running, Start));
    }

    [Fact]
    public void Tick_CustomWarnings_AreRespected()
    {
        var s = new RestartScheduler();
        var settings = Every6h with { RestartWarnMinutes = [30, 1] };
        var at = Start.AddHours(6);
        Assert.Equal(new RestartStep(false, 30), s.Tick(settings, at.AddMinutes(-30), ServerState.Running, Start));
        Assert.Null(s.Tick(settings, at.AddMinutes(-10), ServerState.Running, Start));
        Assert.Null(s.Tick(settings, at.AddMinutes(-5), ServerState.Running, Start));
        Assert.Equal(new RestartStep(false, 1), s.Tick(settings, at.AddMinutes(-1), ServerState.Running, Start));

        // без предупреждений — сразу перезапуск в срок
        var silent = new RestartScheduler();
        Assert.Null(silent.Tick(Every6h with { RestartWarnMinutes = [] }, at.AddMinutes(-1), ServerState.Running, Start));
        Assert.Equal(new RestartStep(true, 0), silent.Tick(Every6h with { RestartWarnMinutes = [] }, at, ServerState.Running, Start));
    }

    [Fact]
    public void Settings_ParseTimesAndWarnings()
    {
        Assert.Equal([10, 5, 4, 3, 2, 1], new ServerAutomation().RestartWarnings); // по умолчанию: за 10, за 5 и дальше каждую минуту
        Assert.True(new ServerAutomation().RestartBackup); // и с копией мира перед перезапуском

        Assert.True(ServerAutomation.TryTimeOfDay(" 5:00 ", out var t));
        Assert.Equal(new TimeSpan(5, 0, 0), t);
        Assert.True(ServerAutomation.TryTimeOfDay("17.30", out t));
        Assert.Equal(new TimeSpan(17, 30, 0), t);
        foreach (var bad in new[] { "", "24:00", "12:60", "12", "12:5", "ab:cd", "-1:00" })
            Assert.False(ServerAutomation.TryTimeOfDay(bad, out _), bad);

        var settings = new ServerAutomation { RestartWarnMinutes = [1, 10, 5, 5, 0, 999], RestartTimes = ["17:30", "05:00", "5:00"] };
        Assert.Equal([10, 5, 1], settings.RestartWarnings);
        Assert.Equal([new TimeSpan(5, 0, 0), new TimeSpan(17, 30, 0)], settings.RestartTimesOfDay);

        // круг через файл
        var dir = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            (settings with { RestartMode = RestartMode.Daily, RestartBackup = false }).Save("p", dir);
            var loaded = ServerAutomation.Load("p", dir);
            Assert.Equal(RestartMode.Daily, loaded.RestartMode);
            Assert.False(loaded.RestartBackup);
            Assert.Equal(["17:30", "05:00", "5:00"], loaded.RestartTimes);
            Assert.Equal([1, 10, 5, 5, 0, 999], loaded.RestartWarnMinutes); // сохранённое, а не значения по умолчанию
            Assert.Contains("\"Daily\"", File.ReadAllText(ServerAutomation.FileFor("p", dir)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
