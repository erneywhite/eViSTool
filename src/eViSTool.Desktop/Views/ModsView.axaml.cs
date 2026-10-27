using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.LogicalTree;
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
        Grid.AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        {
            MarkDropTarget(null);
            DragBadge.IsVisible = false;
        });
        // «По категориям»: мод перетаскивают на заголовок группы или на строку в ней
        Grid.AddHandler(PointerMovedEvent, Grid_PointerMoved, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel);
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
    internal const double CompactBelow = 760; // кнопки над таблицей (с «Категориями») ~560 + поиск не уже 150

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

    // плашка категории в карточке: выбрали категорию — список закрывается, мод — в неё. Команда — из самого пункта, не из
    // привязки кнопки: закрытый список отвязан от модели, а смена категории перестраивает его пункты
    private void CategoryPick_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CategoryChoice choice } button) return;
        e.Handled = true;
        CloseFlyout(button);
        choice.Command?.Execute(null);
    }

    // новая категория: вписали и нажали Enter — мод в неё, список закрывается
    private void CategoryNew_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { DataContext: ModCardViewModel card } box) return;
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(box.Text)) return;
        card.Category = box.Text;
        box.Text = "";
        CloseFlyout(box);
    }

    private static void CloseFlyout(Control? inside)
    {
        if (inside?.FindLogicalAncestorOfType<Popup>() is { } popup) popup.IsOpen = false;
    }

    private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = null;
        if (e.GetCurrentPoint(Grid).Properties.IsLeftButtonPressed)
        {
            // нажатие на строку (не на переключатель «Вкл») — возможно, начало перетаскивания в группу
            if (_vm is { GroupByCategory: true } && e.Source is Visual source && source.FindAncestorOfType<ToggleButton>(includeSelf: true) is null
                && source.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is ModRowViewModel dragged)
                _dragStart = (e, dragged, e.GetPosition(Grid));
            return;
        }
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
        if (e.DataTransfer.TryGetValue(ModRowFormat) is { } dragged)
        {
            var target = DropTarget(e.Source as Visual);
            // над строкой группы подсвечивается её заголовок: видно, куда ляжет мод
            MarkDropTarget(target?.Header ?? (target is { } t ? HeaderOf(t.Category) : null));
            ShowDragBadge(e, dragged, target?.Category);
            e.DragEffects = target is null ? DragDropEffects.None : DragDropEffects.Move;
        }
        else e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Grid_Drop(object? sender, DragEventArgs e)
    {
        MarkDropTarget(null);
        DragBadge.IsVisible = false;
        if (e.DataTransfer.TryGetValue(ModRowFormat) is { } row)
        {
            if (DropTarget(e.Source as Visual) is { } target) _vm?.SetCategory(row, target.Category);
            return;
        }
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (files is { Count: > 0 }) _ = _vm?.AddFilesAsync(files);
    }

    // ---------- перетаскивание мода в группу ----------

    /// <summary>Перетаскиваемая строка — объектом внутри окна (наружу, в другие программы, не уходит).</summary>
    private static readonly DataFormat<ModRowViewModel> ModRowFormat = DataFormat.CreateInProcessFormat<ModRowViewModel>("evistool-mod-row");

    /// <summary>Сдвиг мыши, после которого нажатие на строку — уже перетаскивание, а не щелчок.</summary>
    private const double DragThreshold = 6;

    private (PointerPressedEventArgs Press, ModRowViewModel Row, Point At)? _dragStart;
    private DataGridRowGroupHeader? _dropHeader;

    private void Grid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || !e.GetCurrentPoint(Grid).Properties.IsLeftButtonPressed) return;
        var at = e.GetPosition(Grid);
        if (Math.Abs(at.X - start.At.X) < DragThreshold && Math.Abs(at.Y - start.At.Y) < DragThreshold) return;
        _dragStart = null;
        _ = DragAsync(start.Press, start.Row);
    }

    private async Task DragAsync(PointerPressedEventArgs press, ModRowViewModel row)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ModRowFormat, row));
        var source = Rows().FirstOrDefault(r => r.DataContext == row);
        source?.Classes.Add("dragSource");
        try
        {
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            source?.Classes.Remove("dragSource");
            Rows().FirstOrDefault(r => r.Classes.Contains("dragSource"))?.Classes.Remove("dragSource"); // строку могли переиспользовать
            MarkDropTarget(null);
            DragBadge.IsVisible = false;
        }
    }

    private IEnumerable<DataGridRow> Rows() => Grid.GetVisualDescendants().OfType<DataGridRow>();

    /// <summary>Плашка у курсора: какой мод тащат и в какую группу он ляжет.</summary>
    private void ShowDragBadge(DragEventArgs e, ModRowViewModel row, string? category)
    {
        DragBadgeText.Text = category is null ? row.Name
            : $"{row.Name}  →  {(category.Length == 0 ? ModsViewModel.NoCategoryLabel : category)}";
        var at = e.GetPosition(DragLayer);
        Canvas.SetLeft(DragBadge, at.X + 16);
        Canvas.SetTop(DragBadge, at.Y + 12);
        DragBadge.IsVisible = true;
    }

    /// <summary>
    /// Куда упадёт мод: заголовок группы — её категория, строка — категория её мода («Без категории» — пустая). Не в
    /// таблице «По категориям» — некуда.
    /// </summary>
    internal (string Category, DataGridRowGroupHeader? Header)? DropTarget(Visual? over)
    {
        if (_vm is not { GroupByCategory: true } || over is null) return null;
        if (over.FindAncestorOfType<DataGridRowGroupHeader>(includeSelf: true) is { DataContext: DataGridCollectionViewGroup { Key: string key } } header)
            return (key == ModsViewModel.NoCategoryLabel ? "" : key, header);
        if (over.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is ModRowViewModel row)
            return (row.Category, null);
        return null;
    }

    /// <summary>Видимый заголовок группы этой категории («» — «Без категории»); группа свёрнута за краем — null.</summary>
    private DataGridRowGroupHeader? HeaderOf(string category)
    {
        var key = category.Length == 0 ? ModsViewModel.NoCategoryLabel : category;
        return Grid.GetVisualDescendants().OfType<DataGridRowGroupHeader>()
            .FirstOrDefault(h => h.IsVisible && h.DataContext is DataGridCollectionViewGroup { Key: string k } && k == key);
    }

    // заголовок группы, над которым держат мод, — подсвечен
    private void MarkDropTarget(DataGridRowGroupHeader? header)
    {
        if (ReferenceEquals(header, _dropHeader)) return;
        _dropHeader?.Classes.Remove("dropTarget");
        _dropHeader = header;
        _dropHeader?.Classes.Add("dropTarget");
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
