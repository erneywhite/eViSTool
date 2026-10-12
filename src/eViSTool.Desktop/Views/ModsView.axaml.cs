using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>Вкладка «Мои моды»: таблица модов профиля и карточка выбранного.</summary>
public partial class ModsView : UserControl
{
    private ModsViewModel? _vm;
    private bool? _rightClickOnRow; // последний правый щелчок по таблице пришёлся на строку мода (null — меню с клавиатуры)

    private ColumnDefinition CardColumn => Split.ColumnDefinitions[2]; // колонка карточки справа от разделителя

    public ModsView()
    {
        InitializeComponent();
        // правый клик выделяет строку под курсором до открытия меню — меню действует на неё (как в таблице WPF)
        Grid.AddHandler(PointerPressedEvent, Grid_PointerPressed, RoutingStrategies.Tunnel);
        // перетаскивание zip-архивов из Проводника (Nautilus, Dolphin) в список модов
        Grid.AddHandler(DragDrop.DragOverEvent, Grid_DragOver);
        Grid.AddHandler(DragDrop.DropEvent, Grid_Drop);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ModsViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmChanged;
        CardColumn.Width = new GridLength(_vm.CardWidth); // ширина карточки — как в прошлый раз
    }

    // выбрали порядок в списке «Сначала важное / По имени» — сортировка кликом по заголовку колонки уступает ему
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModsViewModel.SortIndex)) Grid.CollectionView?.SortDescriptions.Clear();
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Grid).Properties.IsRightButtonPressed) return;
        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as ModRowViewModel;
        _rightClickOnRow = row is not null;
        if (row is not null) Grid.SelectedItem = row;
    }

    // меню действий с модом — только над строкой мода: над заголовком колонки или пустым местом оно действовало бы на
    // мод, выбранный раньше. С клавиатуры — если мод выбран
    private void RowMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (_rightClickOnRow == false || (_rightClickOnRow is null && _vm?.Selected is null)) e.Cancel = true;
        _rightClickOnRow = null;
    }

    private void Grid_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Grid_Drop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (files is { Count: > 0 }) _ = _vm?.AddFilesAsync(files);
    }

    private void Splitter_DragCompleted(object? sender, VectorEventArgs e)
    {
        if (_vm is null) return;
        _vm.CardWidth = CardColumn.ActualWidth;
        _vm.SaveLayout();
    }
}
