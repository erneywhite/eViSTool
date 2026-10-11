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
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ServerConfigViewModel old) old.FocusFieldRequested -= FocusField;
            if (e.NewValue is ServerConfigViewModel now) now.FocusFieldRequested += FocusField;
        };
    }

    /// <summary>
    /// Прокрутить к полю и поставить в него курсор (пришли из «Объявлений» по «Изменить» у приветствия). Когда вкладка
    /// и поля уже нарисованы: конфиг мог ещё грузиться — тогда ждём, пока поле появится, но не дольше нескольких секунд.
    /// </summary>
    private void FocusField(string path)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        void Attempt()
        {
            if (FindField(this, path) is { } element)
            {
                element.BringIntoView();
                if (FindDescendant<TextBox>(element) is { } box)
                {
                    box.Focus();
                    box.CaretIndex = box.Text.Length;
                }
                return;
            }
            if (DateTime.UtcNow < until)
                Dispatcher.BeginInvoke(Attempt, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        Dispatcher.BeginInvoke(Attempt, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static FrameworkElement? FindField(DependencyObject root, string path)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { DataContext: ConfigFieldViewModel field, IsVisible: true } element && field.Path == path
                && element is ContentPresenter)
                return element;
            if (FindField(child, path) is { } found) return found;
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
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
