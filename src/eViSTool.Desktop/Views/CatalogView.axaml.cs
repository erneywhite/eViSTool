using Avalonia.Controls;
using Avalonia.Input;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>Вкладка «Каталог»: поиск по модбазе, плитки результатов и карточка выбранного мода.</summary>
public partial class CatalogView : UserControl
{
    private CatalogViewModel? _vm;

    private ColumnDefinition DetailsColumn => Split.ColumnDefinitions[2]; // колонка карточки справа от разделителя

    public CatalogView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as CatalogViewModel;
        if (_vm is not null) DetailsColumn.Width = new GridLength(_vm.DetailsWidth); // ширина карточки — как в прошлый раз
    }

    private void Splitter_DragCompleted(object? sender, VectorEventArgs e)
    {
        if (_vm is null) return;
        _vm.DetailsWidth = DetailsColumn.ActualWidth;
        _vm.SaveLayout();
    }
}
