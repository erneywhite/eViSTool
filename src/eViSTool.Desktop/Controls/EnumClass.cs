using Avalonia;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Класс стиля по значению перечисления: <c>&lt;Ellipse Classes="tone" c:EnumClass.Value="{Binding Tone}" /&gt;</c> даёт
/// элементу класс <c>good</c>, <c>warn</c>… (имя члена с маленькой буквы, <c>UpdateAvailable</c> → <c>updateAvailable</c>),
/// а класс прежнего значения снимает. Стили — <c>Ellipse.tone.good</c>: так вместо лесенки DataTrigger из окна на WPF.
/// </summary>
public static class EnumClass
{
    public static readonly AttachedProperty<object?> ValueProperty =
        AvaloniaProperty.RegisterAttached<StyledElement, object?>("Value", typeof(EnumClass));

    static EnumClass()
    {
        ValueProperty.Changed.AddClassHandler<StyledElement>((element, e) =>
        {
            if (ClassName(e.OldValue) is { } old) element.Classes.Remove(old);
            if (ClassName(e.NewValue) is { } name) element.Classes.Add(name);
        });
    }

    public static object? GetValue(StyledElement element) => element.GetValue(ValueProperty);
    public static void SetValue(StyledElement element, object? value) => element.SetValue(ValueProperty, value);

    /// <summary>Имя класса для значения; не перечисление или сочетание флагов без своего имени — null.</summary>
    public static string? ClassName(object? value) =>
        value is Enum e && Enum.GetName(e.GetType(), e) is { Length: > 0 } name
            ? char.ToLowerInvariant(name[0]) + name[1..]
            : null;
}
