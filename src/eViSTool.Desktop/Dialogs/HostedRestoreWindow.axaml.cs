using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Вопрос перед восстановлением копии мира на сервер у хостера (DataContext — <see cref="HostedRestoreViewModel"/>).
/// Ответ — <see cref="HostedRestoreChoice"/>, закрыли или «Отмена» — null. Здесь только вид: закрыть окно с ответом модели.
/// </summary>
public partial class HostedRestoreWindow : DialogWindow
{
    private HostedRestoreViewModel? _model;

    public HostedRestoreWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.Finished -= OnFinished;
        _model = DataContext as HostedRestoreViewModel;
        if (_model is not null) _model.Finished += OnFinished;
    }

    private void OnFinished(HostedRestoreChoice choice) => Close(choice);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
