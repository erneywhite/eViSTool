using Avalonia.Controls;
using Avalonia.Input;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>Вкладка «Копии» раздела «Сервер» у сервера у хостера. DataContext — HostedBackupsViewModel, его задаёт ServerView.</summary>
public partial class ServerHostedBackupsView : UserControl
{
    public ServerHostedBackupsView() => InitializeComponent();

    // папка для копий применяется, когда поле отпустили (привязка LostFocus) или нажали Enter: на каждую букву проверять путь незачем
    private void BackupDirBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not HostedBackupsViewModel model) return;
        model.CommitBackupDir(BackupDirBox.Text);
    }
}
