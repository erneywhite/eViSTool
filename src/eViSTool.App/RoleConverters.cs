using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using eViSTool.Core.Server.Config;

namespace eViSTool.App;

/// <summary>Цвет роли (RoleRgb) → кисть образца. Цвет не распознан (null) — прозрачная: остаётся пустая рамка.</summary>
public sealed class RoleColorBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RoleRgb rgb) return Brushes.Transparent;
        var brush = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Сетка привилегий: ровные колонки — сколько влезает по ширине, но не больше MaxColumns; строка высотой с самую
/// высокую ячейку. Число колонок панель считает сама при измерении: привязка ширины ячейки WrapPanel к ширине списка
/// зацикливала разметку (ширина ячейки росла от прохода к проходу).
/// </summary>
public sealed class ColumnsPanel : Panel
{
    /// <summary>Уже этого колонку не делаем — лучше на одну меньше.</summary>
    public double MinColumnWidth { get; set; } = 250;

    public int MaxColumns { get; set; } = 3;

    private int _columns = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = Math.Max(1, MaxColumns);
        var unbounded = double.IsInfinity(availableSize.Width);
        _columns = unbounded ? limit : Math.Clamp((int)(availableSize.Width / MinColumnWidth), 1, limit);
        var columnWidth = unbounded ? MinColumnWidth : availableSize.Width / _columns;

        double height = 0, rowHeight = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if ((i + 1) % _columns == 0 || i == InternalChildren.Count - 1)
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
        for (var start = 0; start < InternalChildren.Count; start += _columns)
        {
            var end = Math.Min(start + _columns, InternalChildren.Count);
            double rowHeight = 0;
            for (var i = start; i < end; i++) rowHeight = Math.Max(rowHeight, InternalChildren[i].DesiredSize.Height);
            for (var i = start; i < end; i++)
                InternalChildren[i].Arrange(new Rect((i - start) * columnWidth, top, columnWidth, rowHeight));
            top += rowHeight;
        }

        return finalSize;
    }
}
