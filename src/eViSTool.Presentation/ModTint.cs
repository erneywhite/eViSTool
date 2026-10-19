namespace eViSTool.Presentation;

/// <summary>Цвет без прозрачности, 0–255 на канал.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Значок с буквами у мода без логотипа (docs/design.md, раздел 7): оттенок — по modid, светлота и насыщенность
/// постоянные, как в эскизе: фон oklch(0.40 0.06 h), буквы oklch(0.92 0.03 h). Тёмный спокойный фон и светлые буквы
/// того же оттенка — у каждого мода свой цвет, и он одинаковый в «Моих модах», в карточке и в каталоге. Это вычисляемые
/// цвета, а не токены темы: оттенков 360, в палитру они не входят.
/// </summary>
public static class ModTint
{
    /// <summary>Светлота и насыщенность фона значка (OKLCH).</summary>
    public const double BackgroundLightness = 0.40, BackgroundChroma = 0.06;

    /// <summary>Светлота и насыщенность букв (OKLCH).</summary>
    public const double ForegroundLightness = 0.92, ForegroundChroma = 0.03;

    /// <summary>
    /// Оттенок 0–359 по ключу мода (modid; нет его — имя файла или название). Хеш свой (FNV-1a по символам), а не
    /// string.GetHashCode: тот в .NET меняется от запуска к запуску, а цвет мода должен быть одним и тем же всегда.
    /// Регистр не важен: modid в игре сравниваются без регистра.
    /// </summary>
    public static int Hue(string? key)
    {
        var hash = 2166136261u;
        foreach (var c in (key ?? "").ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return (int)(hash % 360);
    }

    public static Rgb Background(int hue) => Oklch(BackgroundLightness, BackgroundChroma, hue);

    public static Rgb Foreground(int hue) => Oklch(ForegroundLightness, ForegroundChroma, hue);

    /// <summary>
    /// OKLCH → sRGB (формулы Björn Ottosson: OKLab → LMS → линейный sRGB → гамма). Avalonia OKLCH не понимает, поэтому
    /// переводим сами. Цвет за пределами sRGB обрезается по каналам; у наших светлоты и насыщенности таких оттенков нет.
    /// </summary>
    public static Rgb Oklch(double lightness, double chroma, double hueDegrees)
    {
        var (r, g, b) = LinearSrgb(lightness, chroma, hueDegrees);
        return new Rgb(Channel(r), Channel(g), Channel(b));
    }

    /// <summary>Линейные доли sRGB до обрезки: в пределах 0..1 — цвет входит в sRGB как есть.</summary>
    internal static (double R, double G, double B) LinearSrgb(double lightness, double chroma, double hueDegrees)
    {
        var h = hueDegrees * Math.PI / 180;
        var (a, b) = (chroma * Math.Cos(h), chroma * Math.Sin(h));

        var l = Math.Pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(lightness - 0.0894841775 * a - 1.2914855480 * b, 3);

        return (+4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    // линейная доля → байт канала sRGB (гамма-кривая sRGB)
    private static byte Channel(double linear)
    {
        var x = Math.Clamp(linear, 0, 1);
        var srgb = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(srgb, 0, 1) * 255);
    }
}
