namespace eViSTool.Core.Game;

/// <summary>
/// Фон окна из игры (docs/design.md, раздел 5): какая заставка главного меню Vintage Story сегодня и где она лежит.
/// Картинки берутся только из папки установленной игры (assets/game/textures/gui/backgrounds): это арт игры, в программу
/// он не вшит, а игроки подменяют заставки своими. Ничего не хранится: день сам задаёт картинку, после перезапуска та же.
/// </summary>
/// <remarks>
/// Праздники — Хэллоуин (28.10–03.11) и Рождество (22.12–28.12) включительно, по местной дате. В остальные дни —
/// картинка недели: N — число недель от фиксированного понедельника, картинка — mainmenu{(N·3) mod 7 + 1}. Шаг 3 взаимно
/// прост с 7, поэтому соседние недели всегда разные, а за 7 недель подряд проходят все семь. Неделя считается по дням,
/// а не по номеру в году, — через Новый год повтора и скачка нет.
/// </remarks>
public static class GameBackdrop
{
    /// <summary>Папка заставок внутри папки игры.</summary>
    public static readonly string Folder = Path.Combine("assets", "game", "textures", "gui", "backgrounds");

    /// <summary>Сколько у игры заставок недели: mainmenu1.png … mainmenu7.png.</summary>
    public const int WeeklyCount = 7;

    public const string Halloween = "mainmenu-halloween.png";
    public const string Christmas = "mainmenu-xmas.png";

    // отсчёт недель — от понедельника: картинка меняется в ночь на понедельник
    private static readonly DateOnly WeekZero = new(2024, 1, 1);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Файл заставки недели по номеру 1…7.</summary>
    public static string Weekly(int number) => $"mainmenu{number}.png";

    /// <summary>Номер недели от фиксированного понедельника; до него — отрицательный, с округлением вниз (без двойной нулевой недели).</summary>
    public static int WeekOf(DateOnly date)
    {
        var days = date.DayNumber - WeekZero.DayNumber;
        return days >= 0 ? days / 7 : (days - 6) / 7;
    }

    /// <summary>Номер заставки недели 1…7 на эту дату.</summary>
    public static int WeeklyNumber(DateOnly date) => (int)(((long)WeekOf(date) * 3 % WeeklyCount + WeeklyCount) % WeeklyCount) + 1;

    /// <summary>Праздничная заставка на эту дату; не праздник — null.</summary>
    public static string? Holiday(DateOnly date) => (date.Month, date.Day) switch
    {
        (10, >= 28) or (11, <= 3) => Halloween,
        (12, >= 22 and <= 28) => Christmas,
        _ => null,
    };

    /// <summary>
    /// Какие файлы пробовать по порядку: праздничный (в праздник), затем картинка недели и дальше по кругу
    /// (mainmenu7 → mainmenu1): нужной нет — берётся следующая из тех, что есть.
    /// </summary>
    public static IReadOnlyList<string> Candidates(DateOnly date)
    {
        var list = new List<string>(WeeklyCount + 1);
        if (Holiday(date) is { } holiday) list.Add(holiday);
        var first = WeeklyNumber(date);
        for (var i = 0; i < WeeklyCount; i++) list.Add(Weekly((first - 1 + i) % WeeklyCount + 1));
        return list;
    }

    /// <summary>
    /// Заставка на эту дату: для каждого файла из <see cref="Candidates"/> по очереди — первая папка игры, где он есть.
    /// Папки — в порядке важности (игра активного профиля, остальных, найденная). Ни одной заставки нигде — null.
    /// </summary>
    public static string? Find(IEnumerable<string?> gameDirs, DateOnly date)
    {
        var folders = gameDirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => Path.Combine(d!, Folder))
            .Distinct(PathComparer).Where(Directory.Exists).ToList();
        if (folders.Count == 0) return null;
        foreach (var name in Candidates(date))
            foreach (var folder in folders)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    /// <summary>Заставка на сегодня (местная дата по <paramref name="time"/>).</summary>
    public static string? Find(IEnumerable<string?> gameDirs, TimeProvider time) => Find(gameDirs, Today(time));

    /// <summary>Сегодняшняя дата по местному времени.</summary>
    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetLocalNow().DateTime);
}
