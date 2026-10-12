using Avalonia.Data.Converters;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Converters;

/// <summary>
/// Заголовки категорий в выпадающем списке ключей настроек мира. Группировки у
/// списков Avalonia нет, поэтому список плоский: <c>ItemsSource="{Binding KeyOptions, Converter={x:Static
/// conv:CategoryHeaders.Interleave}}"</c>, а контейнер заголовка выключается стилем —
/// <c>&lt;Setter Property="IsEnabled" Value="{Binding Converter={x:Static conv:CategoryHeaders.IsItem}}" /&gt;</c>:
/// выключенный пункт не выбрать ни мышью, ни стрелками.
/// </summary>
public static class CategoryHeaders
{
    /// <summary>Ключи → ключи с заголовками категорий (на один список ключей — всегда один и тот же результат).</summary>
    public static readonly IValueConverter Interleave =
        new FuncValueConverter<IReadOnlyList<WorldSettingKeyOption>?, IReadOnlyList<object>?>(
            options => options is null ? null : WorldSettingKeyList.For(options));

    /// <summary>Элемент списка — не заголовок категории.</summary>
    public static readonly IValueConverter IsItem = new FuncValueConverter<object?, bool>(item => item is not WorldSettingCategoryHeader);
}
