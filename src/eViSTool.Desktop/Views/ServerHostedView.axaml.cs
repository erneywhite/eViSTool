using Avalonia;
using Avalonia.Controls;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>
/// Раздел «Сервер» у сервера у хостера: здесь — только прокрутка консоли (как у своей: прилипает к низу, пока
/// пользователь не отмотал вверх). Опрос лога включает и выключает модель по открытию раздела.
/// </summary>
public partial class ServerHostedView : UserControl
{
    private HostedServerViewModel? _vm;
    private readonly ConsoleAutoScroll _scroll;

    public ServerHostedView()
    {
        InitializeComponent();
        _scroll = new ConsoleAutoScroll(ConsoleList);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.LinesAppended -= _scroll.Appended;
        _vm = DataContext as HostedServerViewModel;
        if (_vm is not null) _vm.LinesAppended += _scroll.Appended;
    }

    // пока раздел был закрыт, строки копились без прокрутки — вернулись, догоняем
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>()) _scroll?.Appended(force: false);
    }
}
