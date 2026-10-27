using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using eViSTool.Desktop.Controls;
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
        // правый клик выделяет строку под курсором до открытия меню — меню действует на неё
        Grid.AddHandler(PointerPressedEvent, Grid_PointerPressed, RoutingStrategies.Tunnel);
        // перетаскивание zip-архивов из Проводника (Nautilus, Dolphin) в список модов
        Grid.AddHandler(DragDrop.DragOverEvent, Grid_DragOver);
        Grid.AddHandler(DragDrop.DropEvent, Grid_Drop);
        LeftPane.SizeChanged += (_, e) => ApplyCompact(e.NewSize.Width);
        // группы «По категориям»: подпись заголовка и свёрнутость, которую помнит профиль
        Grid.LoadingRowGroup += (_, e) => SetUpGroupHeader(e.RowGroupHeader);
        Grid.Loaded += (_, _) => ApplyGrouping();
        Split.SizeChanged += (_, _) => FitCard();
        // ширину карточки запоминает только сдвинутый разделитель, не щелчок по нему
        SplitterMemory.Remember(Splitter, CardColumn, width =>
        {
            if (_vm is null) return;
            _vm.CardWidth = width;
            _vm.SaveLayout();
        });
    }

    /// <summary>
    /// Левая часть уже этого — кнопки над таблицей теряют подписи (остаются иконки с подсказками), колонка «Сторона»
    /// прячется: иначе при сильно суженном окне поле поиска и название мода сжимались почти в ноль.
    /// </summary>
    internal const double CompactBelow = 640;

    private void ApplyCompact(double width)
    {
        var compact = width < CompactBelow;
        LeftPane.Classes.Set("compact", compact);
        foreach (var column in Grid.Columns.Where(c => c.SortMemberPath == "SideText")) column.IsVisible = !compact; // «Сторона»
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as ModsViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmChanged;
        FitCard(); // ширина карточки — как в прошлый раз, если помещается
        Dispatcher.UIThread.Post(ApplyGrouping); // вид таблицы создаётся вместе с её строками
    }

    // выбрали порядок в списке «Сначала важное / По имени» — сортировка кликом по заголовку колонки уступает ему
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModsViewModel.SortIndex)) Grid.CollectionView?.SortDescriptions.Clear();
        if (e.PropertyName == nameof(ModsViewModel.GroupByCategory)) ApplyGrouping();
    }

    /// <summary>
    /// «По категориям» — группы таблицы по <see cref="ModRowViewModel.GroupName"/>. Порядок групп и строк в них задаёт
    /// порядок строк модели (сперва по категории), сам вид таблицы не сортирует.
    /// </summary>
    private void ApplyGrouping()
    {
        if (_vm is null || Grid.CollectionView is not DataGridCollectionView view) return;
        var grouped = view.GroupDescriptions.Count > 0;
        if (grouped == _vm.GroupByCategory) return;
        view.GroupDescriptions.Clear();
        if (_vm.GroupByCategory) view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(ModRowViewModel.GroupName)));
    }

    // заголовки групп таблица переиспользует для разных групп: подписка — один раз на заголовок, группа — на момент щелчка
    private readonly HashSet<DataGridRowGroupHeader> _groupHeaders = [];

    private void SetUpGroupHeader(DataGridRowGroupHeader header)
    {
        header.IsPropertyNameVisible = false;
        header.ItemCountFormat = "({0})";
        header.IsItemCountVisible = true;
        if (_groupHeaders.Add(header))
            header.AddHandler(ToggleButton.IsCheckedChangedEvent, (_, e) =>
            {
                if (e.Source is ToggleButton toggle && header.DataContext is DataGridCollectionViewGroup { Key: string key })
                    _vm?.SetCategoryCollapsed(key, toggle.IsChecked != true);
            });
        // таблица перестроена (операция с модами, смена порядка) — свёрнутая группа остаётся свёрнутой
        if (header.DataContext is DataGridCollectionViewGroup { Key: string group } g && _vm?.IsCategoryCollapsed(group) == true)
            Dispatcher.UIThread.Post(() => Grid.CollapseRowGroup(g, collapseAllSubgroups: false));
    }

    // категория в карточке: выбрали из списка или нажали Enter — записать сразу, не дожидаясь ухода из поля
    private void CardCategory_DropDownClosed(object? sender, EventArgs e) => CommitCategory(sender);

    private void CardCategory_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitCategory(sender);
        e.Handled = true;
    }

    private static void CommitCategory(object? sender)
    {
        if (sender is ComboBox { DataContext: ModCardViewModel card } box && box.Text is { } text && text != card.Category) card.Category = text;
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

    /// <summary>
    /// Ширина карточки — сохранённая (<see cref="ModsViewModel.CardWidth"/>), но не шире, чем оставляет окно: таблице
    /// нужно <see cref="TableRoom"/>, иначе название мода сжимается в ноль. В узком окне карточка временно уже (до своего
    /// минимума), расширили окно — вернулась к своей ширине. Сохранённую ширину меняет только разделитель.
    /// </summary>
    internal const double TableRoom = 520; // таблица с колонкой названия не уже её минимума
    internal const double TableMin = 300; // самое узкое окно (860): 300 + 14 + 280 помещаются в 598 px страницы

    private void FitCard()
    {
        if (_vm is null) return;
        var gap = Split.ColumnDefinitions[1].Width.Value;
        var room = Split.Bounds.Width - TableRoom - gap;
        var width = room > 0 ? Math.Min(_vm.CardWidth, room) : _vm.CardWidth;
        width = Math.Max(width, CardColumn.MinWidth);
        if (Math.Abs(CardColumn.Width.Value - width) > 0.5) CardColumn.Width = new GridLength(width);
        // и разделитель не пускает таблицу уже TableRoom (пока окно это позволяет): иначе оставленная им ширина держалась
        // бы только до первого изменения размера окна, а потом карточка сама сузилась бы
        if (Split.Bounds.Width > 0)
            Split.ColumnDefinitions[0].MinWidth = Math.Max(TableMin, Math.Min(TableRoom, Split.Bounds.Width - gap - CardColumn.MinWidth));
    }
}
