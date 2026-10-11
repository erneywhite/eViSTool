using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Сервер в избранном игры: название, адрес, пароль (DataContext — <see cref="FavoriteServerViewModel"/>). Ответ —
/// <see cref="FavoriteServerChoice"/>, закрыли без сохранения — null. Здесь только вид: закрыть окно с ответом модели
/// и «Отмена».
/// </summary>
public partial class FavoriteServerWindow : DialogWindow
{
    private FavoriteServerViewModel? _model;

    public FavoriteServerWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.Finished -= OnFinished;
        _model = DataContext as FavoriteServerViewModel;
        if (_model is not null) _model.Finished += OnFinished;
    }

    private void OnFinished(FavoriteServerChoice choice) => Close(choice);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
