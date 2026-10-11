using System.Globalization;
using Avalonia.Data.Converters;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Значок игры из списка папок (первая, где нашлась игра); игры нет — null. Буквы «VS» вместо значка — по
/// <c>ObjectConverters.IsNull</c> на том же значении:
/// <c>&lt;Image Source="{Binding IconSearchDirs, Converter={x:Static conv:GameIconConverter.Instance}}" /&gt;</c>
/// </summary>
public sealed class GameIconConverter : IValueConverter
{
    public static readonly GameIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string?> dirs ? GameIcon.From(dirs) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
