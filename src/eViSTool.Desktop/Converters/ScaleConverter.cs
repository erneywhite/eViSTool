using System.Globalization;
using Avalonia.Data.Converters;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Число × множитель из параметра (строка с точкой: «0.3», «-0.3»). Нужен там, где размер или сдвиг — доля ширины
/// элемента: бегущий отрезок неопределённой полоски прогресса в теме. Не число — 0.
/// </summary>
public sealed class ScaleConverter : IValueConverter
{
    public static readonly ScaleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var factor = parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 1,
        };
        return value is double v ? v * factor : 0d;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
