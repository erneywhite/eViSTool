using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// «Ошибки модов» профиля (немодальное, DataContext — <see cref="ModErrorsViewModel"/>): сводка, моды с примером
/// ошибки, выключить, страница мода, скрыть, скопировать. Вся логика — в модели; здесь только вид и закрытие, когда
/// модель просит (открыли страницу мода, скрыли отчёт).
/// </summary>
public partial class ModErrorsWindow : DialogWindow
{
    private ModErrorsViewModel? _model;

    public ModErrorsWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.CloseRequested -= Close;
        _model = DataContext as ModErrorsViewModel;
        if (_model is not null) _model.CloseRequested += Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_model is not null) _model.CloseRequested -= Close;
        base.OnClosed(e);
    }

    // окно немодальное: «Закрыть» и Escape (в DialogWindow) закрывают его без ответа
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
