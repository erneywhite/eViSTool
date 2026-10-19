using Avalonia;
using Avalonia.Media;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Узор заглушки под боковой панелью — «горизонтали» (docs/design.md, раздел 5): группы волнистых линий, как на
/// топографической карте, на ширину панели с запасом и на высоту до 1400 px (ниже — просто цвет Side). Узор повторяется
/// по горизонтали через <see cref="Period"/> px: его можно бесшовно сдвигать вбок (<see cref="ContourDrift"/>). Для «окон»
/// в подложку на страницах (<see cref="BackdropWindow"/>) тот же узор строится шире — <see cref="Strip"/>.
/// </summary>
public static class ContourLines
{
    /// <summary>Период узора по горизонтали, px: восемь узлов линии.</summary>
    public const int Period = 468;

    // узлы каждой линии — через 58,5 px, первый на x = −12 (у левого края панели линии не обрываются)
    private const double Knot = Period / 8.0;
    private const double Left = -12;

    // сколько узора нужно справа от нуля при любом сдвиге: период и ширина панели (210) с запасом
    private const double Right = Period + 240;

    // высоты линии в первых пяти узлах; дальше она идёт теми же высотами обратно (a0 a1 a2 a3 a4 a3 a2 a1), и через
    // восемь узлов всё повторяется. Группы по три-четыре почти параллельные линии. Весь узор поднят на Lift (см. At)
    private const double Lift = 90;

    private static readonly int[][] Lines =
    [
        [83, 92, 85, 73, 76], [100, 109, 99, 86, 90], [117, 125, 112, 98, 106],
        [322, 309, 295, 299, 315], [336, 320, 306, 312, 332], [349, 330, 316, 327, 349], [363, 341, 328, 342, 366],
        [590, 588, 599, 600, 587], [605, 605, 618, 617, 602], [621, 621, 636, 634, 617],
        [877, 875, 856, 846, 857], [891, 886, 864, 855, 871], [906, 897, 872, 865, 886], [920, 908, 880, 876, 901],
        [1154, 1144, 1152, 1166, 1161], [1169, 1159, 1170, 1184, 1176], [1184, 1174, 1187, 1201, 1191],
        [1416, 1425, 1435, 1432, 1417], [1428, 1439, 1451, 1446, 1429], [1440, 1454, 1467, 1459, 1441],
    ];

    /// <summary>Все линии одной геометрией на ширину боковой панели: строится один раз, общая для всех, кто рисует заглушку.</summary>
    public static Geometry Geometry => _geometry ??= Build(Right);

    private static Geometry? _geometry;

    /// <summary>
    /// Тот же узор шире — для «окна» в подложку на странице (<see cref="BackdropWindow"/>): от начала узора хватает на
    /// <paramref name="width"/> px при любом сдвиге плавания (ещё период справа). Строится с запасом и только растёт:
    /// новая геометрия — лишь когда окно стало шире прежнего.
    /// </summary>
    public static Geometry Strip(double width)
    {
        var right = width + Period;
        if (_strip is { } strip && _stripRight >= right) return strip;
        // с запасом в два периода: окно тянут мышью, и без запаса узор перестраивался бы на каждом шаге
        _stripRight = (Math.Ceiling(right / Period) + 2) * Period;
        return _strip = Build(_stripRight);
    }

    private static Geometry? _strip;
    private static double _stripRight;

    /// <summary>
    /// Высота линии в узле <paramref name="i"/> (любом, и отрицательном): пять своих и три обратно, по кругу. Узор поднят
    /// на <see cref="Lift"/> px: так группа из четырёх линий (322…366 в таблице) проходит через карточку с логотипом в
    /// «О программе» (165…300 px от верха окна), и линии, продолжаясь из-под панели, видны в ней целиком, а не краем.
    /// </summary>
    internal static double At(int[] line, int i)
    {
        var k = ((i % 8) + 8) % 8;
        return line[k <= 4 ? k : 8 - k] - Lift;
    }

    // гладкая кривая через узлы (Катмулл — Ром): касательная в узле — по соседним узлам. Контрольные точки отрезка — на
    // трети шага от его концов, сдвинутые по касательной: на (y[i+1] − y[i−1]) / 6. Узлы с одинаковым номером по модулю 8
    // одинаковы вместе с соседями — отрезки через период совпадают точно
    private static Geometry Build(double right)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var knots = (int)Math.Ceiling((right - Left) / Knot);
        foreach (var line in Lines)
        {
            context.BeginFigure(new Point(Left, At(line, 0)), isFilled: false);
            for (var i = 0; i < knots; i++)
            {
                double x0 = Left + i * Knot, x1 = x0 + Knot;
                double y0 = At(line, i), y1 = At(line, i + 1);
                var lean0 = (At(line, i + 1) - At(line, i - 1)) / 6;
                var lean1 = (At(line, i + 2) - At(line, i)) / 6;
                context.CubicBezierTo(new Point(x0 + Knot / 3, y0 + lean0), new Point(x1 - Knot / 3, y1 - lean1), new Point(x1, y1));
            }
            context.EndFigure(isClosed: false);
        }
        return geometry;
    }
}
