using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.App;

/// <summary>Вкладка «Мои моды»: таблица модов профиля и карточка выбранного.</summary>
public partial class ModsView : UserControl
{
    public ModsView()
    {
        InitializeComponent();
        // выбрали порядок в списке «Сначала важное / По имени» — сортировка кликом по заголовку колонки уступает ему
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ModsViewModel old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is ModsViewModel now) now.PropertyChanged += OnVmChanged;
        };
    }

    private ModsViewModel? Vm => DataContext as ModsViewModel;

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModsViewModel.SortIndex)) return;
        Grid.Items.SortDescriptions.Clear();
        foreach (var column in Grid.Columns) column.SortDirection = null;
    }

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

    // меню действий с модом — только над строкой мода: над заголовком колонки или пустым местом оно действовало бы на
    // мод, выбранный раньше
    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is DataGridRow) return;
            if (d is DataGrid) break;
        }
        e.Handled = true;
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
            _ = Vm?.AddFilesAsync(files);
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e) => Vm?.SaveLayout();
}
