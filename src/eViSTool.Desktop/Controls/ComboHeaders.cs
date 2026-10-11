using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Заголовки в плоском выпадающем списке (категории ключей настроек мира):
/// <c>&lt;ComboBox c:ComboHeaders.HeaderType="{x:Type vm:WorldSettingCategoryHeader}" /&gt;</c> — пункты этого типа стрелками
/// не выбираются: в закрытом списке ↑/↓ переходят к соседнему пункту мимо заголовков. Сам ComboBox пропускает только
/// выключенные пункты (контейнер заголовка выключает стиль), но пока список не открывали, контейнеров ещё нет — и он
/// выбрал бы заголовок, а в поле редактируемого списка попал бы его текст. Открытый список листается как обычно:
/// там контейнеры уже есть.
/// </summary>
public static class ComboHeaders
{
    public static readonly AttachedProperty<Type?> HeaderTypeProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, Type?>("HeaderType", typeof(ComboHeaders));

    static ComboHeaders()
    {
        HeaderTypeProperty.Changed.AddClassHandler<ComboBox>((combo, e) =>
        {
            combo.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            // на спуске: раньше поля ввода редактируемого списка и самого ComboBox
            if (e.NewValue is Type) combo.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        });
    }

    public static Type? GetHeaderType(ComboBox combo) => combo.GetValue(HeaderTypeProperty);
    public static void SetHeaderType(ComboBox combo, Type? value) => combo.SetValue(HeaderTypeProperty, value);

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not ComboBox { IsDropDownOpen: false } combo || GetHeaderType(combo) is not { } header
            || e.Key is not (Key.Up or Key.Down) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;

        var items = combo.ItemsView;
        var step = e.Key == Key.Down ? 1 : -1;
        for (var i = combo.SelectedIndex + step; i >= 0 && i < items.Count; i += step)
        {
            if (header.IsInstanceOfType(items[i])) continue;
            combo.SelectedIndex = i;
            break;
        }
        e.Handled = true; // дальше некуда — остаёмся на месте, как и ComboBox без заголовков
    }
}
