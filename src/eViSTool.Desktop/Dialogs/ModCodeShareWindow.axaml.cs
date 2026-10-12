using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>Готовый код набора: блок с кодом и копирование щелчком или кнопкой.</summary>
public partial class ModCodeShareWindow : DialogWindow
{
    public ModCodeShareWindow() => InitializeComponent();

    private ModCodeShare? Model => DataContext as ModCodeShare;

    private void Code_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _ = CopyAsync();
    }

    private void Copy_Click(object? sender, RoutedEventArgs e) => _ = CopyAsync();

    private async Task CopyAsync()
    {
        if (Model is not { } model || Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(model.Code);
            model.MarkCopied();
        }
        catch (Exception ex) when (ex is TimeoutException or ExternalException)
        {
            // буфер занят другой программой — код можно выделить и скопировать руками
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
