using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using eViSTool.App.ViewModels;
using eViSTool.Core.Server;

namespace eViSTool.App;

public partial class ServerView : UserControl
{
    private ServerViewModel? _vm;
    private Window? _window;

    public ServerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.LinesAppended -= OnLinesAppended;
                _vm.CommandInserted -= OnCommandInserted;
            }
            _vm = DataContext as ServerViewModel;
            if (_vm is not null)
            {
                _vm.LinesAppended += OnLinesAppended;
                _vm.CommandInserted += OnCommandInserted;
            }
        };

        // окно закрывают с несохранёнными правками конфигурации — спросить (правки появляются только после
        // захода в раздел, так что подписки при первом показе достаточно)
        Loaded += (_, _) =>
        {
            if (_window is not null || Window.GetWindow(this) is not { } window) return;
            _window = window;
            window.Closing += (_, e) =>
            {
                if (!e.Cancel && _vm is not null && !_vm.Config.ConfirmClose()) e.Cancel = true;
            };
        };

        // пока была открыта «Конфигурация», строки копились без прокрутки — вернулись к консоли, догоняем
        ConsoleList.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) OnLinesAppended(force: false);
        };
    }

    // Автопрокрутка как в терминале: консоль «прилипает» к низу, пока пользователь сам не отмотал вверх.
    // Докручиваем после отрисовки — до неё высота списка ещё старая (особенно с переносом длинных строк).
    private bool _stick = true;
    private ScrollViewer? _scroll;

    private ScrollViewer? Scroll
    {
        get
        {
            if (_scroll is null && FindScroll(ConsoleList) is { } sv)
            {
                _scroll = sv;
                // изменилась не высота содержимого, а позиция — значит, крутил пользователь
                sv.ScrollChanged += (_, e) =>
                {
                    if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
                        _stick = sv.VerticalOffset >= sv.ScrollableHeight - 1;
                };
            }
            return _scroll;
        }
    }

    private void OnLinesAppended(bool force)
    {
        if (force) _stick = true;
        if (!_stick || Scroll is not { } scroll) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => scroll.ScrollToEnd());
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScroll(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private void CommandBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            // Tab дописывает команду и не уводит фокус из поля
            case Key.Tab:
                _vm.CompleteCommand();
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Escape when _vm.ShowSuggestions:
                _vm.HideSuggestions();
                e.Handled = true;
                break;
            // открыты подсказки — стрелки ходят по ним, Enter берёт выбранную; иначе стрелки — история
            case Key.Up when _vm.ShowSuggestions:
                _vm.MoveSuggestion(-1);
                SuggestionList.ScrollIntoView(_vm.SelectedSuggestion);
                e.Handled = true;
                break;
            case Key.Down when _vm.ShowSuggestions:
                _vm.MoveSuggestion(+1);
                SuggestionList.ScrollIntoView(_vm.SelectedSuggestion);
                e.Handled = true;
                break;
            case Key.Enter when _vm.ShowSuggestions && _vm.SelectedSuggestion is { } chosen:
                _vm.InsertCommand(chosen);
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Enter:
                if (_vm.SendCommandCommand.CanExecute(null)) _vm.SendCommandCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                _vm.HistoryStep(-1);
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Down:
                _vm.HistoryStep(+1);
                CommandBox.CaretIndex = CommandBox.Text.Length;
                e.Handled = true;
                break;
        }
    }

    // клик по подсказке — команда в поле, фокус остаётся в поле ввода
    private void SuggestionList_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || (e.OriginalSource as FrameworkElement)?.DataContext is not ServerCommand command) return;
        _vm.InsertCommand(command);
        FocusCommandBox();
    }

    // ушли из поля (не в список подсказок) — список прячется
    private void CommandBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!SuggestionList.IsKeyboardFocusWithin) _vm?.HideSuggestions();
    }

    private void OnCommandInserted(object? sender, EventArgs e) => FocusCommandBox();

    private void FocusCommandBox()
    {
        CommandBox.Focus();
        CommandBox.CaretIndex = CommandBox.Text.Length;
    }

    private void CopyLines_Click(object sender, RoutedEventArgs e)
    {
        var lines = ConsoleList.SelectedItems.Cast<ConsoleLineViewModel>()
            .OrderBy(l => ConsoleList.Items.IndexOf(l))
            .Select(l => l.Text);
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 0) Clipboard.SetText(text);
    }
}
