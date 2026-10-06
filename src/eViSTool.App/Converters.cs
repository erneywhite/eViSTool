using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace eViSTool.App;

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Ширина в пикселях ↔ GridLength (для колонки, которую тянут разделителем).</summary>
public sealed class PixelsToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new GridLength(value is double d ? d : 400);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is GridLength g ? g.Value : 400d;
}

/// <summary>
/// Размер миниатюры скриншота по ширине ряда: ряд делится на столько картинок, чтобы каждая была не шире 260 px
/// (узкая карточка — две в ряд, широкая — три-четыре), 16:9. Параметр: "w" — ширина, "h" — высота.
/// </summary>
public sealed class ScreenshotThumbConverter : IValueConverter
{
    private const double Target = 260, Gap = 6;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var row = value is double d && d > 0 ? d : 400;
        var perRow = Math.Max(1, Math.Ceiling(row / Target));
        var width = Math.Floor(row / perRow) - Gap - 4; // −4: рамка кнопки и округление — чтобы последняя не уехала на новый ряд
        return parameter as string == "h" ? Math.Round(width * 9 / 16) : width;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Есть объект — видно, null — скрыто.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Число больше нуля — видно, иначе скрыто.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true — скрыто, false — видно.</summary>
public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Значение не null (для триггеров «цвет задан»).</summary>
public sealed class IsNotNullConverter : IValueConverter
{
    public static IsNotNullConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Перечисление равно ConverterParameter — для чипов-фильтров (RadioButton ↔ enum).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>Перечисление равно ConverterParameter — видно, иначе скрыто (содержимое вкладок, состояния экрана).</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Пустая строка или null — скрыто.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Размер 0 — видно (для букв вместо логотипа: картинки нет, она ещё грузится или не загрузилась).</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d && d < 1 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
