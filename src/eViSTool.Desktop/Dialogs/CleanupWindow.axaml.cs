using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// «Уборка диска» профиля — из настроек профиля (DataContext — <see cref="CleanupViewModel"/>). Ответа нет. Логика в
/// модели, здесь — вид: окно открылось — модель ищет, что убрать, и следит за игрой и сервером профиля; закрылось —
/// перестаёт следить.
/// </summary>
public partial class CleanupWindow : DialogWindow
{
    private CleanupViewModel? _model;

    public CleanupWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _model = DataContext as CleanupViewModel;
    }

    // папки бывают большими — модель обходит их в фоне, когда окно уже на экране
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_model is { } model) await model.StartAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _model?.Stop();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
