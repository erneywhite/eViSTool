using eViSTool.Core.Localization;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;
using Kind = eViSTool.Presentation.ViewModels.StatsChartKind;

namespace eViSTool.Presentation.Tests;

/// <summary>Раскладка графика статистики: шкалы, деления, подписи времени, ступеньки, линии, вылеты, подсказка.</summary>
public class StatsChartLayoutTests
{
    private static readonly DateTime Start = new(2026, 10, 8, 18, 5, 0);
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(5);

    // область данных 700×120: поля 36 слева, 40 справа, 8 сверху, 22 снизу
    private const double Width = 700 + 36 + 40, Height = 120 + 8 + 22;

    private static StatsReport Report(IEnumerable<(int? Players, double? MemoryMb, double? Cpu)> values, params DateTime[] crashes)
    {
        var points = values.Select((v, i) => new StatsPoint(Start + Step * i, v.Players, v.MemoryMb, v.Cpu)).ToList();
        return new StatsReport
        {
            Enabled = true,
            Period = StatsPeriod.Day,
            From = Start,
            To = Start + Step * points.Count,
            Step = Step,
            Points = points,
            Crashes = [.. crashes],
        };
    }

    private static StatsReport Players(params int?[] players) =>
        Report(players.Select(p => (p, p is null ? null : (double?)2048, p is null ? null : (double?)10)));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.95, 1)]
    [InlineData(0.96, 2)]   // запас 5 %: линия не ложится на верхний край
    [InlineData(2.66, 4)]
    [InlineData(3.9, 6)]
    [InlineData(11, 12)]
    [InlineData(60, 64)]
    [InlineData(62, 64)]    // дальше 64 — кратно 16
    [InlineData(70, 80)]
    public void NiceGb_rounds_memory_scale_up(double gb, double top) => Assert.Equal(top, StatsChartLayout.NiceGb(gb));

    [Theory]
    [InlineData(2, new double[] { 0, 1, 2 })]
    [InlineData(4, new double[] { 0, 1, 2, 3, 4 })]
    [InlineData(5, new double[] { 0, 2, 4 })]
    [InlineData(10, new double[] { 0, 2, 4, 6, 8, 10 })]
    [InlineData(11, new double[] { 0, 5, 10 })]
    [InlineData(25, new double[] { 0, 5, 10, 15, 20, 25 })]
    [InlineData(37, new double[] { 0, 10, 20, 30 })]
    public void IntTicks_pick_a_readable_step(int max, double[] ticks) => Assert.Equal(ticks, StatsChartLayout.IntTicks(max));

    [Fact]
    public void Runs_break_on_gaps()
    {
        var report = Players(1, 2, null, null, 3, null, 0);
        var runs = StatsChartLayout.Runs(report.Points, p => p.Players);
        Assert.Equal(3, runs.Count);
        Assert.Equal([(0, 1.0), (1, 2.0)], runs[0]);
        Assert.Equal([(4, 3.0)], runs[1]);
        Assert.Equal([(6, 0.0)], runs[2]); // ноль — это значение, не пропуск
    }

    [Fact]
    public void Day_labels_every_three_hours_from_the_next_round_hour()
    {
        var report = new StatsReport { Period = StatsPeriod.Day, From = new DateTime(2026, 10, 8, 18, 7, 0), To = new DateTime(2026, 10, 9, 18, 7, 0) };
        var labels = StatsChartLayout.TimeLabels(report).ToList();
        Assert.Equal(new DateTime(2026, 10, 8, 21, 0, 0), labels[0].Time);
        Assert.Equal(new DateTime(2026, 10, 9, 18, 0, 0), labels[^1].Time);
        Assert.Equal(8, labels.Count);
        Assert.All(labels, l => Assert.Equal(Dates.Clock(l.Time), l.Text));
    }

    [Fact]
    public void Week_labels_are_weekdays_at_noon_and_month_labels_every_fifth_day_back_from_today()
    {
        var week = new StatsReport { Period = StatsPeriod.Week, From = new DateTime(2026, 10, 2, 18, 7, 0), To = new DateTime(2026, 10, 9, 18, 7, 0) };
        var days = StatsChartLayout.TimeLabels(week).ToList();
        Assert.Equal(7, days.Count); // 3.10 … 9.10
        Assert.Equal(new DateTime(2026, 10, 3, 12, 0, 0), days[0].Time);
        Assert.Equal(Dates.Weekday(new DateTime(2026, 10, 3)), days[0].Text);

        var month = new StatsReport { Period = StatsPeriod.Month, From = new DateTime(2026, 9, 9, 18, 7, 0), To = new DateTime(2026, 10, 9, 18, 7, 0) };
        var dates = StatsChartLayout.TimeLabels(month).Select(l => l.Time).ToList();
        Assert.Equal([new(2026, 9, 14), new(2026, 9, 19), new(2026, 9, 24), new(2026, 9, 29), new(2026, 10, 4), new DateTime(2026, 10, 9)], dates);
    }

    [Fact]
    public void TimeRange_names_the_step()
    {
        var t = new DateTime(2026, 10, 8, 21, 10, 0);
        Assert.Equal($"{Dates.Weekday(t)} {Dates.DayMonth(t)}, {Dates.Clock(t)}–{Dates.Clock(t.AddMinutes(5))}", StatsChartLayout.TimeRange(t, Step));
    }

    [Fact]
    public void Players_scale_is_at_least_two_and_ticks_sit_on_pixel_centres()
    {
        var g = new StatsChartLayout(Players(0, 1, 1, 0), Kind.Players, Width, Height);
        Assert.True(g.CanDraw);
        Assert.Equal(2, g.Max);
        var ticks = g.Ticks();
        Assert.Equal(["0", "1", "2"], ticks.Select(t => t.Label));
        Assert.All(ticks, t => Assert.Null(t.RightLabel));
        Assert.Equal([128.5, 68.5, 8.5], ticks.Select(t => t.Y)); // низ, середина, верх области данных
        Assert.True(ticks[0].IsBase);
        Assert.False(ticks[1].IsBase);
    }

    [Fact]
    public void Resources_scale_memory_in_gigabytes_and_cpu_in_percent()
    {
        var report = Report([(1, 2700, 10), (1, 1900, 30)]);
        var g = new StatsChartLayout(report, Kind.Resources, Width, Height);
        Assert.Equal(4, g.Max);
        var ticks = g.Ticks();
        Assert.Equal(["0", "2", "4"], ticks.Select(t => t.Label));
        Assert.Equal(["0 %", "50 %", "100 %"], ticks.Select(t => t.RightLabel));

        // линии — через середины шагов: память 2700 МБ на шкале 4 ГБ, процессор 10 % на шкале 100
        var memory = Assert.Single(g.MemoryLines());
        Assert.Equal(new StatsChartPoint(36 + 175, 8 + 120 - 120 * (2700 / 1024.0 / 4)), memory[0]);
        var cpu = Assert.Single(g.CpuLines());
        Assert.Equal(new StatsChartPoint(36 + 525, 8 + 120 - 120 * 0.3), cpu[1]);
    }

    [Fact]
    public void Lonely_point_becomes_a_short_dash_to_the_end_of_its_step()
    {
        var report = Report([(null, null, null), (1, 1024, 50), (null, null, null), (null, null, null)]);
        var g = new StatsChartLayout(report, Kind.Resources, Width, Height);
        var line = Assert.Single(g.CpuLines());
        Assert.Equal([new StatsChartPoint(36 + 262.5, 68), new StatsChartPoint(36 + 350, 68)], line);
    }

    [Fact]
    public void Player_steps_are_closed_down_to_zero_and_break_on_gaps()
    {
        var g = new StatsChartLayout(Players(1, 1, null, 2), Kind.Players, Width, Height);
        double x(int i) => 36 + 175 * i;
        const double zero = 128, one = 68, two = 8;

        var areas = g.PlayerAreas();
        Assert.Equal(2, areas.Count);
        Assert.Equal([new(x(0), zero), new(x(0), one), new(x(1), one), new(x(2), one), new StatsChartPoint(x(2), zero)], areas[0]);
        Assert.Equal([new(x(3), zero), new(x(3), two), new(x(4), two), new StatsChartPoint(x(4), zero)], areas[1]);

        var tops = g.PlayerTops();
        Assert.Equal([new(x(0), one), new(x(1), one), new StatsChartPoint(x(2), one)], tops[0]);
        Assert.Equal([new(x(3), two), new StatsChartPoint(x(4), two)], tops[1]);
    }

    [Fact]
    public void Crashes_are_marked_in_order_inside_the_axis_and_close_ones_get_one_label()
    {
        var report = Report(Enumerable.Repeat<(int?, double?, double?)>((1, 1024, 5), 4),
            Start.AddMinutes(11), Start.AddMinutes(10), Start.AddHours(-1), Start.AddMinutes(19.5));
        var g = new StatsChartLayout(report, Kind.Players, Width, Height);

        // час до начала — не на оси; остальные — по времени, на середине пикселя
        Assert.Equal([Snap(36 + 350), Snap(36 + 385), Snap(36 + 682.5)], g.CrashXs());

        // 10-я и 11-я минуты рядом: подпись только у первой; у правого края подпись — слева от черты
        var labels = g.CrashLabels(30, 15);
        Assert.Equal([new StatsChartPoint(Snap(386) + 4, 8), new StatsChartPoint(Snap(718.5) - 4 - 30, 8)], labels);

        // на нижнем графике подписей нет, только черты
        Assert.Empty(new StatsChartLayout(report, Kind.Resources, Width, Height).CrashLabels(30, 15));
    }

    private static double Snap(double v) => Math.Round(v) + 0.5;

    [Fact]
    public void Index_under_the_mouse_and_the_hover_line()
    {
        var g = new StatsChartLayout(Players(1, 2, 3, 4), Kind.Players, Width, Height);
        Assert.Null(g.IndexAt(35.9));
        Assert.Equal(0, g.IndexAt(36));
        Assert.Equal(1, g.IndexAt(36 + 175));
        Assert.Equal(3, g.IndexAt(736)); // правый край — последний шаг
        Assert.Null(g.IndexAt(736.1));
        Assert.Equal(Snap(36 + 175 + 87.5), g.HoverX(1));
    }

    [Fact]
    public void Tooltip_box_goes_left_near_the_right_edge_but_not_off_the_chart()
    {
        var g = new StatsChartLayout(Players(1, 2), Kind.Players, Width, Height);
        Assert.Equal(110, g.TipLeft(100, 120));
        Assert.Equal(700 - 10 - 120, g.TipLeft(700, 120));
        Assert.Equal(0, g.TipLeft(100, 700)); // шире графика — от левого края
    }

    [Fact]
    public void Tooltip_lines_by_kind_with_gaps_and_crashes()
    {
        var report = Report([(null, null, null), (3, 2150, 41.6)], Start.AddMinutes(7));
        var players = new StatsChartLayout(report, Kind.Players, Width, Height);
        var resources = new StatsChartLayout(report, Kind.Resources, Width, Height);

        // сервер не работал
        Assert.Equal([new(StatsChartLayout.TimeRange(Start, Step), StatsChartTone.Muted), new StatsChartTipLine(Loc.T("stats.tipOff"), StatsChartTone.Muted)],
            players.TipLines(0));

        // работал, и в этом шаге был вылет
        var at = Start.AddMinutes(5);
        Assert.Equal([
            new(StatsChartLayout.TimeRange(at, Step), StatsChartTone.Muted),
            new(Loc.T("stats.tipPlayers", 3), StatsChartTone.Text),
            new StatsChartTipLine(Loc.T("stats.tipCrash", Dates.Clock(Start.AddMinutes(7))), StatsChartTone.Danger),
        ], players.TipLines(1));
        Assert.Equal([
            new(StatsChartLayout.TimeRange(at, Step), StatsChartTone.Muted),
            new(Loc.T("stats.tipMemory", (2150 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)), StatsChartTone.Good),
            new(Loc.T("stats.tipCpu", 41.6.ToString("0", System.Globalization.CultureInfo.CurrentCulture)), StatsChartTone.Warn),
            new StatsChartTipLine(Loc.T("stats.tipCrash", Dates.Clock(Start.AddMinutes(7))), StatsChartTone.Danger),
        ], resources.TipLines(1));
    }

    [Fact]
    public void Nothing_to_draw_without_points_or_room()
    {
        var empty = new StatsChartLayout(null, Kind.Players, Width, Height);
        Assert.False(empty.CanDraw);
        Assert.Empty(empty.TimeLabels());
        Assert.Empty(empty.CrashXs());
        Assert.Empty(empty.PlayerAreas());
        Assert.Null(empty.IndexAt(100));

        Assert.False(new StatsChartLayout(Players(1), Kind.Players, 36 + 40 + 20, Height).CanDraw); // уже 20 px
        Assert.False(new StatsChartLayout(Players(1), Kind.Players, Width, 8 + 22 + 20).CanDraw);
        Assert.True(new StatsChartLayout(Players(1), Kind.Players, Width, Height).CanDraw);
    }

    [Fact]
    public void Time_labels_outside_the_axis_are_dropped()
    {
        // сутки, но точек — на 3 часа: подписи за правым краем оси не рисуются
        var points = Enumerable.Range(0, 36).Select(i => new StatsPoint(new DateTime(2026, 10, 9, 15, 0, 0) + Step * i, 1, 1024, 5)).ToList();
        var report = new StatsReport
        {
            Period = StatsPeriod.Day, From = new DateTime(2026, 10, 9, 15, 0, 0), To = new DateTime(2026, 10, 10, 15, 0, 0), Step = Step, Points = points,
        };
        var g = new StatsChartLayout(report, Kind.Players, Width, Height);
        var label = Assert.Single(g.TimeLabels()); // 18:00 — на правом краю (три часа из трёх), 21:00 и дальше — за ним
        Assert.Equal(736, label.X);
        Assert.Equal(Dates.Clock(new DateTime(2026, 10, 9, 18, 0, 0)), label.Text);
    }

    // ---------- предел памяти машины

    private static StatsReport Memory(long machineMb, params double[] memoryMb) =>
        Report(memoryMb.Select(m => ((int?)1, (double?)m, (double?)10))) with { MachineMemoryMb = machineMb, PeakMemoryMb = (long)memoryMb.Max() };

    [Fact]
    public void Limit_IsDrawnOnlyWhenThePeakIsClose_AndTheScaleNeverStretchesToAFarLimit()
    {
        // 4 ГБ из 64: предела не видно, шкала — по пику
        var far = new StatsChartLayout(Memory(64 * 1024, 3000, 4096), Kind.Resources, Width, Height);
        Assert.Null(far.LimitGb);
        Assert.Null(far.LimitY);
        Assert.Equal(6, far.Max);

        // 3,2 ГБ из 4 (80 %): пунктир на 4 ГБ, шкала расширена до 6, чтобы он влез
        var close = new StatsChartLayout(Memory(4096, 2000, 3277), Kind.Resources, Width, Height);
        Assert.Equal(4, close.LimitGb);
        Assert.Equal(6, close.Max);
        Assert.Equal(StatsChartLayout.Snap(close.Y(4, 6)), close.LimitY);

        // у игроков и от старого агента предела нет
        Assert.Null(new StatsChartLayout(Memory(4096, 3277), Kind.Players, Width, Height).LimitGb);
        Assert.Null(new StatsChartLayout(Memory(4096, 3277) with { MachineMemoryMb = null }, Kind.Resources, Width, Height).LimitGb);
    }

    [Fact]
    public void Tiles_SayHowMuchTheMachineHas_AndATightPeakTurnsYellow() => Sta.Run(() =>
    {
        var language = Loc.Instance.Language; // главная модель выставляет язык из настроек — другим тестам его не менять
        var stats = new MainViewModel().Server.Stats;
        Loc.Instance.SetLanguage(language);
        var r = Memory(4096, 2000, 3800) with { AvgMemoryMb = 2900, PeakMemoryAt = Start, AvgCpu = 5, PeakCpu = 30, PeakCpuAt = Start, MachineCores = 8 };

        stats.Show(r);

        Assert.Equal(Loc.T("stats.ofMemory", ServerStatsViewModel.LimitGb(4096)), stats.MemoryLimitText);
        Assert.True(stats.MemoryTight);                                 // 3,7 ГБ из 4 — больше 90 %
        Assert.StartsWith(" · ", stats.MemoryPeakText);
        Assert.Equal(ServerStatsViewModel.AvgPeakGb(2900, 3800), stats.MemoryText);
        Assert.Equal((2.8).ToString("0.0", Loc.Culture) + " / " + (3.7).ToString("0.0", Loc.Culture) + " " + Loc.T("stats.gb"),
            ServerStatsViewModel.AvgPeakGb(2900, 3800)); // единица одна: так влезает в плитку
        Assert.Equal("850 " + Loc.T("stats.mb") + " / " + (1.2).ToString("0.0", Loc.Culture) + " " + Loc.T("stats.gb"),
            ServerStatsViewModel.AvgPeakGb(850, 1228));
        Assert.Equal(Loc.Plural("stats.ofCores", 8), stats.CpuLimitText);
        Assert.Contains(Loc.Plural("stats.cores", 8), stats.LoadTip);

        stats.Show(r with { MachineMemoryMb = 64 * 1024, MachineCores = null }); // старый агент не знает ядер
        Assert.False(stats.MemoryTight);
        Assert.Equal("", stats.CpuLimitText);
        Assert.DoesNotContain(" · ", stats.CpuPeakText);
        Assert.Equal(Loc.T("stats.loadTip"), stats.LoadTip);
    });
}
