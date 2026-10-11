using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Дублирование роли сервера (DataContext — <see cref="DuplicateRoleViewModel"/>): код и название копии. Ответ —
/// <see cref="RoleCopyChoice"/>, закрыли без копии — null. Здесь только вид: закрыть окно с ответом модели.
/// </summary>
public partial class DuplicateRoleWindow : DialogWindow
{
    private DuplicateRoleViewModel? _model;

    public DuplicateRoleWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.Finished -= OnFinished;
        _model = DataContext as DuplicateRoleViewModel;
        if (_model is not null) _model.Finished += OnFinished;
    }

    private void OnFinished(RoleCopyChoice choice) => Close(choice);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
