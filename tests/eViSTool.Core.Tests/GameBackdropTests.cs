using eViSTool.Core.Game;

namespace eViSTool.Core.Tests;

public sealed class GameBackdropTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evistool-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Папка игры с заставками (пустые файлы: выбору важно только, что файл есть).</summary>
    private string Game(string name, params string[] pictures)
    {
        var game = Path.Combine(_root, name);
        var folder = Path.Combine(game, GameBackdrop.Folder);
        Directory.CreateDirectory(folder);
        foreach (var picture in pictures) File.WriteAllBytes(Path.Combine(folder, picture), []);
        return game;
    }

    private static string Picture(string game, string name) => Path.Combine(game, GameBackdrop.Folder, name);

    private static readonly string[] AllWeekly = [.. Enumerable.Range(1, GameBackdrop.WeeklyCount).Select(GameBackdrop.Weekly)];

    // ---- какая картинка сегодня

    [Fact]
    public void Picture_changes_only_on_monday_and_neighbour_weeks_differ()
    {
        // десять лет подряд, день за днём: внутри недели картинка одна, в понедельник — другая
        for (var day = new DateOnly(2020, 1, 1); day < new DateOnly(2030, 1, 1); day = day.AddDays(1))
        {
            var changed = GameBackdrop.WeeklyNumber(day) != GameBackdrop.WeeklyNumber(day.AddDays(-1));
            Assert.True(changed == (day.DayOfWeek == DayOfWeek.Monday), $"{day:yyyy-MM-dd} ({day.DayOfWeek})");
        }
    }

    [Fact]
    public void Seven_weeks_in_a_row_show_all_seven_pictures()
    {
        for (var start = new DateOnly(2023, 12, 25); start < new DateOnly(2027, 1, 1); start = start.AddDays(1))
        {
            var numbers = Enumerable.Range(0, 7).Select(w => GameBackdrop.WeeklyNumber(start.AddDays(7 * w))).ToHashSet();
            Assert.Equal(Enumerable.Range(1, 7), numbers.Order());
        }
    }

    [Fact]
    public void New_year_does_not_restart_the_count()
    {
        // неделя 28.12.2026 (пн) – 03.01.2027 (вс) — одна картинка, хотя год сменился посреди неё
        var week = Enumerable.Range(0, 7).Select(d => GameBackdrop.WeeklyNumber(new DateOnly(2026, 12, 28).AddDays(d))).Distinct().ToList();
        Assert.Single(week);
        Assert.NotEqual(week[0], GameBackdrop.WeeklyNumber(new DateOnly(2026, 12, 27)));
        Assert.NotEqual(week[0], GameBackdrop.WeeklyNumber(new DateOnly(2027, 1, 4)));
        // и номер недели идёт подряд, без скачка на 1 января
        Assert.Equal(GameBackdrop.WeekOf(new DateOnly(2026, 12, 21)) + 2, GameBackdrop.WeekOf(new DateOnly(2027, 1, 4)));
    }

    [Fact]
    public void Weeks_before_the_reference_monday_are_counted_down_without_a_double_zero()
    {
        Assert.Equal(0, GameBackdrop.WeekOf(new DateOnly(2024, 1, 1)));
        Assert.Equal(0, GameBackdrop.WeekOf(new DateOnly(2024, 1, 7)));
        Assert.Equal(-1, GameBackdrop.WeekOf(new DateOnly(2023, 12, 31)));
        Assert.Equal(-1, GameBackdrop.WeekOf(new DateOnly(2023, 12, 25)));
        Assert.Equal(-2, GameBackdrop.WeekOf(new DateOnly(2023, 12, 24)));
        Assert.InRange(GameBackdrop.WeeklyNumber(new DateOnly(1999, 3, 3)), 1, 7);
    }

    [Theory]
    [InlineData(2026, 10, 27, null)]
    [InlineData(2026, 10, 28, GameBackdrop.Halloween)]
    [InlineData(2026, 10, 31, GameBackdrop.Halloween)]
    [InlineData(2026, 11, 3, GameBackdrop.Halloween)]
    [InlineData(2026, 11, 4, null)]
    [InlineData(2026, 12, 21, null)]
    [InlineData(2026, 12, 22, GameBackdrop.Christmas)]
    [InlineData(2026, 12, 25, GameBackdrop.Christmas)]
    [InlineData(2026, 12, 28, GameBackdrop.Christmas)]
    [InlineData(2026, 12, 29, null)]
    [InlineData(2027, 1, 1, null)]
    public void Holidays_include_both_ends(int year, int month, int day, string? expected) =>
        Assert.Equal(expected, GameBackdrop.Holiday(new DateOnly(year, month, day)));

    [Fact]
    public void Candidates_are_holiday_then_week_picture_then_the_rest_in_a_circle()
    {
        var halloween = new DateOnly(2026, 10, 30);
        var first = GameBackdrop.WeeklyNumber(halloween);
        var candidates = GameBackdrop.Candidates(halloween);

        Assert.Equal(GameBackdrop.Halloween, candidates[0]);
        Assert.Equal(GameBackdrop.Weekly(first), candidates[1]);
        Assert.Equal(GameBackdrop.Weekly(first % 7 + 1), candidates[2]); // следующая по кругу: после 7 — снова 1
        Assert.Equal(AllWeekly.Order(), candidates.Skip(1).Order());

        var ordinary = new DateOnly(2026, 10, 19);
        Assert.Equal(GameBackdrop.Weekly(GameBackdrop.WeeklyNumber(ordinary)), GameBackdrop.Candidates(ordinary)[0]);
        Assert.Equal(7, GameBackdrop.Candidates(ordinary).Count);
    }

    // ---- где лежит

    [Fact]
    public void Picture_is_taken_from_the_first_folder_that_has_it()
    {
        var day = new DateOnly(2026, 10, 19);
        var wanted = GameBackdrop.Weekly(GameBackdrop.WeeklyNumber(day));
        var other = AllWeekly.First(p => p != wanted);
        var active = Game("active", other);   // у игры профиля нужной нет — только другая
        var second = Game("second", wanted);
        var third = Game("third", wanted);

        // нужная картинка из второй папки важнее «какой-нибудь» из первой
        Assert.Equal(Picture(second, wanted), GameBackdrop.Find([active, second, third], day));
        Assert.Equal(Picture(third, wanted), GameBackdrop.Find([third, second], day));
    }

    [Fact]
    public void Missing_week_picture_falls_back_to_the_next_one_in_the_circle()
    {
        var day = new DateOnly(2026, 10, 19);
        var wanted = GameBackdrop.WeeklyNumber(day);
        var next = wanted % 7 + 1;
        var afterNext = next % 7 + 1;
        var game = Game("game", GameBackdrop.Weekly(afterNext), GameBackdrop.Weekly(next));

        Assert.Equal(Picture(game, GameBackdrop.Weekly(next)), GameBackdrop.Find([game], day));
    }

    [Fact]
    public void Wrapping_from_seven_to_one()
    {
        // день, когда выпадает mainmenu7, а его нет: следующая — mainmenu1, а не mainmenu6
        var day = Enumerable.Range(0, 7 * 7).Select(d => new DateOnly(2026, 1, 5).AddDays(d)).First(d => GameBackdrop.WeeklyNumber(d) == 7 && GameBackdrop.Holiday(d) is null);
        var game = Game("game", GameBackdrop.Weekly(6), GameBackdrop.Weekly(1));
        Assert.Equal(Picture(game, GameBackdrop.Weekly(1)), GameBackdrop.Find([game], day));
    }

    [Fact]
    public void Holiday_picture_wins_and_falls_back_to_the_week_picture()
    {
        var halloween = new DateOnly(2026, 10, 30);
        var week = GameBackdrop.Weekly(GameBackdrop.WeeklyNumber(halloween));
        var plain = Game("plain", AllWeekly);
        Assert.Equal(Picture(plain, week), GameBackdrop.Find([plain], halloween)); // праздничной нет — картинка недели

        var festive = Game("festive", GameBackdrop.Halloween, GameBackdrop.Christmas);
        Assert.Equal(Picture(festive, GameBackdrop.Halloween), GameBackdrop.Find([plain, festive], halloween));
        Assert.Equal(Picture(festive, GameBackdrop.Christmas), GameBackdrop.Find([plain, festive], new DateOnly(2026, 12, 24)));
        Assert.Equal(Picture(plain, GameBackdrop.Weekly(GameBackdrop.WeeklyNumber(new DateOnly(2026, 11, 4)))),
            GameBackdrop.Find([plain, festive], new DateOnly(2026, 11, 4)));
    }

    [Fact]
    public void Nothing_found_is_null()
    {
        var day = new DateOnly(2026, 10, 19);
        Assert.Null(GameBackdrop.Find([], day));
        Assert.Null(GameBackdrop.Find([null, "", "   "], day));
        Assert.Null(GameBackdrop.Find([Path.Combine(_root, "missing")], day));
        Assert.Null(GameBackdrop.Find([Game("empty")], day));
        Assert.Null(GameBackdrop.Find([Game("other", "oak.png", "stone.png")], day)); // текстуры из той же папки — не заставки
    }

    [Fact]
    public void Today_is_the_local_date()
    {
        var game = Game("game", GameBackdrop.Halloween, GameBackdrop.Weekly(1));
        var riga = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

        // по UTC ещё 27 октября, а дома уже 28-е — праздник
        var evening = new Clock(new DateTimeOffset(2026, 10, 27, 22, 30, 0, TimeSpan.Zero), riga);
        Assert.Equal(new DateOnly(2026, 10, 28), GameBackdrop.Today(evening));
        Assert.Equal(Picture(game, GameBackdrop.Halloween), GameBackdrop.Find([game], evening));

        var before = new Clock(new DateTimeOffset(2026, 10, 27, 21, 30, 0, TimeSpan.Zero), riga);
        Assert.Equal(Picture(game, GameBackdrop.Weekly(1)), GameBackdrop.Find([game], before));
    }

    private sealed class Clock(DateTimeOffset utc, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
