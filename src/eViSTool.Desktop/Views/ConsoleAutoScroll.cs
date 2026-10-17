using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace eViSTool.Desktop.Views;

/// <summary>
/// Прокрутка списка строк консоли, как у терминала: список прилипает к низу, пока пользователь сам не отмотал вверх.
/// Одна на список — у своей консоли раздела «Сервер» и у консоли сервера у хостера.
/// </summary>
internal sealed class ConsoleAutoScroll
{
    private readonly ListBox _list;
    private bool _stick = true;
    private bool _endPosted;

    public ConsoleAutoScroll(ListBox list)
    {
        _list = list;
        // прокрутку меняет и пользователь, и рост списка — ScrollChanged доходит сюда из прокрутки списка
        list.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
    }

    private ScrollViewer? Scroll => _list.FindDescendantOfType<ScrollViewer>();

    /// <summary>Строки добавились: прилипли — едем за ними. <paramref name="force"/> — к концу в любом случае (первая порция).</summary>
    public void Appended(bool force)
    {
        if (force) _stick = true;
        if (_stick) ScrollToEndLater();
    }

    // докручиваем после разметки: до неё высота списка ещё старая (особенно с переносом длинных строк)
    private void ScrollToEndLater()
    {
        if (_endPosted) return;
        _endPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _endPosted = false;
            if (_stick) Scroll?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    // Внизу — прилипли. Позиция ушла вверх или сдвинулась без изменения высоты — это крутил пользователь: отлипли.
    // Высота выросла, пока прилипли (новые строки, строки досчитались при прокрутке виртуального списка, окно сжали) —
    // догоняем низ. Сами мы прокручиваем только вниз, к концу, поэтому сдвиг вверх — всегда человек.
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer sv) return;
        var bottom = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (sv.Offset.Y >= bottom - 1) _stick = true;
        else if (e.OffsetDelta.Y < 0 || (e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0)) _stick = false;
        else if (_stick) ScrollToEndLater();
    }
}
