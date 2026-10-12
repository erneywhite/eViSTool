using System.Runtime.InteropServices;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Вставить код набора модов. Разбор — в <see cref="ModCodeViewModel"/> (DataContext), здесь — буфер обмена и ответ
/// окна: моды из кода по «Дальше».
/// </summary>
public partial class ModCodeWindow : DialogWindow
{
    public ModCodeWindow() => InitializeComponent();

    private ModCodeViewModel? Model => DataContext as ModCodeViewModel;

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || Clipboard is not { } clipboard) return;
        try
        {
            if (await clipboard.TryGetTextAsync() is { } text) model.Text = text.Trim();
        }
        catch (Exception ex) when (ex is TimeoutException or ExternalException)
        {
            // буфер занят другой программой — можно вставить Ctrl+V
        }
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (Model?.Set is { Entries.Count: > 0 } set) Close(set);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
