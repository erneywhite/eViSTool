using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// Разделитель карточки «Моих модов» и каталога (Controls/SplitterMemory): ширину колонки сохраняет только сдвиг. Щелчок
/// по разделителю (нажали и отпустили на месте) не записывает ширину, которую карточке временно дало узкое окно.
/// </summary>
public sealed class SplitterMemoryTests
{
    [AvaloniaFact]
    public void Click_on_the_splitter_saves_nothing_and_a_drag_saves_the_new_width()
    {
        var splitter = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 1);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14,300"), Children = { new Border(), splitter, new Border() } };
        Grid.SetColumn(grid.Children[2], 2);
        var card = grid.ColumnDefinitions[2];
        var saved = new List<double>();
        SplitterMemory.Remember(splitter, card, saved.Add);
        var window = new Window { Width = 800, Height = 300, Content = grid };
        window.Show();
        try
        {
            var at = Mouse.Center(window, splitter);

            // щелчок: нажали и отпустили на месте — ширина та же, сохранять нечего
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(saved);
            Assert.Equal(300, card.ActualWidth, 1);

            // сдвиг влево на 60 — карточка шире, ширина сохранена
            window.MouseDown(at, MouseButton.Left);
            window.MouseMove(at - new Point(30, 0));
            window.MouseMove(at - new Point(60, 0));
            window.MouseUp(at - new Point(60, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(360, Assert.Single(saved), 1);
            Assert.Equal(360, card.ActualWidth, 1);

            // и снова щелчок — второй записи нет
            at = Mouse.Center(window, splitter);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(saved);
        }
        finally
        {
            window.Close();
        }
    }
}
