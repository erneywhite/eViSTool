using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using eViSTool.Core.Server.Config;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Цвет роли (<see cref="RoleRgb"/>) → кисть образца: <c>Background="{Binding Swatch, Converter={x:Static conv:RoleColorBrushConverter.Instance}}"</c>.
/// Цвет не распознан (null) — прозрачная кисть: остаётся пустая рамка.
/// </summary>
public sealed class RoleColorBrushConverter : IValueConverter
{
    public static readonly RoleColorBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RoleRgb rgb ? new ImmutableSolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B)) : Brushes.Transparent;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
