using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Фокус в поле, когда открылось окно: <c>&lt;TextBox c:Focus.OnOpen="True" c:Focus.SelectAll="True" /&gt;</c>.
/// Вместо <c>Loaded += (_, _) => NameBox.Focus()</c>. Фокус ставится не сразу в Loaded/Opened, а через
/// очередь диспетчера: на Linux окно в этот момент ещё не готово его принять, и фокус теряется.
/// </summary>
public static class Focus
{
    public static readonly AttachedProperty<bool> OnOpenProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("OnOpen", typeof(Focus));

    /// <summary>Вместе с фокусом выделить весь текст (TextBox) — имя, которое скорее заменят, чем допишут.</summary>
    public static readonly AttachedProperty<bool> SelectAllProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("SelectAll", typeof(Focus));

    static Focus()
    {
        OnOpenProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.Loaded -= OnLoaded;
            if (!e.GetNewValue<bool>()) return;
            control.Loaded += OnLoaded;
            if (control.IsLoaded) Schedule(control);
        });
    }

    public static bool GetOnOpen(Control control) => control.GetValue(OnOpenProperty);
    public static void SetOnOpen(Control control, bool value) => control.SetValue(OnOpenProperty, value);
    public static bool GetSelectAll(Control control) => control.GetValue(SelectAllProperty);
    public static void SetSelectAll(Control control, bool value) => control.SetValue(SelectAllProperty, value);

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control) Schedule(control);
    }

    private static void Schedule(Control control) => Dispatcher.UIThread.Post(() => Apply(control), DispatcherPriority.Background);

    // Loaded приходит только после показа окна (окно собрали заранее, а показали позже — тоже), так что здесь окно
    // уже на экране и ждать Opened отдельно не нужно
    private static void Apply(Control control)
    {
        if (GetOnOpen(control) && control.Focus() && GetSelectAll(control) && control is TextBox box) box.SelectAll();
    }
}
