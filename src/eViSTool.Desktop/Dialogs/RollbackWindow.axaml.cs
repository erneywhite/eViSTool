using Avalonia.Input;
using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>Окно выбора версии мода: сохранённые копии + релизы модбазы для ветки игры. Ответ — выбранный <see cref="VersionOption"/>.</summary>
public partial class RollbackWindow : DialogWindow
{
    private readonly RollbackViewModel _model;

    /// <summary>Для загрузчика разметки и предпросмотра: пустой список.</summary>
    public RollbackWindow() : this("", [])
    {
    }

    public RollbackWindow(string heading, IEnumerable<VersionOption> options)
    {
        InitializeComponent();
        _model = new RollbackViewModel(heading, options);
        _model.Accepted += option => Close(option);
        DataContext = _model;
        // Enter в таблице забирает сама таблица (у ListView в WPF — нет), и кнопка по умолчанию не срабатывает: перехватываем до неё
        List.AddHandler(KeyDownEvent, List_KeyDown, RoutingStrategies.Tunnel);
    }

    private void List_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        _model.InstallCommand.Execute(null);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
