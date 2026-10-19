using System.Globalization;
using Avalonia.Data.Converters;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Имя образца в выборе значка профиля для экранного диктора: «мох», «меч», «Без цвета» вместо внутреннего ключа
/// («moss», «sword», пусто). Имена цветов и знаков не пересекаются, поэтому один конвертер на оба ряда.
/// </summary>
public sealed class IconChoiceName : IValueConverter
{
    public static readonly IconChoiceName Instance = new();

    public static string Of(string? name) =>
        string.IsNullOrEmpty(name) ? Loc.T("icon.color.none")
        // ключи собраны из имени; что у каждого цвета и знака есть перевод, проверяет ProfileIconTests
        : ProfileIcon.Colors.Contains(name) ? Loc.T($"icon.color.{name}")
        : ProfileIcon.Glyphs.Contains(name) ? Loc.T($"icon.glyph.{name}")
        : name;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Of(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
