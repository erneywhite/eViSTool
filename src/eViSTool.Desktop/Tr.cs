using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using eViSTool.Core.Localization;

namespace eViSTool.Desktop;

/// <summary>
/// Перевод в разметке: Text="{local:Tr mods.check}". Текст меняется сразу при переключении языка, как и в окне на WPF.
/// Привязка — к Loc.Language, а перевод берёт конвертер: оповещение индексатора («Item[]», как ждёт WPF) привязки
/// Avalonia не ловят, а смену свойства Language — ловят.
/// </summary>
public sealed class Tr(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(Loc.Language)) { Source = Loc.Instance, Mode = BindingMode.OneWay, Converter = Translate.Instance, ConverterParameter = Key };

    private sealed class Translate : IValueConverter
    {
        public static readonly Translate Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Loc.T((string)parameter!);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
    }
}
