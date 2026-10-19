using Avalonia;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Переход цвета кисти — фона, рамки, текста (docs/design.md, раздел 3). Вся тема пишет его вместо BrushTransition
/// из Avalonia: <c>&lt;ctl:PremulBrushTransition Property="Background" Duration="0:0:0.18" Easing="0.2,0.7,0.2,1" /&gt;</c>.
/// <para>Зачем. BrushTransition смешивает цвет по каналам без учёта прозрачности, а Transparent у Avalonia — белый с
/// нулевой альфой (#00FFFFFF). Переход от прозрачного фона к тёмно-зелёному проходил через светлые полупрозрачные цвета, и
/// элемент на миг вспыхивал белым: наведение на пункт меню, плоскую кнопку, чип; кнопка, которая включилась. Здесь цвета
/// смешиваются с предумноженной прозрачностью: у прозрачного конца своего цвета нет, по пути меняется только
/// прозрачность, а цвет берётся у другого конца. Непрозрачные цвета смешиваются как у Avalonia — в линейном свете.</para>
/// <para>Конец null (элемент только что создан, кисти ещё не было) — сразу новое значение: у Avalonia первые полпути
/// перехода от null элемент стоял бы совсем без фона и рамки. Градиент смешивается с градиентом и со сплошным цветом
/// (главная кнопка), остальные кисти (картинка, радиальный градиент) — сменой на середине, как у Avalonia.</para>
/// </summary>
public sealed class PremulBrushTransition : InterpolatingTransitionBase<IBrush?>
{
    protected override IBrush? Interpolate(double progress, IBrush? from, IBrush? to) => BrushMix.Mix(progress, from, to);
}

/// <summary>Смешение кистей и цветов с предумноженной прозрачностью в линейном свете — для <see cref="PremulBrushTransition"/>.</summary>
internal static class BrushMix
{
    public static IBrush? Mix(double progress, IBrush? from, IBrush? to)
    {
        if (from is null || to is null) return to;
        // кривая темы без отскоков; на случай чужой — за концы не выходим
        var t = Math.Clamp(progress, 0, 1);
        if (t <= 0) return from;
        if (t >= 1) return to;
        return (from, to) switch
        {
            (ISolidColorBrush a, ISolidColorBrush b) => new ImmutableSolidColorBrush(Mix(t, Effective(a), Effective(b))),
            (ILinearGradientBrush a, ILinearGradientBrush b) => Mix(t, a, b),
            (ILinearGradientBrush a, ISolidColorBrush b) => Mix(t, a, AsGradient(b, a)),
            (ISolidColorBrush a, ILinearGradientBrush b) => Mix(t, AsGradient(a, b), b),
            _ => t < 0.5 ? from : to,
        };
    }

    /// <summary>
    /// Цвет на доле пути <paramref name="t"/>: каналы, умноженные на прозрачность, смешиваются в линейном свете и делятся
    /// на смешанную прозрачность. У конца с альфой 0 вклада в цвет нет — по пути цвет другого конца, меняется только
    /// прозрачность; у двух непрозрачных — то же, что у Avalonia.
    /// </summary>
    public static Color Mix(double t, Color from, Color to)
    {
        double fa = from.A / 255.0, ta = to.A / 255.0;
        var alpha = fa + (ta - fa) * t;
        if (alpha <= 0) return Color.FromArgb(0, to.R, to.G, to.B);
        byte Channel(byte f, byte c)
        {
            var pf = ToLinear(f) * fa;
            var pt = ToLinear(c) * ta;
            return ToByte(ToSrgb((pf + (pt - pf) * t) / alpha));
        }
        return Color.FromArgb(ToByte(alpha), Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
    }

    // прозрачность кисти (Opacity) — в альфу цвета: смешанная кисть выходит с Opacity 1
    private static Color Effective(ISolidColorBrush brush) => WithOpacity(brush.Color, brush.Opacity);

    private static Color WithOpacity(Color color, double opacity) =>
        opacity >= 1 ? color : Color.FromArgb(ToByte(color.A / 255.0 * Math.Max(0, opacity)), color.R, color.G, color.B);

    /// <summary>Сплошной цвет как градиент с остановками и геометрией <paramref name="shape"/> — чтобы смешать их по остановкам.</summary>
    private static ILinearGradientBrush AsGradient(ISolidColorBrush brush, ILinearGradientBrush shape) =>
        new ImmutableLinearGradientBrush(shape.GradientStops.Select(s => new ImmutableGradientStop(s.Offset, Effective(brush))).ToList(),
            1, Immutable(shape.Transform), shape.TransformOrigin, shape.SpreadMethod, shape.StartPoint, shape.EndPoint);

    // остановки — попарно по номеру (лишние у короче — её последняя), как у Avalonia; геометрия — плавно, если в одних
    // единицах, иначе сменой на середине
    private static ILinearGradientBrush Mix(double t, ILinearGradientBrush from, ILinearGradientBrush to)
    {
        var count = Math.Max(from.GradientStops.Count, to.GradientStops.Count);
        var stops = new List<ImmutableGradientStop>(count);
        for (var i = 0; i < count; i++)
        {
            var f = from.GradientStops[Math.Min(i, from.GradientStops.Count - 1)];
            var c = to.GradientStops[Math.Min(i, to.GradientStops.Count - 1)];
            stops.Add(new ImmutableGradientStop(f.Offset + (c.Offset - f.Offset) * t,
                Mix(t, WithOpacity(f.Color, from.Opacity), WithOpacity(c.Color, to.Opacity))));
        }
        var near = t < 0.5 ? from : to;
        return new ImmutableLinearGradientBrush(stops, 1, Immutable(near.Transform), near.TransformOrigin, near.SpreadMethod,
            Mix(t, from.StartPoint, to.StartPoint), Mix(t, from.EndPoint, to.EndPoint));
    }

    private static RelativePoint Mix(double t, RelativePoint from, RelativePoint to) =>
        from.Unit != to.Unit ? (t < 0.5 ? from : to)
            : new RelativePoint(from.Point.X + (to.Point.X - from.Point.X) * t, from.Point.Y + (to.Point.Y - from.Point.Y) * t, from.Unit);

    private static ImmutableTransform? Immutable(ITransform? transform) => transform is null ? null : new ImmutableTransform(transform.Value);

    // sRGB ↔ линейный свет — те же формулы, что у Avalonia (ColorAnimator)
    private static double ToLinear(byte value)
    {
        var c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double ToSrgb(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        return linear <= 0.0031308 ? linear * 12.92 : Math.Pow(linear, 5.0 / 12.0) * 1.055 - 0.055;
    }

    private static byte ToByte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
}
