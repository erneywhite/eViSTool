using Avalonia.Interactivity;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Dialogs;

/// <summary>
/// «Установить также в»: мод ставится в текущий профиль и, по галочкам, в другие (сервер ↔ клиент). Ответ —
/// <see cref="AlsoInstallAnswer"/> («Установить») или ничего (Отмена, Escape); строки и ответ собирает
/// <see cref="AlsoInstallViewModel"/> из DataContext.
/// </summary>
public partial class AlsoInstallWindow : DialogWindow
{
    public AlsoInstallWindow() => InitializeComponent();

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AlsoInstallViewModel model) Close(model.Confirm());
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
