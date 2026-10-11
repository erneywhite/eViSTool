using System.Globalization;
using System.Windows;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media;
using eViSTool.Presentation.ViewModels;

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
    /// <summary>Наоборот: видно, когда текста нет (подсказка в пустом поле).</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } != Invert ? Visibility.Visible : Visibility.Collapsed;

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

/// <summary>
/// Известные ключи настроек мира — с группами по категориям (в модели лежит плоский список). Вид один на список:
/// у всех строк настроек общий источник, как и общий выпадающий список.
/// </summary>
public sealed class GroupByCategoryConverter : IValueConverter
{
    public static GroupByCategoryConverter Instance { get; } = new();

    private readonly ConditionalWeakTable<object, ListCollectionView> _views = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not System.Collections.IList list) return null;
        if (_views.TryGetValue(list, out var cached)) return cached;
        var view = new ListCollectionView(list);
        view.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(WorldSettingKeyOption.Category)));
        _views.Add(list, view);
        return view;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Цвет строки консоли по смыслу. Лесная палитра: ошибки — розоватые, предупреждения — янтарь, ввод — песок,
/// сообщения eViSTool — дымчато-голубые, чат — зелёный. null — обычный цвет текста темы.
/// </summary>
public sealed class ConsoleTintBrushConverter : IValueConverter
{
    public static ConsoleTintBrushConverter Instance { get; } = new();

    private static readonly Brush ErrorBrush = Frozen(0xE8, 0x9B, 0x96);
    private static readonly Brush WarningBrush = Frozen(0xED, 0xBF, 0x82);
    private static readonly Brush InputBrush = Frozen(0xD4, 0xBD, 0x83);
    private static readonly Brush SystemBrush = Frozen(0x9F, 0xB7, 0xC9);
    private static readonly Brush ChatBrush = Frozen(0xB0, 0xC8, 0x98);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ConsoleTint.Error => ErrorBrush,
        ConsoleTint.Warning => WarningBrush,
        ConsoleTint.Input => InputBrush,
        ConsoleTint.System => SystemBrush,
        ConsoleTint.Chat => ChatBrush,
        _ => null,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Значок игры из списка папок (первая, где нашлась игра); игры нет — null.</summary>
public sealed class GameIconConverter : IValueConverter
{
    public static GameIconConverter Instance { get; } = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IEnumerable<string?> dirs ? GameIcon.From(dirs) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Значок игры найден — видно (Shown), не найден — скрыто; Hidden — наоборот (буквы «VS» вместо значка).</summary>
public sealed class GameIconVisibilityConverter(bool whenFound) : IValueConverter
{
    public static GameIconVisibilityConverter Shown { get; } = new(true);
    public static GameIconVisibilityConverter Hidden { get; } = new(false);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is IEnumerable<string?> dirs && GameIcon.From(dirs) is not null) == whenFound ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Число пикселей — поле только слева (отступ вложенных полей конфига).</summary>
public sealed class LeftMarginConverter : IValueConverter
{
    public static LeftMarginConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is double d ? d : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
