using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using eViSTool.App.ViewModels;

namespace eViSTool.App;

public partial class ServerView : UserControl
{
    private ServerViewModel? _vm;

    public ServerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.LinesAppended -= OnLinesAppended;
            _vm = DataContext as ServerViewModel;
            if (_vm is not null) _vm.LinesAppended += OnLinesAppended;
        };
    }

    // автопрокрутка, только если пользователь и так внизу (читает старое — не дёргаем)
    private void OnLinesAppended(bool force)
    {
        var scroll = FindScroll(ConsoleList);
        var atBottom = force || scroll is null || scroll.VerticalOffset >= scroll.ScrollableHeight - 2;
        if (atBottom && ConsoleList.Items.Count > 0)
            ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
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

    private void CopyLines_Click(object sender, RoutedEventArgs e)
    {
        var lines = ConsoleList.SelectedItems.Cast<ConsoleLineViewModel>()
            .OrderBy(l => ConsoleList.Items.IndexOf(l))
            .Select(l => l.Text);
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 0) Clipboard.SetText(text);
    }
}
