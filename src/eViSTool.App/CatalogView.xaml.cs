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

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e) => (DataContext as CatalogViewModel)?.SaveLayout();
}
