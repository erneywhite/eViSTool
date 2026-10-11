using Avalonia.Controls;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// «Вышла новая версия eViSTool» (немодальное, DataContext — <see cref="UpdateWindowViewModel"/>): слева версии и кнопки,
/// справа что изменилось за все пропущенные версии. Здесь только вид: закрыть окно, когда модель просит, и не дать
/// закрыть его посреди установки (Escape, крестик, закрытие главного окна).
/// </summary>
public partial class UpdateWindow : DialogWindow
{
    private UpdateWindowViewModel? _model;

    public UpdateWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _model?.Dispose(); // модель «О программе» живёт дольше окна — отписаться от неё
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.CloseRequested -= Close;
        _model = DataContext as UpdateWindowViewModel;
        if (_model is not null) _model.CloseRequested += Close;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_model is { CanClose: false }) e.Cancel = true; // посреди установки не закрываем: файлы программы как раз подменяются
        base.OnClosing(e);
    }
}
