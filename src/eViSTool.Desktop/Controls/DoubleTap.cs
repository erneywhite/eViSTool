using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Двойной щелчок по строке списка выполняет команду:
/// <c>&lt;ListBox c:DoubleTap.Command="{Binding OpenCommand}" /&gt;</c> — для ListBox, DataGrid и любого ItemsControl.
/// Срабатывает только по строке: заголовок колонки, полоса прокрутки и пустое место под строками не в счёт, как и
/// кнопка, переключатель или поле внутри строки (у них свой щелчок — в WPF их тоже не перехватывало).
/// Параметр команды — <see cref="CommandParameterProperty"/>, если он задан, иначе элемент строки (то, что лежит в
/// ItemsSource): окна на WPF брали SelectedItem, а это он и есть — первый щелчок выделил строку.
/// </summary>
public static class DoubleTap
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(DoubleTap));

    public static readonly AttachedProperty<object?> CommandParameterProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("CommandParameter", typeof(DoubleTap));

    static DoubleTap()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.RemoveHandler(InputElement.DoubleTappedEvent, OnDoubleTapped);
            if (e.NewValue is not null) control.AddHandler(InputElement.DoubleTappedEvent, OnDoubleTapped);
        });
    }

    public static ICommand? GetCommand(Control control) => control.GetValue(CommandProperty);
    public static void SetCommand(Control control, ICommand? value) => control.SetValue(CommandProperty, value);
    public static object? GetCommandParameter(Control control) => control.GetValue(CommandParameterProperty);
    public static void SetCommandParameter(Control control, object? value) => control.SetValue(CommandParameterProperty, value);

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control owner || GetCommand(owner) is not { } command || e.Source is not Visual source) return;
        if (!TryFindRow(owner, source, out var item)) return;

        var parameter = owner.IsSet(CommandParameterProperty) ? GetCommandParameter(owner) : item;
        if (!command.CanExecute(parameter)) return;
        command.Execute(parameter);
        e.Handled = true;
    }

    /// <summary>Строка под щелчком: контейнер элемента списка (ListBoxItem, строка DataGrid, обёртка ItemsControl).</summary>
    private static bool TryFindRow(Control owner, Visual source, out object? item)
    {
        for (var visual = source; visual is not null && visual != owner; visual = visual.GetVisualParent())
        {
            if (visual is Button or TextBox or ComboBox) break;
            if (visual is DataGridRow row)
            {
                item = row.DataContext;
                return true;
            }
            if (owner is ItemsControl items && visual is Control container && items.IndexFromContainer(container) >= 0)
            {
                item = items.ItemFromContainer(container);
                return true;
            }
        }
        item = null;
        return false;
    }
}
