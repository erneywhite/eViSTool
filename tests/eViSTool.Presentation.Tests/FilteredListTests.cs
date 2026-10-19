using System.Collections.ObjectModel;
using System.Collections.Specialized;
using eViSTool.Presentation;

namespace eViSTool.Presentation.Tests;

/// <summary>Вид списка без оконного движка: фильтр, устойчивая сортировка, обновление сдвигом (те же объекты, без очистки).</summary>
public sealed class FilteredListTests
{
    private sealed record Mod(string Name, int Importance);

    private static ObservableCollection<Mod> Source() =>
        [new("Carry On", 2), new("aaa", 1), new("Bbb", 1), new("Ccc", 0)];

    [Fact]
    public void FiltersAndSorts_Stably()
    {
        var source = Source();
        var list = new FilteredList<Mod>(source, m => m.Importance > 0, (a, b) => a.Importance.CompareTo(b.Importance));
        Assert.Equal(["aaa", "Bbb", "Carry On"], list.Items.Select(m => m.Name)); // равные — в исходном порядке
    }

    [Fact]
    public void FollowsTheSource_AndRefreshesOnNewConditions()
    {
        var source = Source();
        var search = "";
        var list = new FilteredList<Mod>(source, m => m.Name.Contains(search, StringComparison.OrdinalIgnoreCase),
            (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));

        source.Add(new Mod("Abc", 3));
        Assert.Equal(["aaa", "Abc", "Bbb", "Carry On", "Ccc"], list.Items.Select(m => m.Name));

        search = "c";
        list.Refresh();
        Assert.Equal(["Abc", "Carry On", "Ccc"], list.Items.Select(m => m.Name));

        source.Clear();
        Assert.Empty(list.Items);
    }

    [Fact]
    public void Refresh_MovesItems_InsteadOfClearing()
    {
        var source = Source();
        var byImportance = true;
        var list = new FilteredList<Mod>(source, null,
            (a, b) => byImportance ? a.Importance.CompareTo(b.Importance) : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        var kept = list.Items[1];
        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, e) => actions.Add(e.Action);

        byImportance = false;
        list.Refresh();

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions); // выделение и прокрутка в таблице не сбросятся
        Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, actions);
        Assert.Contains(list.Items, m => ReferenceEquals(m, kept)); // тот же объект, не копия
        Assert.Equal(["aaa", "Bbb", "Carry On", "Ccc"], list.Items.Select(m => m.Name));
    }

    /// <summary>
    /// Исходную коллекцию построили заново новыми объектами (таблица модов после операции): вид не пустеет ни на миг —
    /// новые встают на места прежних заменой, лишние уходят в конце. Опустей он, таблица сбросила бы прокрутку к началу.
    /// </summary>
    [Fact]
    public void RebuiltSource_ReplacesInPlace_WithoutEmptying()
    {
        var source = Source();
        var list = new FilteredList<Mod>(source, null, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var kept = list.Items[0];
        var counts = new List<int>();
        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, e) =>
        {
            actions.Add(e.Action);
            counts.Add(list.Items.Count);
        };

        using (list.DeferRefresh())
        {
            source.Clear();
            source.Add(kept); // эта строка не изменилась — тот же объект
            source.Add(new Mod("Bbb", 1));
            source.Add(new Mod("Ccc", 0));
        }

        Assert.Equal(["aaa", "Bbb", "Ccc"], list.Items.Select(m => m.Name));
        Assert.Same(kept, list.Items[0]);
        Assert.All(counts, c => Assert.True(c >= 3, $"вид опустел до {c}"));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Contains(NotifyCollectionChangedAction.Replace, actions);
        Assert.Equal(NotifyCollectionChangedAction.Remove, actions[^1]); // лишний («Carry On») — в конце
    }

    [Fact]
    public void DeferRefresh_OneUpdateAtTheEnd()
    {
        var list = new FilteredList<Mod>(Source());
        var changes = 0;
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) => changes++;

        using (list.DeferRefresh())
        {
            list.Filter = m => m.Importance == 1;
            Assert.Equal(0, changes); // пока отложено — ничего не меняется
            list.Sort = (a, b) => string.Compare(b.Name, a.Name, StringComparison.CurrentCulture);
        }

        Assert.Equal(["Bbb", "aaa"], list.Items.Select(m => m.Name));
    }
}
