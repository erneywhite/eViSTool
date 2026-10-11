using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Число → отступ слева (LeftMarginConverter окна на WPF): вложенное поле формы конфига сдвигается на
/// <c>ConfigField.IndentLeft</c> пикселей. Не число — без отступа.
/// </summary>
public sealed class LeftMarginConverter : IValueConverter
{
    public static readonly LeftMarginConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is double d ? d : 0, 0, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
