using Avalonia.Input;
using Avalonia.Interactivity;
using eViSTool.Core.Server;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Окно «Команды сервера» (DataContext — <see cref="ServerCommandsViewModel"/>): все команды сервера с поиском. Ответ —
/// выбранная <see cref="ServerCommand"/>, закрыли без выбора — null. Здесь только вид: закрыться с ответом модели, перейти
/// из поиска в список стрелкой вниз и вставить команду по Enter в списке. Модель, подписанную на сервер, освобождает тот,
/// кто её создал (<see cref="ServerCommandsViewModel.Dispose"/>).
/// </summary>
public partial class ServerCommandsWindow : DialogWindow
{
    private ServerCommandsViewModel? _model;

    public ServerCommandsWindow()
    {
        InitializeComponent();
        SearchBox.AddHandler(KeyDownEvent, SearchBox_KeyDown, RoutingStrategies.Tunnel);
        List.AddHandler(KeyDownEvent, List_KeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null) _model.Accepted -= OnAccepted;
        _model = DataContext as ServerCommandsViewModel;
        if (_model is not null) _model.Accepted += OnAccepted;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_model is not null) _model.Accepted -= OnAccepted;
        base.OnClosed(e);
    }

    private void OnAccepted(ServerCommand command) => Close(command);

    // из поиска: ↓ — в список, на первую строку (Enter в поиске доходит до «Вставить» сам: первая подходящая)
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || e.KeyModifiers != KeyModifiers.None || List.ItemCount == 0) return;
        List.SelectedIndex = 0;
        if (List.ContainerFromIndex(0) is { } first) first.Focus();
        else List.Focus();
        e.Handled = true;
    }

    // Enter в списке забирает сам список (в WPF он шёл на «Вставить»): перехватываем до него
    private void List_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || _model is null) return;
        e.Handled = true;
        _model.InsertCommand.Execute(null);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
