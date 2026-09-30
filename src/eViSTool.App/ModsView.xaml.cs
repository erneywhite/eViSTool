using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using eViSTool.App.ViewModels;

namespace eViSTool.App;

/// <summary>Вкладка «Мои моды»: таблица модов профиля и карточка выбранного.</summary>
public partial class ModsView : UserControl
{
    public ModsView() => InitializeComponent();

    private ModsViewModel? Vm => DataContext as ModsViewModel;

    // «Модпак ▾»: меню открывается обычным кликом
    private void PackMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // перетаскивание zip-архивов из Проводника в список модов
    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            Vm?.AddFiles(files);
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e) => Vm?.SaveLayout();
}
