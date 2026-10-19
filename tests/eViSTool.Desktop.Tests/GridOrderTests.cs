using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eViSTool.Desktop.Controls;

namespace eViSTool.Desktop.Tests;

/// <summary>
/// GridOrder (docs/design.md, раздел 9): таблица показывает строки в порядке своего списка, хотя её вид сдвигов не
/// понимает. Список живёт дольше страницы (он в модели), поэтому держит таблицу подпиской, только пока она в окне.
/// </summary>
public sealed class GridOrderTests
{
    /// <summary>Список строк, который знает, кто на него подписан.</summary>
    private sealed class Watched(IEnumerable<string> items) : ObservableCollection<string>(items)
    {
        private NotifyCollectionChangedEventHandler? _handlers;

        public override event NotifyCollectionChangedEventHandler? CollectionChanged
        {
            add
            {
                _handlers += value;
                base.CollectionChanged += value;
            }
            remove
            {
                _handlers -= value;
                base.CollectionChanged -= value;
            }
        }

        /// <summary>Подписан ли на список GridOrder (а через него — таблица).</summary>
        public bool HoldsFollower =>
            _handlers?.GetInvocationList().Any(d => d.Target?.GetType().DeclaringType == typeof(GridOrder)) == true;
    }

    private static List<string> Shown(DataGrid grid) => [.. grid.CollectionView!.Cast<string>()];

    [AvaloniaFact]
    public async Task Grid_follows_moves_only_while_it_is_in_a_window()
    {
        var items = new Watched(["a", "b", "c", "d"]);
        var grid = new DataGrid
        {
            ItemsSource = items, AutoGenerateColumns = false, IsReadOnly = true,
            Columns = { new DataGridTextColumn { Header = "Имя", Binding = new Binding() } },
        };
        GridOrder.SetFollowMoves(grid, true);
        Assert.False(items.HoldsFollower); // таблица ещё не в окне
        var window = new Window { Width = 400, Height = 300, Content = grid };
        window.Show();
        try
        {
            Assert.True(items.HoldsFollower);
            items.Move(0, 3);
            await Wait.Frames(50);
            Assert.Equal(items, Shown(grid));

            // таблицу сняли с окна — список её больше не держит; сдвиг без неё таблица догоняет, вернувшись в окно
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.False(items.HoldsFollower);
            items.Move(3, 0);
            window.Content = grid;
            await Wait.Frames(50);
            Assert.True(items.HoldsFollower);
            Assert.Equal(items, Shown(grid));

            items.Move(1, 2);
            await Wait.Frames(50);
            Assert.Equal(items, Shown(grid));

            // следить перестали — подписки нет и в окне
            GridOrder.SetFollowMoves(grid, false);
            Assert.False(items.HoldsFollower);
        }
        finally
        {
            window.Close();
        }
    }
}
