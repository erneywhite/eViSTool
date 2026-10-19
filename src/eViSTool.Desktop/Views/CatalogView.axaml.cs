using Avalonia.Controls;
using Avalonia.Input;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>Вкладка «Каталог»: поиск по модбазе, плитки результатов и карточка выбранного мода.</summary>
public partial class CatalogView : UserControl
{
    private CatalogViewModel? _vm;

    private ColumnDefinition DetailsColumn => Split.ColumnDefinitions[2]; // колонка карточки справа от разделителя

    public CatalogView()
    {
        InitializeComponent();
        Split.SizeChanged += (_, _) => FitDetails();
        Filters.SizeChanged += (_, e) => Filters.Columns = e.NewSize.Width >= FiltersInThreeFrom ? 3 : 2;
    }

    /// <summary>С этой ширины ряда фильтры — по три: «Популярные сейчас» с шевроном помещается в треть.</summary>
    internal const double FiltersInThreeFrom = 570;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as CatalogViewModel;
        FitDetails(); // ширина карточки — как в прошлый раз, если помещается
    }

    /// <summary>Списку результатов — не уже этого, пока окно позволяет: карточка ужимается раньше.</summary>
    internal const double ListRoom = 420;
    internal const double ListMin = 300;

    /// <summary>
    /// Ширина карточки — сохранённая (<see cref="CatalogViewModel.DetailsWidth"/>), но не шире, чем оставляет окно: иначе в
    /// окне уже ~1400 px правый край карточки (кнопки «Установить», «Поставить эту») уезжал за окно. Расширили окно —
    /// карточка вернулась к своей ширине; сохранённую меняет только разделитель. Как FitCard у «Моих модов».
    /// </summary>
    private void FitDetails()
    {
        if (_vm is null) return;
        var gap = Split.ColumnDefinitions[1].Width.Value;
        var room = Split.Bounds.Width - ListRoom - gap;
        var width = room > 0 ? Math.Min(_vm.DetailsWidth, room) : _vm.DetailsWidth;
        width = Math.Max(width, DetailsColumn.MinWidth);
        if (Math.Abs(DetailsColumn.Width.Value - width) > 0.5) DetailsColumn.Width = new GridLength(width);
        if (Split.Bounds.Width > 0)
            Split.ColumnDefinitions[0].MinWidth = Math.Max(ListMin, Math.Min(ListRoom, Split.Bounds.Width - gap - DetailsColumn.MinWidth));
    }

    private void Splitter_DragCompleted(object? sender, VectorEventArgs e)
    {
        if (_vm is null) return;
        _vm.DetailsWidth = DetailsColumn.ActualWidth;
        _vm.SaveLayout();
    }
}
