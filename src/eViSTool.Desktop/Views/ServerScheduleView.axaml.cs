using Avalonia.Controls;
using Avalonia.Input;
using eViSTool.Presentation.ViewModels;

namespace eViSTool.Desktop.Views;

/// <summary>Вкладка «Расписание» раздела «Сервер». DataContext — ServerScheduleViewModel.</summary>
public partial class ServerScheduleView : UserControl
{
    public ServerScheduleView() => InitializeComponent();

    // папка для копий применяется, когда поле отпустили (привязка LostFocus) или нажали Enter: на каждую букву проверять путь незачем
    private void BackupDirBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ServerScheduleViewModel model) return;
        model.CommitBackupDir(BackupDirBox.Text);
    }
}
