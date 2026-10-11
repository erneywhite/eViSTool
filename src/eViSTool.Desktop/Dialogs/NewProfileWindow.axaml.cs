using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Окно «Новый профиль» (DataContext — <see cref="NewProfileViewModel"/>): ответ — <see cref="NewProfileChoice"/>,
/// закрыли без выбора — null. Здесь только вид: закрыть окно с ответом модели и вернуть фокус в поле названия.
/// </summary>
public partial class NewProfileWindow : DialogWindow
{
    private NewProfileViewModel? _model;

    public NewProfileWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.Finished -= OnFinished;
            _model.FocusNameRequested -= FocusName;
        }
        _model = DataContext as NewProfileViewModel;
        if (_model is null) return;
        _model.Finished += OnFinished;
        _model.FocusNameRequested += FocusName;
    }

    private void OnFinished(NewProfileChoice choice) => Close(choice);

    private void FocusName() => NameBox.Focus();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
