using Avalonia.Controls.Primitives;
using eViSTool.Presentation;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Выделение списка над упорядоченным видом (<see cref="FilteredList{T}"/>). Когда вид сдвигает выделенную строку на
/// другое место, Avalonia считает сдвиг удалением и вставкой и теряет выделение; модель этот «ничего не выбрано»
/// пропускает, а здесь список после сдвига выбирает то, что выбрано в модели. Через саму модель вернуть нельзя:
/// привязка не переотдаёт списку значение, которое уже отдавала.
/// </summary>
public static class KeepSelection
{
    public static void Follow<T>(SelectingItemsControl list, FilteredList<T> view, Func<T?> selected) where T : class =>
        view.Refreshed += () =>
        {
            if (selected() is { } item && !ReferenceEquals(list.SelectedItem, item)) list.SelectedItem = item;
        };
}
