using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    public ServerView()
    {
        InitializeComponent();
        // на спуске: свои клавиши поля (Enter, стрелки, Tab) — раньше самого TextBox и перехода фокуса по Tab
        CommandBox.AddHandler(KeyDownEvent, CommandBox_KeyDown, RoutingStrategies.Tunnel);
        CommandBox.LostFocus += CommandBox_LostFocus;
        // щелчок по подсказке: пункт выделяется сам и событие помечает обработанным — берём и такие
        SuggestionList.AddHandler(PointerReleasedEvent, SuggestionList_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        // прокрутку консоли меняет и пользователь, и рост списка — ScrollChanged доходит сюда из прокрутки списка
        ConsoleList.AddHandler(ScrollViewer.ScrollChangedEvent, ConsoleScroll_Changed);
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

    private bool _stick = true;
    private bool _endPosted;

    private ScrollViewer? Scroll => ConsoleList.FindDescendantOfType<ScrollViewer>();

    private void OnLinesAppended(bool force)
    {
        if (force) _stick = true;
        if (_stick) ScrollToEndLater();
    }

    // докручиваем после разметки: до неё высота списка ещё старая (особенно с переносом длинных строк)
    private void ScrollToEndLater()
    {
        if (_endPosted) return;
        _endPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _endPosted = false;
            if (_stick) Scroll?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    // Внизу — прилипли. Позиция ушла вверх или сдвинулась без изменения высоты — это крутил пользователь: отлипли.
    // Высота выросла, пока прилипли (новые строки, строки досчитались при прокрутке виртуального списка, окно сжали) —
    // догоняем низ. Сами мы прокручиваем только вниз, к концу, поэтому сдвиг вверх — всегда человек.
    private void ConsoleScroll_Changed(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer sv) return;
        var bottom = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (sv.Offset.Y >= bottom - 1) _stick = true;
        else if (e.OffsetDelta.Y < 0 || (e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0)) _stick = false;
        else if (_stick) ScrollToEndLater();
    }

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
