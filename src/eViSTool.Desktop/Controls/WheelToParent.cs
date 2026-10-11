using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Колесо мыши над элементом прокручивает страницу, а не меняет его значение:
/// <c>&lt;ComboBox c:WheelToParent.IsEnabled="True" /&gt;</c>. Выпадающий список в фокусе крутит выбор колесом —
/// в длинной форме (настройки сервера) это случайные правки. Открытый список крутится сам, как и в окне на WPF.
/// </summary>
public static class WheelToParent
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("IsEnabled", typeof(WheelToParent));

    static WheelToParent()
    {
        IsEnabledProperty.Changed.AddClassHandler<InputElement>((element, e) =>
        {
            element.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            // на спуске события: элемент и его части (поле редактируемого списка) колесо уже не увидят
            if (e.GetNewValue<bool>()) element.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        });
    }

    public static bool GetIsEnabled(InputElement element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(InputElement element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || sender is not Visual element || element is ComboBox { IsDropDownOpen: true }) return;
        e.Handled = true;

        // колесо прокручивает ScrollContentPresenter прокрутки; ближайший над элементом — её. Событие отдаём ему как
        // есть: шаг, Shift для горизонтали и передача наружу, когда прокрутка упёрлась, — всё как у обычного колеса
        var presenter = element.GetVisualAncestors().OfType<ScrollContentPresenter>().FirstOrDefault();
        if (presenter is null || TopLevel.GetTopLevel(element) is not { } root) return;
        presenter.RaiseEvent(new PointerWheelEventArgs(presenter, e.Pointer, root, e.GetPosition(root),
            e.Timestamp, e.Properties, e.KeyModifiers, e.Delta));
    }
}
