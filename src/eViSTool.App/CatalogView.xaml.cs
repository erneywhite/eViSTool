using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using eViSTool.App.ViewModels;

namespace eViSTool.App;

public partial class CatalogView : UserControl
{
    public CatalogView()
    {
        InitializeComponent();
    }

    private void ModsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModsList.SelectedItem is { } item) ModsList.ScrollIntoView(item);
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e) => (DataContext as CatalogViewModel)?.SaveLayout();
}
