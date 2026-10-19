using System.Globalization;
using Avalonia.Data.Converters;
using eViSTool.Core.Localization;
using eViSTool.Core.Profiles;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Имя образца в выборе значка профиля для экранного диктора: «мох», «меч», «Без цвета» вместо внутреннего ключа
/// («moss», «sword», пусто). Имена цветов и знаков не пересекаются, поэтому один конвертер на оба ряда. Вторая привязка —
/// к языку программы, как у Tr: сменили язык на ходу — имя пересчитывается.
/// <code>
/// &lt;MultiBinding Converter="{x:Static conv:IconChoiceName.Instance}"&gt;
///     &lt;Binding /&gt; &lt;Binding Path="Language" Source="{x:Static loc:Loc.Instance}" /&gt;
/// &lt;/MultiBinding&gt;
/// </code>
/// </summary>
public sealed class IconChoiceName : IMultiValueConverter
{
    public static readonly IconChoiceName Instance = new();

    public static string Of(string? name) =>
        string.IsNullOrEmpty(name) ? Loc.T("icon.color.none")
        // ключи собраны из имени; что у каждого цвета и знака есть перевод, проверяет ProfileIconTests
        : ProfileIcon.Colors.Contains(name) ? Loc.T($"icon.color.{name}")
        : ProfileIcon.Glyphs.Contains(name) ? Loc.T($"icon.glyph.{name}")
        : name;

    // язык (вторая привязка) нужен только как повод пересчитать
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        Of(values.Count > 0 ? values[0] as string : null);
}
