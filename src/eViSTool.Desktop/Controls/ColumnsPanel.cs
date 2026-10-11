using Avalonia;
using Avalonia.Controls;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Ровные колонки: сколько влезает по ширине (каждая не уже <see cref="MinColumnWidth"/>), но не больше
/// <see cref="MaxColumns"/>; строка высотой с самую высокую ячейку. Сетка привилегий во вкладке «Роли».
/// Число колонок панель считает сама при измерении: в окне на WPF привязка ширины ячейки WrapPanel к ширине списка
/// зацикливала разметку (ширина ячейки росла от прохода к проходу).
/// </summary>
public sealed class ColumnsPanel : Panel
{
    /// <summary>Уже этого колонку не делаем — лучше на одну меньше.</summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<ColumnsPanel, double>(nameof(MinColumnWidth), 250);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<ColumnsPanel, int>(nameof(MaxColumns), 3);

    private int _columns = 1;

    static ColumnsPanel() => AffectsMeasure<ColumnsPanel>(MinColumnWidthProperty, MaxColumnsProperty);

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>Сколько колонок вышло при последнем измерении.</summary>
    public int Columns => _columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = Math.Max(1, MaxColumns);
        var unbounded = double.IsInfinity(availableSize.Width);
        var minWidth = MinColumnWidth > 0 ? MinColumnWidth : 1;
        _columns = unbounded ? limit : Math.Clamp((int)(availableSize.Width / minWidth), 1, limit);
        var columnWidth = unbounded ? minWidth : availableSize.Width / _columns;

        double height = 0, rowHeight = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if ((i + 1) % _columns == 0 || i == Children.Count - 1)
            {
                height += rowHeight;
                rowHeight = 0;
            }
        }

        return new Size(unbounded ? columnWidth * _columns : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = finalSize.Width / _columns;
        double top = 0;
        for (var start = 0; start < Children.Count; start += _columns)
        {
            var end = Math.Min(start + _columns, Children.Count);
            double rowHeight = 0;
            for (var i = start; i < end; i++) rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            for (var i = start; i < end; i++)
                Children[i].Arrange(new Rect((i - start) * columnWidth, top, columnWidth, rowHeight));
            top += rowHeight;
        }

        return finalSize;
    }
}
