using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace eViSTool.Presentation;

/// <summary>
/// Отфильтрованный и упорядоченный вид исходной коллекции — одинаковый для любого
/// оконного движка. <see cref="Items"/> привязывается к списку или таблице. Обновляется не очисткой, а сдвигом
/// элементов на свои места: те же объекты остаются в списке, и выделенная строка с прокруткой не сбрасываются.
/// Исходная коллекция поменялась — вид обновляется сам; поменялись условия фильтра — <see cref="Refresh"/>.
/// </summary>
public sealed class FilteredList<T> where T : class
{
    private readonly IReadOnlyList<T> _source;
    private readonly ObservableCollection<T> _items = [];
    private Predicate<T>? _filter;
    private Comparison<T>? _sort;
    private int _deferred;

    public FilteredList(ObservableCollection<T> source, Predicate<T>? filter = null, Comparison<T>? sort = null)
    {
        _source = source;
        _filter = filter;
        _sort = sort;
        Items = new ReadOnlyObservableCollection<T>(_items);
        source.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>То, что видно: прошедшие фильтр, в нужном порядке.</summary>
    public ReadOnlyObservableCollection<T> Items { get; }

    public int Count => _items.Count;

    /// <summary>
    /// Вид сейчас переставляет строки. Список окна, у которого выделена сдвигаемая строка, на время сдвига теряет
    /// выделение и сообщает «ничего не выбрано» — модели в это время такой выбор принимать не надо.
    /// </summary>
    public bool IsRefreshing { get; private set; }

    /// <summary>Строки переставлены (или не понадобилось): модель может вернуть выделение спискам окна.</summary>
    public event Action? Refreshed;

    public Predicate<T>? Filter
    {
        get => _filter;
        set { _filter = value; Refresh(); }
    }

    /// <summary>Порядок; null — как в исходной коллекции. Сортировка устойчивая: равные остаются в исходном порядке.</summary>
    public Comparison<T>? Sort
    {
        get => _sort;
        set { _sort = value; Refresh(); }
    }

    /// <summary>Несколько изменений условий подряд — одно обновление в конце.</summary>
    public IDisposable DeferRefresh()
    {
        _deferred++;
        return new Deferral(this);
    }

    private sealed class Deferral(FilteredList<T> list) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--list._deferred == 0) list.Refresh();
        }
    }

    /// <summary>Пересчитать вид: условия фильтра или ключи сортировки поменялись.</summary>
    public void Refresh()
    {
        if (_deferred > 0) return;
        IEnumerable<T> wanted = _filter is { } filter ? _source.Where(x => filter(x)) : _source;
        var desired = _sort is { } sort
            ? wanted.Select((item, index) => (item, index)).OrderBy(x => x, new Stable(sort)).Select(x => x.item).ToList()
            : wanted.ToList();

        IsRefreshing = true;
        try
        {
            Apply(desired);
        }
        finally
        {
            IsRefreshing = false;
        }
        Refreshed?.Invoke();
    }

    private void Apply(List<T> desired)
    {
        // оставшиеся — сдвигом на свои места; недостающий встаёт на место лишнего (замена), а нет такого — вставкой;
        // остальные лишние — вон в конце. Лишние не убираются заранее: исходную коллекцию построили заново (таблица
        // модов после операции — все строки новые), и вид, опустев хоть на миг, сбросил бы прокрутку таблицы к началу
        var keep = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _items.Count && ReferenceEquals(_items[i], desired[i])) continue;
            var at = IndexOf(desired[i], i + 1);
            if (at >= 0) _items.Move(at, i);
            else if (i < _items.Count && !keep.Contains(_items[i])) _items[i] = desired[i];
            else _items.Insert(i, desired[i]);
        }
        for (var i = _items.Count - 1; i >= desired.Count; i--) _items.RemoveAt(i);
    }

    private int IndexOf(T item, int from)
    {
        for (var i = from; i < _items.Count; i++)
            if (ReferenceEquals(_items[i], item)) return i;
        return -1;
    }

    private sealed class Stable(Comparison<T> sort) : IComparer<(T item, int index)>
    {
        public int Compare((T item, int index) a, (T item, int index) b)
        {
            var c = sort(a.item, b.item);
            return c != 0 ? c : a.index.CompareTo(b.index);
        }
    }
}
