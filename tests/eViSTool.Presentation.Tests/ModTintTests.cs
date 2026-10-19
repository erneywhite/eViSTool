using eViSTool.Core.ModDb;
using eViSTool.Core.Mods;
using eViSTool.Core.Profiles;
using eViSTool.Core.Versioning;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Presentation.Tests;

/// <summary>
/// Цветные значки модов без логотипа (docs/design.md, раздел 7): оттенок по modid не меняется от запуска к запуску и
/// разный у разных модов, перевод OKLCH → sRGB верный, а сами цвета — тёмный спокойный фон и читаемые светлые буквы.
/// </summary>
public sealed class ModTintTests
{
    private static ModRowViewModel Row(string? modId, string file)
    {
        var info = modId is null ? null : new ModInfo { ModId = modId, OriginalModId = modId, Name = modId, Version = "1.0.0" };
        var result = UpdateChecker.Evaluate([new LocalMod("/mods/" + file, info, info is null ? "нет modinfo.json" : null)], new Dictionary<string, ModDbResult>(),
            ModVersion.ParseOrNull("1.22.7")!).Single();
        return new ModRowViewModel(result, [], false, ProfileKind.Client);
    }

    [Fact]
    public void Hue_IsTheSameForOneModId_InEveryRun_AndIgnoresCase()
    {
        // числа закреплены: string.GetHashCode в .NET меняется от запуска к запуску, свой хеш — нет
        Assert.Equal(257, ModTint.Hue("carryon"));
        Assert.Equal(92, ModTint.Hue("primitivesurvival"));
        Assert.Equal(ModTint.Hue("carryon"), ModTint.Hue("CarryOn"));
        Assert.Equal(ModTint.Hue("carryon"), Row("carryon", "carryon_1.8.0.zip").Hue);
        Assert.Equal(Row("carryon", "a.zip").Hue, Row("carryon", "b.zip").Hue); // по modid, а не по файлу
        Assert.Equal(ModTint.Hue("broken.zip"), Row(null, "broken.zip").Hue); // без modinfo — по имени файла

        // в каталоге у того же мода тот же цвет: там modid приходит с модбазы
        var listed = new ModDbListItem { Name = "Carry On", ModIdStrs = ["CarryOn"] };
        Assert.Equal(ModTint.Hue("carryon"), new CatalogItemViewModel(listed, new Dictionary<string, string>()).Hue);
    }

    [Fact]
    public void Hue_DiffersBetweenMods()
    {
        string[] ids = ["carryon", "carryon2", "latecomer", "primitivesurvival", "stand_test", "expandedfoods", "butchering", "xskills", "betterruins"];
        var hues = ids.Select(ModTint.Hue).ToList();
        Assert.All(hues, h => Assert.InRange(h, 0, 359));
        Assert.Equal(ids.Length, hues.Distinct().Count());
    }

    [Fact]
    public void Oklch_MatchesReferenceColors()
    {
        Assert.Equal(new Rgb(255, 255, 255), ModTint.Oklch(1, 0, 0));
        Assert.Equal(new Rgb(0, 0, 0), ModTint.Oklch(0, 0, 0));
        Assert.Equal(new Rgb(255, 0, 0), ModTint.Oklch(0.627955, 0.257683, 29.2339)); // чистый красный sRGB
        // токены лесной темы сняты с OKLCH эскиза — тот же перевод даёт их с точностью до единицы
        Near("#111913", ModTint.Oklch(0.205, 0.017, 152)); // Forest.Side
        Near("#1A211B", ModTint.Oklch(0.240, 0.016, 150)); // Forest.Base
        Near("#323C33", ModTint.Oklch(0.345, 0.021, 146)); // Forest.Raised
        Near("#3C473C", ModTint.Oklch(0.385, 0.024, 145)); // Forest.Line
    }

    private static void Near(string hex, Rgb actual)
    {
        var expected = (Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
        Assert.True(Math.Abs(expected.Item1 - actual.R) <= 1 && Math.Abs(expected.Item2 - actual.G) <= 1 && Math.Abs(expected.Item3 - actual.B) <= 1,
            $"{hex} ≠ {actual.Hex}");
    }

    [Fact]
    public void Tints_AreDarkCalmAndReadable_ForEveryHue()
    {
        for (var h = 0; h < 360; h++)
        {
            // оба цвета входят в sRGB как есть: ни один канал не обрезан, оттенок не искажён
            foreach (var (l, c) in new[] { (ModTint.BackgroundLightness, ModTint.BackgroundChroma), (ModTint.ForegroundLightness, ModTint.ForegroundChroma) })
            {
                var (r, g, b) = ModTint.LinearSrgb(l, c, h);
                Assert.True(r is >= 0 and <= 1 && g is >= 0 and <= 1 && b is >= 0 and <= 1, $"оттенок {h}: ({r:F3}, {g:F3}, {b:F3})");
            }

            var bg = ModTint.Background(h);
            var fg = ModTint.Foreground(h);
            // фон тёмный (темнее светлой рамки темы Forest.LineStrong #4B5649) и спокойный: каналы недалеко друг от друга
            Assert.True(Luminance(bg) < Luminance(new Rgb(0x4B, 0x56, 0x49)), $"оттенок {h}: фон {bg.Hex} слишком светлый");
            Assert.True(Math.Max(bg.R, Math.Max(bg.G, bg.B)) - Math.Min(bg.R, Math.Min(bg.G, bg.B)) <= 72, $"оттенок {h}: фон {bg.Hex} слишком яркий");
            // буквы читаются: контраст не меньше 4.5 : 1 (WCAG AA для мелкого текста)
            var contrast = (Luminance(fg) + 0.05) / (Luminance(bg) + 0.05);
            Assert.True(contrast >= 4.5, $"оттенок {h}: контраст {contrast:F2} ({fg.Hex} на {bg.Hex})");
        }
    }

    // относительная яркость WCAG
    private static double Luminance(Rgb c)
    {
        static double Linear(byte v) => v / 255.0 <= 0.04045 ? v / 255.0 / 12.92 : Math.Pow((v / 255.0 + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }
}
