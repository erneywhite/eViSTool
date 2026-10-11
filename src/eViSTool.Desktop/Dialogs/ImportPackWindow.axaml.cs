using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>Предпросмотр импорта модпака: что поставится, обновится или откатится, режим и настройки модов. Ответ — <see cref="PackImportChoice"/> или ничего (отмена).</summary>
public partial class ImportPackWindow : DialogWindow
{
    public ImportPackWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    public ImportPackWindow(ImportPackViewModel model) : this()
    {
        DataContext = model;
    }

    // Enter в таблице и на переключателях Avalonia оставляет им самим (шаг по таблице, «нажатие» переключателя), и до
    // кнопки по умолчанию он не доходит. В окне на WPF он шёл на «Импортировать» — здесь так же. На обычной кнопке Enter
    // нажимает её саму, тут ничего не меняем.
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Handled || e.KeyModifiers != KeyModifiers.None || e.Source is not Visual source) return;
        if (source is not ToggleButton && source.FindAncestorOfType<DataGrid>(includeSelf: true) is null) return;
        e.Handled = true;
        Import();
    }

    private void Import() => Close((DataContext as ImportPackViewModel)?.Choice);

    private void Import_Click(object? sender, RoutedEventArgs e) => Import();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
