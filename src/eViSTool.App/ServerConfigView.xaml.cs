using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using eViSTool.App.ViewModels;

namespace eViSTool.App;

/// <summary>Редактор serverconfig.json в разделе «Сервер». DataContext — ServerConfigViewModel.</summary>
public partial class ServerConfigView : UserControl
{
    public ServerConfigView()
    {
        InitializeComponent();
        // редактор снова на экране (вернулись с «Модов», из консоли): файл могли изменить — модель покажет свежее,
        // если своих правок нет
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) (DataContext as ServerConfigViewModel)?.RefreshIfChangedOnDisk();
        };
    }

    // Колесо над закрытым выпадающим списком меняло бы его значение (если он в фокусе) — в длинной форме это
    // случайные правки. Отдаём прокрутку странице; открытый список крутится сам.
    private void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer scroll || e.OriginalSource is not DependencyObject source) return;

        for (var element = source; element is not null && !ReferenceEquals(element, scroll); element = ParentOf(element))
        {
            if (element is not ComboBox combo) continue;
            if (combo.IsDropDownOpen) return;

            e.Handled = true;
            scroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = scroll });
            return;
        }
    }

    /// <summary>Родитель по визуальному дереву, а где его нет (содержимое всплывающего списка, Run) — по логическому.</summary>
    private static DependencyObject? ParentOf(DependencyObject element) =>
        (element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : null) ?? LogicalTreeHelper.GetParent(element);
}
