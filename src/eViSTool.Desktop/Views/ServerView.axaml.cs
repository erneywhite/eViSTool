using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>
/// Раздел «Сервер»: управление сервером профиля, вкладки раздела и консоль. Здесь — только то, что касается вида:
/// прокрутка консоли, клавиши и каретка поля команды, фокус. Подписок на закрытие окна нет: о несохранённом
/// спрашивает MainViewModel.ConfirmExit.
/// </summary>
public partial class ServerView : UserControl
{
    private ServerViewModel? _vm;
    private readonly ConsoleAutoScroll _scroll;

    public ServerView()
    {
        InitializeComponent();
        _scroll = new ConsoleAutoScroll(ConsoleList);
        // на спуске: свои клавиши поля (Enter, стрелки, Tab) — раньше самого TextBox и перехода фокуса по Tab
        CommandBox.AddHandler(KeyDownEvent, CommandBox_KeyDown, RoutingStrategies.Tunnel);
        CommandBox.LostFocus += CommandBox_LostFocus;
        // щелчок по подсказке: пункт выделяется сам и событие помечает обработанным — берём и такие
        SuggestionList.AddHandler(PointerReleasedEvent, SuggestionList_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.LinesAppended -= OnLinesAppended;
            _vm.CommandInserted -= OnCommandInserted;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = DataContext as ServerViewModel;
        if (_vm is null) return;
        _vm.LinesAppended += OnLinesAppended;
        _vm.CommandInserted += OnCommandInserted;
        _vm.PropertyChanged += OnVmChanged;
    }

    // пока была открыта другая вкладка (или другой раздел), строки копились без прокрутки — вернулись к консоли, догоняем
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerViewModel.IsConsoleTab) && _vm?.IsConsoleTab == true) OnLinesAppended(force: false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>()) OnLinesAppended(force: false);
    }

    // ---------- консоль: прилипает к низу, как терминал, пока пользователь сам не отмотал вверх ----------

    private void OnLinesAppended(bool force) => _scroll.Appended(force);

    // ---------- поле команды ----------

    private void CommandBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            // Tab дописывает команду и не уводит фокус из поля
            case Key.Tab:
                _vm.CompleteCommand();
                CaretToEnd();
                e.Handled = true;
                break;
            case Key.Escape when _vm.ShowSuggestions:
                _vm.HideSuggestions();
                e.Handled = true;
                break;
            // открыты подсказки — стрелки ходят по ним, Enter берёт выбранную; иначе стрелки — история
            case Key.Up when _vm.ShowSuggestions:
                _vm.MoveSuggestion(-1);
                ShowSelectedSuggestion();
                e.Handled = true;
                break;
            case Key.Down when _vm.ShowSuggestions:
                _vm.MoveSuggestion(+1);
                ShowSelectedSuggestion();
                e.Handled = true;
                break;
            case Key.Enter when _vm.ShowSuggestions && _vm.SelectedSuggestion is { } chosen:
                _vm.InsertCommand(chosen);
                CaretToEnd();
                e.Handled = true;
                break;
            case Key.Enter:
                if (_vm.SendCommandCommand.CanExecute(null)) _vm.SendCommandCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                _vm.HistoryStep(-1);
                CaretToEnd();
                e.Handled = true;
                break;
            case Key.Down:
                _vm.HistoryStep(+1);
                CaretToEnd();
                e.Handled = true;
                break;
        }
    }

    private void ShowSelectedSuggestion()
    {
        if (_vm?.SelectedSuggestion is { } chosen) SuggestionList.ScrollIntoView(chosen);
    }

    // щелчок по подсказке — команда в поле, фокус остаётся в поле ввода
    private void SuggestionList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is null || e.InitialPressMouseButton != MouseButton.Left
            || (e.Source as StyledElement)?.DataContext is not ServerCommand command) return;
        _vm.InsertCommand(command);
        FocusCommandBox();
    }

    // ушли из поля (не в список подсказок) — список прячется
    private void CommandBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (!SuggestionList.IsKeyboardFocusWithin) _vm?.HideSuggestions();
    }

    // команда выбрана в окне «Команды»: фокус — когда главное окно снова активно после диалога
    private void OnCommandInserted(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(FocusCommandBox, DispatcherPriority.Background);

    private void FocusCommandBox()
    {
        CommandBox.Focus();
        CaretToEnd();
    }

    private void CaretToEnd()
    {
        CommandBox.ClearSelection();
        CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
    }
}
