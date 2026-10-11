using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Окно «Найти сервер» (DataContext — <see cref="FindServerViewModel"/>): общий список серверов и избранное игры.
/// Немодальное. Здесь только вид: закрыться, когда модель просит (игру запустили с сервером из списка).
/// </summary>
public partial class FindServerWindow : DialogWindow
{
    private FindServerViewModel? _model;

    public FindServerWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.CloseRequested -= OnCloseRequested;
        _model = DataContext as FindServerViewModel;
        if (_model is not null) _model.CloseRequested += OnCloseRequested;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_model is not null) _model.CloseRequested -= OnCloseRequested;
        base.OnClosed(e);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
