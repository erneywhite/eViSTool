using System.Globalization;
using Avalonia.Data.Converters;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Размер миниатюры скриншота по ширине ряда: ряд делится на столько картинок, чтобы каждая была не шире 260 px
/// (узкая карточка — две в ряд, широкая — три-четыре), 16:9. Параметр: "w" — ширина, "h" — высота.
/// <c>Width="{Binding $parent[ItemsControl].Bounds.Width, Converter={x:Static conv:ScreenshotThumbConverter.Instance}, ConverterParameter=w}"</c>
/// </summary>
public sealed class ScreenshotThumbConverter : IValueConverter
{
    public static readonly ScreenshotThumbConverter Instance = new();

    private const double Target = 260, Gap = 6;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var row = value is double d && d > 0 ? d : 400;
        var perRow = Math.Max(1, Math.Ceiling(row / Target));
        var width = Math.Floor(row / perRow) - Gap - 4; // −4: рамка кнопки и округление — чтобы последняя не уехала на новый ряд
        return parameter as string == "h" ? Math.Round(width * 9 / 16) : width;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
