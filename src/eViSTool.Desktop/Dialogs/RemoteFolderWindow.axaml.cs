using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Выбор папки на машине с сервером (DataContext — <see cref="RemoteFolderViewModel"/>): ответ — полный путь папки на
/// той машине, закрыли без выбора — null. Вся логика в модели; здесь только вид: открылось окно — открыть стартовую
/// папку, модель выбрала — закрыть окно с её ответом.
/// </summary>
public partial class RemoteFolderWindow : DialogWindow
{
    private RemoteFolderViewModel? _model;

    public RemoteFolderWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.Chosen -= OnChosen;
        _model = DataContext as RemoteFolderViewModel;
        if (_model is not null) _model.Chosen += OnChosen;
    }

    // папки читает агент по сети — когда окно уже на экране (в окне на WPF — по Loaded)
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_model is { } model) await model.LoadAsync();
    }

    private void OnChosen(string path) => Close(path);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
