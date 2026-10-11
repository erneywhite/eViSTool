using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Перечисление равно ConverterParameter (имя члена). Для чипов-фильтров RadioButton ↔ enum в обе стороны:
/// <c>IsChecked="{Binding Filter, Converter={x:Static conv:EnumEqualsConverter.Instance}, ConverterParameter=Updates}"</c>,
/// и для видимости вкладок и состояний: <c>IsVisible="{Binding State, Converter=…, ConverterParameter=Loaded}"</c>.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && value.ToString() == parameter.ToString();

    // снятую галочку не пишем: её снял выбор соседнего чипа, и он уже записал своё значение
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name
            ? Enum.Parse(Nullable.GetUnderlyingType(targetType) ?? targetType, name)
            : BindingOperations.DoNothing;
}
