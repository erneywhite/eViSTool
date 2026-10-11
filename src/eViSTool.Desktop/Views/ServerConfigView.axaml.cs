using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>
/// Редактор serverconfig.json в разделе «Сервер». DataContext — ServerConfigViewModel. Здесь — только то, что касается
/// вида: переход к полю (прокрутка и курсор) и перечитывание файла при возврате на страницу. Колесо над выпадающими
/// списками отдаёт прокрутку странице через ctl:WheelToParent в разметке. Подписок на закрытие окна нет: о
/// несохранённом спрашивает модель (ConfirmClose через MainViewModel.ConfirmExit).
/// </summary>
public partial class ServerConfigView : UserControl
{
    private ServerConfigViewModel? _vm;
    private MainViewModel? _main;

    public ServerConfigView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.FocusFieldRequested -= FocusField;
        _vm = DataContext as ServerConfigViewModel;
        if (_vm is not null) _vm.FocusFieldRequested += FocusField;
        WatchPage(); // DataContext приходит от главного окна, когда у него уже есть своя модель
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WatchPage();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_main is not null) _main.PropertyChanged -= OnMainChanged;
        _main = null;
    }

    // ---------- вернулись на страницу «Сервер» ----------

    // Пока была открыта другая страница (моды, настройки), файл могли поменять — например, выключили мод на вкладке
    // «Моды». Вернулись — модель покажет свежее, если своих правок нет (и если открыта именно «Конфигурация»: это она
    // проверяет сама). Смену вкладок раздела «Сервер» модель ловит сама (ServerViewModel.Tab → SetActive).
    private void WatchPage()
    {
        var main = TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;
        if (ReferenceEquals(main, _main)) return;
        if (_main is not null) _main.PropertyChanged -= OnMainChanged;
        _main = main;
        if (_main is not null) _main.PropertyChanged += OnMainChanged;
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab) && _main?.SelectedTab == AppTab.Server) _vm?.RefreshIfChangedOnDisk();
    }

    // ---------- переход к полю ----------

    /// <summary>
    /// Прокрутить к полю и поставить в него курсор (пришли из «Объявлений» по «Изменить» у приветствия). Вкладку
    /// модель уже переключила; поля могут быть ещё не нарисованы (а конфиг — ещё грузиться): тогда пробуем снова,
    /// пока поле не появится, но не дольше нескольких секунд.
    /// </summary>
    private void FocusField(string path)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        void Attempt()
        {
            if (FindField(path) is { } row)
            {
                row.BringIntoView();
                if (row.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box)
                {
                    box.Focus();
                    box.ClearSelection();
                    box.CaretIndex = box.Text?.Length ?? 0;
                }
                return;
            }
            if (DateTime.UtcNow < until) DispatcherTimer.RunOnce(Attempt, TimeSpan.FromMilliseconds(50));
        }
        // после разметки: вкладка только что стала видимой
        Dispatcher.UIThread.Post(Attempt, DispatcherPriority.Loaded);
    }

    /// <summary>Строка поля на видимой вкладке: контейнер списка, у которого содержимое — модель этого поля.</summary>
    private ContentPresenter? FindField(string path) =>
        this.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Content is ConfigFieldViewModel field && field.Path == path && p.IsEffectivelyVisible && p.Child is not null);
}
