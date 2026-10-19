using Avalonia.Controls;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Разделитель, за которым ширину колонки запоминают (карточка «Моих модов» и каталога): сохранять, только если он
/// правда сдвинул колонку. Thumb поднимает DragCompleted на любое отпускание после нажатия, и простой щелчок по
/// разделителю записал бы ширину, которую колонке временно дало узкое окно (FitCard, FitDetails), — в широком окне
/// карточка к своей ширине потом уже не вернулась бы.
/// </summary>
internal static class SplitterMemory
{
    /// <param name="column">колонка, ширину которой запоминают</param>
    /// <param name="save">записать новую ширину колонки</param>
    public static void Remember(GridSplitter splitter, ColumnDefinition column, Action<double> save)
    {
        var before = double.NaN; // ширина в момент нажатия
        splitter.DragStarted += (_, _) => before = column.ActualWidth;
        splitter.DragCompleted += (_, _) =>
        {
            var moved = !double.IsNaN(before) && Math.Abs(column.ActualWidth - before) >= 0.5;
            before = double.NaN;
            if (moved) save(column.ActualWidth);
        };
    }
}
