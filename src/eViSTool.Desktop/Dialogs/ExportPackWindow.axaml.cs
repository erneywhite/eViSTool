using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// Параметры сборки модпака. Ответ окна — <c>PackExportOptions</c> (закрыли без «Собрать» — ничего). Путь к файлу
/// спрашивается отдельно, после этого окна. Поля и правила — в <see cref="ExportPackViewModel"/>, здесь только вид:
/// закрытие и возврат фокуса в название.
/// </summary>
public partial class ExportPackWindow : DialogWindow
{
    /// <summary>Для загрузчика разметки; окно показывают с моделью.</summary>
    public ExportPackWindow() => InitializeComponent();

    public ExportPackWindow(ExportPackViewModel model) : this() => DataContext = model;

    private ExportPackViewModel Model => (ExportPackViewModel)DataContext!;

    private void Build_Click(object? sender, RoutedEventArgs e)
    {
        if (Model.Build() is { } options) Close(options);
        else NameBox.Focus(); // без названия пак не собрать: окно остаётся, фокус — в поле
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
