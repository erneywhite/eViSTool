using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace eViSTool.Desktop.Controls;

/// <summary>
/// Порядок строк таблицы — как в её списке: <c>&lt;DataGrid ctl:GridOrder.FollowMoves="True" … /&gt;</c>.
/// <para>Зачем. Вид таблицы (DataGridCollectionView) пропускает сдвиги (Move) в списке строк — вставку и удаление понимает,
/// а сдвиг нет. FilteredList сортирует сдвигами, поэтому после смены порядка («Сначала важное» / «По имени») или после
/// того, как строка уехала на новое место (мод выключили — он ушёл к выключенным), таблица показывала прежний порядок, а
/// следующие вставки ложились не туда. Раньше это пряталось тем, что таблица после каждой операции строилась заново
/// целиком — и заодно прыгала к началу.</para>
/// <para>Как. После сдвигов, один раз на пачку и до ближайшего кадра, порядок вида сверяется со списком; разошёлся — вид
/// перечитывается (Refresh). Прокрутку и выбранную строку DataGrid при этом сохраняет сам. Не разошёлся (строка встала на
/// место удалением и вставкой) — таблицу не трогаем, её строки не пересобираются.</para>
/// </summary>
public static class GridOrder
{
    public static readonly AttachedProperty<bool> FollowMovesProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("FollowMoves", typeof(GridOrder));

    // кто следит за списком таблицы. Список держит следящего (а с ним таблицу) подпиской, только пока таблица в окне:
    // список живёт дольше страницы (модель), и иначе снятая с окна таблица жила бы с ним и перечитывала свой вид
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGrid, Follower> Followers = new();

    static GridOrder()
    {
        FollowMovesProperty.Changed.AddClassHandler<DataGrid>((grid, _) => Follow(grid));
        DataGrid.ItemsSourceProperty.Changed.AddClassHandler<DataGrid>((grid, _) => Follow(grid));
    }

    public static bool GetFollowMoves(DataGrid grid) => grid.GetValue(FollowMovesProperty);
    public static void SetFollowMoves(DataGrid grid, bool value) => grid.SetValue(FollowMovesProperty, value);

    private static void Follow(DataGrid grid)
    {
        if (Followers.TryGetValue(grid, out var old))
        {
            old.Stop();
            Followers.Remove(grid);
        }
        if (GetFollowMoves(grid) && grid.ItemsSource is INotifyCollectionChanged source)
            Followers.Add(grid, new Follower(grid, source));
    }

    private sealed class Follower
    {
        private readonly DataGrid _grid;
        private readonly INotifyCollectionChanged _source;
        private bool _pending;
        private bool _listening;

        public Follower(DataGrid grid, INotifyCollectionChanged source)
        {
            _grid = grid;
            _source = source;
            _grid.AttachedToVisualTree += OnAttached;
            _grid.DetachedFromVisualTree += OnDetached;
            if (_grid.IsAttachedToVisualTree()) Listen();
        }

        public void Stop()
        {
            _grid.AttachedToVisualTree -= OnAttached;
            _grid.DetachedFromVisualTree -= OnDetached;
            Unlisten();
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            Listen();
            Schedule(); // сдвиги, пока таблицы не было в окне, прошли мимо неё
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Unlisten();

        private void Listen()
        {
            if (_listening) return;
            _listening = true;
            _source.CollectionChanged += OnChanged;
        }

        private void Unlisten()
        {
            if (!_listening) return;
            _listening = false;
            _source.CollectionChanged -= OnChanged;
        }

        private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Move) Schedule();
        }

        private void Schedule()
        {
            if (_pending) return;
            _pending = true;
            // выше отрисовки: кадр с неверным порядком не успевает показаться
            Dispatcher.UIThread.Post(Sync, DispatcherPriority.Normal);
        }

        private void Sync()
        {
            _pending = false;
            if (_grid.CollectionView is { } view && _grid.ItemsSource is { } source && !SameOrder(view, source)) view.Refresh();
        }

        private static bool SameOrder(IEnumerable shown, IEnumerable source)
        {
            var a = shown.GetEnumerator();
            var b = source.GetEnumerator();
            while (true)
            {
                var more = a.MoveNext();
                if (more != b.MoveNext()) return false;
                if (!more) return true;
                if (!ReferenceEquals(a.Current, b.Current)) return false;
            }
        }
    }
}
